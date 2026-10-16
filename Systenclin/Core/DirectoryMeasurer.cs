using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>
/// Mede o tamanho de uma árvore de diretórios e conta arquivos.
///
/// Regras deliberadas:
/// - Nunca segue junções nem links simbólicos. Um junction em %TEMP% apontando
///   para C:\Windows faria o limpador apagar o Windows. Isso já aconteceu com
///   produtos reais; aqui é tratado como caso de segurança.
/// - Um diretório que não pode ser lido não interrompe a varredura; ele é
///   ignorado e o motivo fica disponível.
/// - Respeita cancelamento: a varredura pode ser abortada a qualquer momento.
/// </summary>
public static class DirectoryMeasurer
{
    private const int MaxDepth = 24;

    public sealed record Result(long SizeBytes, int FileCount, int SkippedCount, bool Truncated);

    public static Result Measure(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        long total = 0;
        int files = 0;
        int skipped = 0;
        bool truncated = false;

        // Raiz que já é junção/symlink: não atravessa. A remoção apagaria só o
        // link, e medir a árvore apontada contaria bytes que não sairão; por
        // isso marca truncado e devolve vazio para o alvo não entrar na lista.
        bool rootIsReparse;
        try
        {
            rootIsReparse = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // Sem leitura dos atributos, o mais seguro é não atravessar.
            rootIsReparse = true;
        }

        if (rootIsReparse)
        {
            return new Result(0, 0, 0, Truncated: true);
        }

        var stack = new Stack<(string Dir, int Depth, bool IsReparse)>();
        stack.Push((path, 0, false));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (dir, depth, isReparse) = stack.Pop();

            if (depth > MaxDepth)
            {
                truncated = true;
                continue;
            }

            // Junção, symlink ou ponto de montagem: nunca atravessa. A decisão
            // vem dos atributos em cache da enumeração, sem syscall extra aqui.
            if (isReparse)
            {
                skipped++;
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException) { skipped++; continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { skipped++; continue; }

            try
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        if (entry is DirectoryInfo sub)
                        {
                            bool subIsReparse;
                            try
                            {
                                // Atributos já vêm em cache da enumeração.
                                subIsReparse = (sub.Attributes & FileAttributes.ReparsePoint) != 0;
                            }
                            catch
                            {
                                // Sem leitura dos atributos, o mais seguro é não atravessar.
                                subIsReparse = true;
                            }

                            stack.Push((sub.FullName, depth + 1, subIsReparse));
                        }
                        else if (entry is FileInfo file)
                        {
                            total += file.Length;
                            files++;
                        }
                    }
                    catch (UnauthorizedAccessException) { skipped++; }
                    catch (FileNotFoundException) { }
                    catch (IOException) { skipped++; }
                }
            }
            catch (UnauthorizedAccessException) { skipped++; }
            catch (IOException) { skipped++; }
        }

        return new Result(total, files, skipped, truncated);
    }
}
