using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace Systenclin.Core;

/// <summary>Um arquivo que faz parte de um grupo de duplicados.</summary>
public sealed record DuplicateFile(string Path, long Size, DateTime LastWriteTimeUtc);

/// <summary>Arquivos de conteúdo idêntico (mesmo tamanho e mesmo SHA-256).</summary>
public sealed record DuplicateGroup(long Size, IReadOnlyList<DuplicateFile> Files);

/// <summary>
/// Encontra arquivos duplicados por conteúdo e manda os escolhidos para a Lixeira.
/// Duas fases: agrupar por tamanho (barato) e só então comparar hash (caro).
/// </summary>
public static class DuplicateFinder
{
    private const int HashBufferSize = 1024 * 1024;

    // IgnoreInaccessible: pasta sem permissão é pulada em vez de derrubar a varredura.
    // AttributesToSkip:
    //   System       — arquivos gerenciados pelo SO, fora do escopo desta limpeza.
    //   ReparsePoint — nunca atravessar junções/links (segurança: evita agir fora
    //                  da árvore varrida e contar o mesmo arquivo duas vezes).
    //   Hidden NÃO é pulado de propósito: arquivos ocultos do usuário são candidatos válidos.
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    /// <summary>
    /// Varre <paramref name="root"/> inteiro e devolve os grupos com 2 ou mais
    /// arquivos idênticos, do maior espaço recuperável para o menor. Raiz
    /// inexistente devolve lista vazia; cancelamento lança.
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> Find(
        string root,
        int minimumFileSizeBytes = 4096,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<DuplicateGroup>();

        // Fase 1: candidatos por tamanho. Tamanhos diferentes nunca são duplicados,
        // então esse filtro sai quase de graça.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<DuplicateFile>>();

        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Options))
            {
                ct.ThrowIfCancellationRequested();

                string path;
                long size;
                DateTime lastWrite;
                try
                {
                    path = file.FullName;
                    size = file.Length;
                    lastWrite = file.LastWriteTimeUtc;
                }
                catch (IOException)
                {
                    continue; // sumiu entre a enumeração e a leitura.
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (size < minimumFileSizeBytes)
                    continue;

                // Mesmo caminho, outra grafia: conta uma vez só.
                if (!seen.Add(path))
                    continue;

                if (!bySize.TryGetValue(size, out var list))
                {
                    list = new List<DuplicateFile>();
                    bySize[size] = list;
                }

                list.Add(new DuplicateFile(path, size, lastWrite));
            }
        }
        catch (IOException)
        {
            // A raiz sumiu no meio da varredura: fica o que já foi lido.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }

        var candidates = new List<(long Size, List<DuplicateFile> Files)>();
        int candidateCount = 0;

        foreach (var (size, files) in bySize)
        {
            if (files.Count < 2)
                continue;

            candidates.Add((size, files));
            candidateCount += files.Count;
        }

        progress?.Report($"Lendo: {candidateCount} arquivos candidatos…");

        // Fase 2: hash só dos candidatos.
        var byHash = new Dictionary<(long Size, string Hash), List<DuplicateFile>>();
        int done = 0;

        foreach (var (size, files) in candidates)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report($"Calculando hash: {done}/{candidateCount}");

                string? hash = TryHash(file.Path, ct);
                if (hash is null)
                    continue; // ilegível agora: fica fora, sem lançar.

                var key = (size, hash);
                if (!byHash.TryGetValue(key, out var list))
                {
                    list = new List<DuplicateFile>();
                    byHash[key] = list;
                }

                list.Add(file);
            }
        }

        var groups = new List<DuplicateGroup>();

        foreach (var (key, files) in byHash)
        {
            if (files.Count < 2)
                continue;

            // Mais antigo primeiro: é o candidato natural a "original" que fica.
            files.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            // AsReadOnly: a lista do grupo não muda mais depois daqui.
            groups.Add(new DuplicateGroup(key.Size, files.AsReadOnly()));
        }

        // Maior espaço recuperável primeiro: Size × (arquivos - 1).
        groups.Sort(static (a, b) =>
            (b.Size * (b.Files.Count - 1)).CompareTo(a.Size * (a.Files.Count - 1)));

        return groups;
    }

    /// <summary>
    /// Manda os arquivos para a Lixeira, um a um. Devolve quantos foram reciclados.
    /// Falha individual não interrompe as demais, e nunca há queda para exclusão
    /// permanente: o que não puder ser reciclado simplesmente fica onde está.
    /// Arquivo atrás de vínculo (junção/symlink) é recusado antes de tentar, com
    /// o motivo entregue a <paramref name="onRefused"/> quando informado.
    /// </summary>
    public static int RecycleFiles(
        IReadOnlyList<string> paths,
        CancellationToken ct = default,
        Action<string, string>? onRefused = null)
    {
        if (paths is null || paths.Count == 0)
            return 0;

        int recycled = 0;
        var verifiedAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            ct.ThrowIfCancellationRequested();

            // Segunda linha de defesa: reciclar através de um vínculo agiria fora
            // da árvore pretendida. Recusa item a item, sem parar o lote.
            if (FileDeleter.IsUnsafeToRecycle(path, verifiedAncestors, out var reason))
            {
                onRefused?.Invoke(path, reason);
                continue;
            }

            if (ShellRecycle.Send(path) == RecycleOutcome.Deleted)
                recycled++;
        }

        return recycled;
    }

    /// <summary>
    /// SHA-256 em streaming, sem carregar o arquivo inteiro na memória.
    /// Leitura tolerante: outro processo pode estar lendo ou renomeando o arquivo
    /// enquanto hasheamos. Ilegível devolve null; cancelamento lança.
    /// </summary>
    private static string? TryHash(string path, CancellationToken ct)
    {
        byte[]? buffer = null;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                HashBufferSize,
                FileOptions.SequentialScan);

            using var sha = SHA256.Create();
            buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);

            int read;
            while ((read = stream.Read(buffer, 0, HashBufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
