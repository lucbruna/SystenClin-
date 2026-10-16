using System.IO;
using System.Threading;

namespace Systenclin.Core;

/// <summary>
/// Um arquivo acima do tamanho mínimo encontrado na varredura. LastWriteTimeUtc
/// fica em UTC: a conversão para o fuso local é responsabilidade da UI.
/// </summary>
public sealed record LargeFileItem(string Path, long Size, DateTime LastWriteTimeUtc);

/// <summary>
/// Encontra os maiores arquivos de uma pasta e manda os escolhidos para a
/// Lixeira. Nenhuma API pública apaga de forma permanente.
/// </summary>
public static class LargeFileFinder
{
    /// <summary>Tamanho mínimo padrão: 100 MB.</summary>
    public const long DefaultMinSizeBytes = 100L * 1024 * 1024;

    /// <summary>Só os maiores interessam à UI — o excedente é descartado.</summary>
    private const int MaxItems = 2000;

    /// <summary>
    /// Varre <paramref name="root"/> recursivamente com pilha manual: pasta sem
    /// permissão, que sumiu ou ilegível derruba só a própria subárvore, nunca a
    /// varredura inteira. Junções/symlinks nunca são atravessados (evita loop e
    /// evita contar o mesmo arquivo duas vezes). Devolve os arquivos com
    /// Size &gt;= <paramref name="minSizeBytes"/>, do maior para o menor (no
    /// máximo os 2000 maiores). Raiz nula, vazia ou inexistente devolve lista
    /// vazia. Cancelamento lança OperationCanceledException — a UI trata;
    /// qualquer outra falha inesperada devolve o parcial já lido, sem lançar.
    /// </summary>
    public static IReadOnlyList<LargeFileItem> Find(
        string root,
        long minSizeBytes = DefaultMinSizeBytes,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<LargeFileItem>();

        var found = new List<LargeFileItem>();
        var pending = new Stack<string>();
        pending.Push(root);

        try
        {
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                string dir = pending.Pop();
                progress?.Report(dir);

                try
                {
                    var info = new DirectoryInfo(dir);

                    foreach (var file in info.EnumerateFiles())
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            long size = file.Length;
                            if (size >= minSizeBytes)
                                found.Add(new LargeFileItem(file.FullName, size, file.LastWriteTimeUtc));
                        }
                        catch (IOException)
                        {
                            continue; // sumiu ou perdeu o acesso entre a enumeração e a leitura.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            continue;
                        }
                    }

                    foreach (var sub in info.EnumerateDirectories())
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            // ReparsePoint: junção/symlink fica fora da pilha.
                            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                                continue;

                            pending.Push(sub.FullName);
                        }
                        catch (IOException)
                        {
                            continue; // a subpasta sumiu no meio: segue adiante.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            continue;
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // A pasta sumiu entre o push e a varredura: segue adiante.
                }
                catch (IOException)
                {
                    // Falha de enumeração (também cobre DirectoryNotFoundException).
                }
                catch (UnauthorizedAccessException)
                {
                    // Pasta sem permissão: pulada por inteiro.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // cancelamento é o único erro que a UI trata como fluxo normal.
        }
        catch
        {
            // Exceção inesperada: devolve o que já foi lido.
        }

        found.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        if (found.Count > MaxItems)
            found.RemoveRange(MaxItems, found.Count - MaxItems);

        return found.AsReadOnly();
    }

    /// <summary>
    /// Manda os arquivos para a Lixeira, um a um. Devolve quantos foram
    /// reciclados. Falha individual não interrompe as demais, e nunca há queda
    /// para exclusão permanente: o que não puder ser reciclado simplesmente fica
    /// onde está. Cancelamento para cedo sem lançar. Arquivo atrás de vínculo
    /// (junção/symlink) é recusado antes de tentar, com o motivo entregue a
    /// <paramref name="onRefused"/> quando informado.
    /// </summary>
    public static int RecycleItems(
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

            if (ct.IsCancellationRequested)
                break; // devolve o que já foi reciclado.

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
}
