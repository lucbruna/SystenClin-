using System.IO;

namespace Systenclin.Core;

/// <summary>
/// Tamanho recursivo de uma subpasta imediata da raiz analisada. Path é o
/// caminho completo; Name é só o nome da pasta.
/// </summary>
public sealed record FolderEntry(string Path, string Name, long Bytes, int FileCount);

/// <summary>
/// Resultado de <see cref="FolderAnalyzer.Analyze"/>: totais da raiz
/// (subpastas + arquivos soltos) e o detalhamento por subpasta imediata,
/// da maior para a menor.
/// </summary>
public sealed record FolderAnalysis(
    string Root,
    long TotalBytes,
    int TotalFiles,
    long LooseBytes,
    int LooseFiles,
    IReadOnlyList<FolderEntry> Children);

/// <summary>
/// Mede o que ocupa espaço dentro de uma pasta: o tamanho recursivo de cada
/// subpasta imediata e os arquivos soltos na raiz (não recursivo).
///
/// Mesmas regras de travessia segura do LargeFileFinder:
/// - junções/links simbólicos (ReparsePoint) nunca são atravessados — a de
///   primeiro nível nem entra em Children, senão o tamanho medido não seria
///   o que os arquivos realmente ocupam ali;
/// - pasta sem permissão, ilegível ou que sumiu derruba só a própria árvore;
/// - cancelamento lança OperationCanceledException; qualquer outra falha
///   inesperada devolve o parcial já medido, sem lançar.
/// </summary>
public static class FolderAnalyzer
{
    /// <summary>
    /// Analisa <paramref name="root"/>. Raiz nula, vazia, inexistente ou sem
    /// permissão devolve resultado zerado, sem lançar. O progresso recebe o
    /// caminho completo de cada subpasta de primeiro nível quando sua medição
    /// começa.
    /// </summary>
    public static FolderAnalysis Analyze(
        string root,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Empty(string.Empty);

        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(root);
        }
        catch
        {
            return Empty(root); // caminho malformado: devolve vazio.
        }

        if (!Directory.Exists(rootPath))
            return Empty(rootPath);

        long looseBytes = 0;
        int looseFiles = 0;
        var children = new List<FolderEntry>();

        try
        {
            (looseBytes, looseFiles) = MeasureLooseFiles(rootPath, ct);

            foreach (var sub in new DirectoryInfo(rootPath).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // Junção/symlink: fora da medição — atravessar contaria
                    // bytes que não pertencem a esta árvore.
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    progress?.Report(sub.FullName);

                    var (bytes, files) = MeasureTree(sub.FullName, ct);
                    children.Add(new FolderEntry(sub.FullName, sub.Name, bytes, files));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (DirectoryNotFoundException)
                {
                    // A subpasta sumiu entre a enumeração e a medição.
                }
                catch (UnauthorizedAccessException)
                {
                    // Sem permissão: fica fora do resultado.
                }
                catch (IOException)
                {
                    // Falha de enumeração/leitura: pula só esta subpasta.
                }
                catch
                {
                    // Inesperado aqui não derruba as demais subpastas.
                }
            }

            children.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        }
        catch (OperationCanceledException)
        {
            throw; // cancelamento é o único erro que a UI trata como fluxo normal.
        }
        catch
        {
            // Inesperado: devolve o parcial já medido.
        }

        long totalBytes = looseBytes;
        int totalFiles = looseFiles;
        foreach (var child in children)
        {
            totalBytes += child.Bytes;
            totalFiles += child.FileCount;
        }

        return new FolderAnalysis(
            rootPath, totalBytes, totalFiles, looseBytes, looseFiles, children.AsReadOnly());
    }

    private static FolderAnalysis Empty(string root) =>
        new(root, 0, 0, 0, 0, Array.Empty<FolderEntry>());

    /// <summary>
    /// Arquivos soltos diretamente na raiz: soma de tamanhos sem descer em
    /// subpastas. Falha individual (arquivo sumiu, sem permissão) só o pula.
    /// </summary>
    private static (long Bytes, int Files) MeasureLooseFiles(string root, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0;

        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    bytes += file.Length;
                    files++;
                }
                catch (DirectoryNotFoundException)
                {
                    // Sumiu entre a enumeração e a leitura.
                }
                catch (UnauthorizedAccessException)
                {
                    // Sem permissão de leitura: não conta.
                }
                catch (IOException)
                {
                    // Ilegível: não conta.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            // A raiz sumiu.
        }
        catch (UnauthorizedAccessException)
        {
            // Raiz sem permissão: nada a contar.
        }
        catch (IOException)
        {
            // Falha de enumeração: devolve o que já somou.
        }
        catch
        {
            // Inesperado: devolve o que já somou.
        }

        return (bytes, files);
    }

    /// <summary>
    /// Medição recursiva de uma árvore com pilha manual: pasta sem permissão,
    /// que sumiu ou ilegível derruba só a própria subárvore. Junções/symlinks
    /// nunca entram na pilha. Só soma tamanhos — nenhuma lista é materializada.
    /// </summary>
    private static (long Bytes, int Files) MeasureTree(string root, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        try
        {
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                string dir = pending.Pop();

                try
                {
                    var info = new DirectoryInfo(dir);

                    foreach (var file in info.EnumerateFiles())
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            bytes += file.Length;
                            files++;
                        }
                        catch (DirectoryNotFoundException)
                        {
                            // Sumiu entre a enumeração e a leitura.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Sem permissão de leitura: não conta.
                        }
                        catch (IOException)
                        {
                            // Ilegível: não conta.
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
                        catch (DirectoryNotFoundException)
                        {
                            // Sumiu no meio: segue adiante.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Sem permissão: segue adiante.
                        }
                        catch (IOException)
                        {
                            // Ilegível: segue adiante.
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // A pasta sumiu entre o push e a varredura: segue adiante.
                }
                catch (UnauthorizedAccessException)
                {
                    // Pasta sem permissão: pulada por inteiro.
                }
                catch (IOException)
                {
                    // Falha de enumeração: pulada por inteiro.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // cancelamento é o único erro que a UI trata como fluxo normal.
        }
        catch
        {
            // Inesperado: devolve o parcial já somado.
        }

        return (bytes, files);
    }
}
