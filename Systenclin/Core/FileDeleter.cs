using System.IO;

namespace Systenclin.Core;

/// <summary>Apaga uma árvore de diretórios de forma resiliente. Nunca lança por falha individual.</summary>
public static class FileDeleter
{
    public sealed record Outcome(long BytesFreed, int FilesDeleted, int FilesSkipped, int DirsLeftBehind);

    // IgnoreInaccessible: pasta sem permissão é pulada em vez de derrubar a limpeza.
    // RecurseSubdirectories=false: a varredura é iterativa, com pilha própria.
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Apaga os arquivos e depois as pastas, de baixo para cima, sem recursão.
    /// Junção ou symlink nunca é atravessado: só o vínculo é removido.
    /// Cancelamento para cedo e devolve o que já foi apagado, sem lançar exceção.
    /// </summary>
    public static Outcome DeleteTree(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new Outcome(0, 0, 0, 0);

        if (ct.IsCancellationRequested)
            return new Outcome(0, 0, 0, 0);

        long bytesFreed = 0;
        int filesDeleted = 0;
        int filesSkipped = 0;
        int dirsLeftBehind = 0;
        int filesSeen = 0;

        // Cada pasta entra duas vezes: primeiro para esvaziar, depois (ReadyToRemove)
        // para a remoção — assim os filhos saem antes dos pais, sem recursão.
        var stack = new Stack<(DirectoryInfo Dir, bool ReadyToRemove)>();
        stack.Push((new DirectoryInfo(path), false));

        while (stack.Count > 0)
        {
            var (dir, readyToRemove) = stack.Pop();

            if (readyToRemove)
            {
                if (ct.IsCancellationRequested)
                    return new Outcome(bytesFreed, filesDeleted, filesSkipped, dirsLeftBehind);

                try
                {
                    Directory.Delete(dir.FullName, recursive: false);
                }
                catch
                {
                    // Pasta não vazia, em uso ou sem permissão: fica para trás.
                    dirsLeftBehind++;
                }

                continue;
            }

            stack.Push((dir, true));

            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", Options))
                {
                    if (entry is DirectoryInfo child)
                    {
                        if (IsReparsePoint(child))
                        {
                            if (ct.IsCancellationRequested)
                                return new Outcome(bytesFreed, filesDeleted, filesSkipped, dirsLeftBehind);

                            // Remove o vínculo, nunca o conteúdo apontado.
                            try
                            {
                                Directory.Delete(child.FullName, recursive: false);
                            }
                            catch
                            {
                                dirsLeftBehind++;
                            }
                        }
                        else
                        {
                            stack.Push((child, false));
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        filesSeen++;
                        if (filesSeen % 64 == 0 && ct.IsCancellationRequested)
                            return new Outcome(bytesFreed, filesDeleted, filesSkipped, dirsLeftBehind);

                        // O tamanho vem da enumeração; se já não está disponível, conta 0.
                        long size = 0;
                        try { size = file.Length; } catch { }

                        if (TryDeleteFile(file.FullName))
                        {
                            bytesFreed += size;
                            filesDeleted++;
                        }
                        else
                        {
                            filesSkipped++;
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Sem lista de filhos: a remoção da pasta decide o resultado.
            }
            catch (IOException)
            {
                // Pasta sumiu no meio do caminho: idem.
            }
        }

        return new Outcome(bytesFreed, filesDeleted, filesSkipped, dirsLeftBehind);
    }

    /// <summary>
    /// Apaga um arquivo. IOException (arquivo em uso) ganha uma única segunda
    /// tentativa após uma pausa curta; acesso negado não é repetido.
    /// </summary>
    private static bool TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (IOException)
        {
            // Uma segunda chance: antivírus ou indexador soltam o arquivo em milissegundos.
        }
        catch
        {
            return false;
        }

        try
        {
            Thread.Sleep(50);
            File.Delete(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Vínculo lido dos atributos da enumeração; sem atributos, não atravessa.</summary>
    private static bool IsReparsePoint(DirectoryInfo dir)
    {
        try
        {
            return (dir.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}
