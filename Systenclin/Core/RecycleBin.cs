using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>Conteúdo da lixeira: quantos itens e quanto espaço ocupam.</summary>
public sealed record RecycleBinInfo(long ItemCount, long SizeBytes);

/// <summary>
/// Consulta e esvazia a lixeira do Windows pela API do shell. Nunca lança:
/// falha vira zeros (consulta) ou resultado com erro em português (esvaziar).
/// (Nome com "Manager" porque já existe um RecycleBin interno em Cleaner.cs,
/// que manda arquivos para a lixeira; este aqui mede e esvazia a lixeira.)
/// </summary>
public static class RecycleBinManager
{
    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND: a confirmação
    // e o progresso são os nossos (botão em dois cliques), então o shell não
    // precisa perguntar nada de novo nem travar a interface com uma janela.
    private const uint EmptyFlags = 0x7;

    // S_FALSE: a lixeira já estava vazia — também conta como sucesso.
    private const int SFalse = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public uint cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>
    /// Conta e mede a lixeira de todos os drives (pszRootPath nulo). Nunca
    /// lança: falha vira contagem e tamanho zerados.
    /// </summary>
    public static RecycleBinInfo Query()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            var hr = SHQueryRecycleBin(null, ref info);

            return hr == 0
                ? new RecycleBinInfo(info.i64NumItems, info.i64Size)
                : new RecycleBinInfo(0, 0);
        }
        catch
        {
            return new RecycleBinInfo(0, 0);
        }
    }

    /// <summary>
    /// Esvazia a lixeira de todos os drives, sem confirmação/progresso/som do
    /// shell. Nunca lança: sucesso é HRESULT 0 (vaziou) ou 1 (já estava vazia);
    /// qualquer outro código vira erro em PT com o HRESULT.
    /// </summary>
    public static ProcessActionResult Empty()
    {
        try
        {
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, EmptyFlags);

            return hr == 0 || hr == SFalse
                ? new ProcessActionResult(true, null)
                : new ProcessActionResult(false, $"O Windows recusou a limpeza da lixeira (código 0x{hr:X8}).");
        }
        catch (Exception ex)
        {
            return new ProcessActionResult(false, $"Não foi possível esvaziar a lixeira: {ex.Message}");
        }
    }
}
