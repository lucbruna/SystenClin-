using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>Limpa o cache do resolver DNS do Windows. Nunca lança.</summary>
public static class DnsFlusher
{
    // API do Windows: retorno diferente de zero significa sucesso.
    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern int DnsFlushResolverCache();

    /// <summary>
    /// Esvazia o cache DNS. Sucesso devolve true com error nulo;
    /// falha devolve false e a razão em <paramref name="error"/>.
    /// </summary>
    public static bool Flush(out string? error)
    {
        try
        {
            if (DnsFlushResolverCache() != 0)
            {
                error = null;
                return true;
            }

            // Zero = falha; o código Win32 diz o motivo, quando presente.
            int win32 = Marshal.GetLastWin32Error();
            error = win32 != 0
                ? $"Não foi possível limpar o cache DNS (código Win32 {win32})."
                : "Não foi possível limpar o cache DNS.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Falha ao limpar o cache DNS: {ex.Message}";
            return false;
        }
    }
}
