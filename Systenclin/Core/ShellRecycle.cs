using System.IO;
using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>Resultado de uma tentativa de mandar um arquivo para a Lixeira.</summary>
internal enum RecycleOutcome { Deleted, Aborted, Failed }

/// <summary>
/// Manda arquivo para a Lixeira via SHFileOperation. P/Invoke único do projeto,
/// antes replicado em Cleaner, DuplicateFinder, LargeFileFinder e ShortcutFixer.
/// Sem dependência externa: é a API do próprio Windows.
/// </summary>
internal static class ShellRecycle
{
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
    /// Envia um arquivo para a Lixeira. Devolve <see cref="RecycleOutcome.Deleted"/>
    /// só se o shell reporta sucesso e o arquivo realmente sumiu do caminho
    /// original; <see cref="RecycleOutcome.Aborted"/> quando o usuário recusa
    /// (nada foi apagado); <see cref="RecycleOutcome.Failed"/> em qualquer outra
    /// falha. Nunca apaga de forma permanente.
    /// </summary>
    public static RecycleOutcome Send(string path)
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
                return RecycleOutcome.Aborted;

            if (result == 0 && !File.Exists(path))
                return RecycleOutcome.Deleted;

            return RecycleOutcome.Failed;
        }
        catch
        {
            return RecycleOutcome.Failed;
        }
    }
}
