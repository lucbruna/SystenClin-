using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Systenclin.Core;

/// <summary>Onde o atalho .lnk foi encontrado.</summary>
public enum ShortcutLocation
{
    UserDesktop,
    CommonDesktop,
    UserStartMenu,
    CommonStartMenu,
}

/// <summary>
/// Um atalho .lnk cujo destino não existe mais. Target é o TargetPath cru lido do
/// atalho (variáveis de ambiente ficam como estão, sem expandir).
/// </summary>
public sealed record ShortcutItem(string Path, string Name, ShortcutLocation Location, string? Target);

/// <summary>
/// Encontra atalhos .lnk quebrados (destino inexistente) no Desktop e no Menu
/// Iniciar e manda os escolhidos para a Lixeira. Nenhuma API pública lança.
/// </summary>
public static class ShortcutFixer
{
    // IgnoreInaccessible: pasta sem permissão é pulada em vez de derrubar a varredura.
    // ReparsePoint: junção nunca é atravessada. System: item do sistema fica fora.
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    /// <summary>
    /// Varre Desktop e Menu Iniciar (do usuário e de todos os usuários) e devolve os
    /// .lnk cujo destino não resolve mais para arquivo nem pasta existentes, ordenados
    /// por local e depois nome. Atalho sem destino legível não é julgado (falso
    /// positivo é pior que falso negativo). Cancelamento para cedo e devolve o que
    /// já tem; nunca lança.
    /// </summary>
    public static IReadOnlyList<ShortcutItem> FindBroken(CancellationToken ct = default)
    {
        List<ShortcutItem>? found = null;

        try
        {
            // O Windows Script Host é COM e pode exigir apartamento STA; Task.Run
            // roda em MTA. Por isso a varredura inteira (enumeração + resolução)
            // roda numa thread STA dedicada, com resultado capturado e Join.
            var worker = new Thread(() =>
            {
                try
                {
                    found = Scan(ct);
                }
                catch
                {
                    // Exceção aqui derrubaria o processo: engole e mantém o parcial.
                }
            })
            {
                IsBackground = true,
                Name = "ShortcutFixer.Scan",
            };

            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join();
        }
        catch
        {
            // Falha ao criar/iniciar a thread: devolve o que houver (ou vazio).
        }

        var result = found ?? new List<ShortcutItem>();

        result.Sort(static (a, b) =>
        {
            int byLocation = a.Location.CompareTo(b.Location);
            return byLocation != 0
                ? byLocation
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return result.AsReadOnly();
    }

    /// <summary>
    /// Manda os atalhos para a Lixeira, um a um. Devolve quantos foram reciclados.
    /// Falha individual não interrompe as demais, e nunca há queda para exclusão
    /// permanente: o que não puder ser reciclado simplesmente fica onde está.
    /// Cancelamento para cedo sem lançar. Atalho atrás de vínculo (junção/symlink)
    /// é recusado antes de tentar, com o motivo entregue a
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

            if (TryRecycle(path))
                recycled++;
        }

        return recycled;
    }

    /// <summary>
    /// Roda na thread STA: enumera os .lnk das quatro raízes (dedupe por caminho) e
    /// resolve o destino de cada um via WScript.Shell. Nunca lança.
    /// </summary>
    private static List<ShortcutItem> Scan(CancellationToken ct)
    {
        var results = new List<ShortcutItem>();

        (string Root, ShortcutLocation Location)[] roots =
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), ShortcutLocation.UserDesktop),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ShortcutLocation.CommonDesktop),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutLocation.UserStartMenu),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutLocation.CommonStartMenu),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Path, string Name, ShortcutLocation Location)>();

        foreach (var (root, location) in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.lnk", Options))
                {
                    if (ct.IsCancellationRequested)
                        return results; // parcial: API pública nunca lança.

                    string path;
                    try
                    {
                        path = file.FullName;
                    }
                    catch (IOException)
                    {
                        continue; // sumiu entre a enumeração e a leitura.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    // O padrão do Win32 pode casar "algo.lnk2" em nomes curtos:
                    // confirma a extensão antes de gastar COM.
                    if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Mesmo caminho, outra grafia: conta uma vez só.
                    if (!seen.Add(path))
                        continue;

                    candidates.Add((path, Path.GetFileNameWithoutExtension(path), location));
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
        }

        if (candidates.Count == 0)
            return results;

        object? shell = null;

        try
        {
            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            shell = wshType is null ? null : Activator.CreateInstance(wshType);

            if (shell is null)
                return results; // sem WSH instalado não há como julgar destino algum.

            foreach (var (path, name, location) in candidates)
            {
                if (ct.IsCancellationRequested)
                    return results;

                string? target = TryResolveTarget(shell, path);

                if (IsBroken(target))
                    results.Add(new ShortcutItem(path, name, location, target));
            }
        }
        catch
        {
            // WSH registrado mas indisponível na prática: devolve o que já há.
        }
        finally
        {
            if (shell is not null)
            {
                try { Marshal.FinalReleaseComObject(shell); }
                catch { }
            }
        }

        return results;
    }

    /// <summary>
    /// Lê o TargetPath do atalho via CreateShortcut do WScript.Shell.
    /// Falha de COM ou ausência de destino devolve null (atalho não julgável).
    /// </summary>
    private static string? TryResolveTarget(object shell, string path)
    {
        object? shortcut = null;

        try
        {
            dynamic wsh = shell;
            shortcut = wsh.CreateShortcut(path);

            dynamic lnk = shortcut;
            string? target = lnk.TargetPath;

            return string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        }
        catch
        {
            // Atalho estranho ou COM instável: fica fora, falso negativo é aceitável.
            return null;
        }
        finally
        {
            if (shortcut is not null)
            {
                try { Marshal.FinalReleaseComObject(shortcut); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Quebrado = destino legível não vazio que, após expandir variáveis de ambiente,
    /// não existe nem como arquivo nem como pasta. Sem destino legível, falso.
    /// </summary>
    private static bool IsBroken(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false; // não julgável.

        string expanded = Environment.ExpandEnvironmentVariables(target);
        return !File.Exists(expanded) && !Directory.Exists(expanded);
    }

    // O DuplicateFinder tem o mesmo P/Invoke, mas não expõe reciclar arquivo único
    // como API reutilizável — o padrão foi replicado para o arquivo ficar
    // auto-contido. Diferença deliberada: aqui não existe plano B de exclusão
    // permanente, nunca.

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    // Se a Lixeira não estiver disponível, o shell pede confirmação antes de
    // apagar de vez; nunca apaga permanentemente em silêncio (sobrepõe o FOF_NOCONFIRMATION).
    private const ushort FOF_WANTNUKEWARNING = 0x4000;
    private const int ERROR_CANCELLED = 1223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// Tenta reciclar um único atalho. true só se o shell reporta sucesso e o
    /// arquivo realmente sumiu do caminho original.
    /// </summary>
    private static bool TryRecycle(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // Duplo nulo no fim: a API exige lista terminada em \0\0.
                pFrom = Path.GetFullPath(path) + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                         | FOF_WANTNUKEWARNING,
            };

            int result = SHFileOperation(ref op);

            // Recusa do usuário: nada foi apagado.
            if (result == ERROR_CANCELLED || op.fAnyOperationsAborted)
                return false;

            return result == 0 && !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
