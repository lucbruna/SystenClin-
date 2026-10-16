using System.IO;
using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>
/// Lê arquivo que outro processo mantém aberto em modo exclusivo.
///
/// Por que isso é necessário: Chrome, Edge e Firefox travam seus bancos SQLite
/// com bloqueio exclusivo enquanto estão abertos. Um File.ReadAllBytes comum
/// falha com "arquivo em uso". Aqui abrimos com FILE_SHARE_READ | FILE_SHARE_WRITE,
/// que o Windows permite mesmo com o navegador rodando.
///
/// Limite honesto: a cópia pode sair ligeiramente inconsistente se o navegador
/// estiver gravando no exato momento. Para leitura e limpeza isso é aceitável;
/// para escrita, nunca. Por isso toda operação aqui é somente leitura.
/// </summary>
internal static class SharedFileReader
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Copia o conteúdo para um arquivo temporário e devolve o caminho.
    /// O chamador é dono do temporário e deve apagá-lo.
    /// Em qualquer falha o temporário parcial é removido antes de devolver
    /// null — o chamador nunca recebe o caminho, então ninguém mais o apagaria.
    /// </summary>
    public static string? CopyToTemp(string sourcePath, out string? error, CancellationToken ct = default)
    {
        error = null;

        if (!File.Exists(sourcePath))
        {
            error = "arquivo não encontrado";
            return null;
        }

        var handle = CreateFileW(
            sourcePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);

        if (handle == InvalidHandle)
        {
            int code = Marshal.GetLastWin32Error();
            error = code switch
            {
                5 => "acesso negado (rode como administrador)",
                32 => "em uso exclusivo por outro processo",
                _ => $"erro do Windows {code}",
            };
            return null;
        }

        string temp = Path.Combine(
            Path.GetTempPath(),
            $"systenclin-leitura-{Guid.NewGuid():N}.db");

        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 256];

                while (true)
                {
                    // Checagem por bloco: cancelar não pode deixar cópia órfã.
                    if (ct.IsCancellationRequested)
                    {
                        error = "cancelado";
                        return null;
                    }

                    if (!ReadFile(handle, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
                    {
                        error = $"falha na leitura (erro do Windows {Marshal.GetLastWin32Error()})";
                        return null;
                    }

                    if (read == 0) break;
                    output.Write(buffer, 0, (int)read);
                }

                output.Flush();
            }

            return temp;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            CloseHandle(handle);

            // O FileStream já foi fechado neste ponto (using interno), então a
            // remoção do parcial funciona; em sucesso, error é null e fica.
            if (error is not null) TryDelete(temp);
        }
    }

    /// <summary>Apaga um temporário criado por <see cref="CopyToTemp"/>, ignorando falha.</summary>
    public static void TryDelete(string? tempPath)
    {
        if (string.IsNullOrEmpty(tempPath)) return;
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    }
}
