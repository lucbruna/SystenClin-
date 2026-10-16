using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>Tipo físico do disco: HDD "paga pedágio" para buscar dados fora de ordem, SSD não.</summary>
public enum DiskMediaType
{
    /// <summary>A consulta ao driver falhou; desconhecido é melhor que chute.</summary>
    Unknown = 0,

    /// <summary>Disco mecânico: busca fora de ordem custa tempo.</summary>
    Hdd = 1,

    /// <summary>Disco de estado sólido: sem penalidade de busca.</summary>
    Ssd = 2,
}

/// <summary>Resultado de uma otimização: sucesso, motivo da falha e saída do defrag.</summary>
public sealed record OptimizeResult(bool Success, string? Error, string? Output);

/// <summary>
/// Detecta o tipo do disco que contém o Windows e dispara a otimização
/// correta via defrag.exe /O: retrim em SSD, desfragmentação em HDD.
/// O próprio defrag escolhe o modo pela mídia do volume. Nunca lança.
/// </summary>
public static class DiskOptimizer
{
    // ── Interop ─────────────────────────────────────────────────────────
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    // IOCTL_STORAGE_QUERY_PROPERTY: consulta propriedades do dispositivo.
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    // StorageDeviceSeekPenaltyProperty + PropertyStandardQuery.
    private const uint STORAGE_DEVICE_SEEK_PENALTY_PROPERTY = 7;
    private const uint PROPERTY_STANDARD_QUERY = 0;

    // Guarda só os ÚLTIMOS caracteres da saída: é no fim que ficam o
    // resumo e os erros, e o corte evita texto sem limite na interface.
    private const int OutputLimit = 4000;

    private static readonly IntPtr InvalidHandle = new(-1);

    // Layout nativo de STORAGE_PROPERTY_QUERY; o byte final espelha o
    // AdditionalParameters[1] da estrutura C e nunca é preenchido.
    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    // Layout nativo de DEVICE_SEEK_PENALTY_DESCRIPTOR: se o dispositivo
    // paga penalidade de busca (HDD) ou não (SSD).
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer, uint nInBufferSize,
        out DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ── Cache do tipo de disco ──────────────────────────────────────────
    // Lazy default é thread-safe (ExecutionAndPublication) e calcula UMA vez.
    private static readonly Lazy<DiskMediaType> SystemDriveMediaType = new(QuerySystemDriveMediaType);

    /// <summary>Tipo do disco que contém o Windows. Resultado é cacheado.</summary>
    public static DiskMediaType GetSystemDriveMediaType() => SystemDriveMediaType.Value;

    /// <summary>
    /// Consulta o StorageDeviceSeekPenaltyProperty do volume do sistema.
    /// Qualquer falha devolve Unknown: nunca lança.
    /// </summary>
    private static DiskMediaType QuerySystemDriveMediaType()
    {
        var handle = InvalidHandle;

        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root))
                return DiskMediaType.Unknown;

            // "C:\" → "\\.\C:": o nome de volume que o CreateFile aceita.
            var volume = @"\\.\" + root.TrimEnd(Path.DirectorySeparatorChar);

            // Acesso 0: a consulta de metadados não exige ler o volume.
            handle = CreateFileW(
                volume,
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle == InvalidHandle || handle == IntPtr.Zero)
                return DiskMediaType.Unknown;

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = STORAGE_DEVICE_SEEK_PENALTY_PROPERTY,
                QueryType = PROPERTY_STANDARD_QUERY,
            };

            if (!DeviceIoControl(
                    handle,
                    IOCTL_STORAGE_QUERY_PROPERTY,
                    ref query,
                    (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                    out var descriptor,
                    (uint)Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(),
                    out _,
                    IntPtr.Zero))
            {
                return DiskMediaType.Unknown;
            }

            // HDD precisa mover a cabeça de leitura: paga penalidade de busca. SSD não.
            return descriptor.IncursSeekPenalty != 0 ? DiskMediaType.Hdd : DiskMediaType.Ssd;
        }
        catch
        {
            // Driver exótico, volume virtual, permissão: não dá para saber.
            return DiskMediaType.Unknown;
        }
        finally
        {
            // Fecha mesmo em falha; o volume não é desmontado por isso.
            if (handle != InvalidHandle && handle != IntPtr.Zero)
                CloseHandle(handle);
        }
    }

    /// <summary>
    /// Otimiza o disco do sistema com defrag.exe /O: TRIM em SSD,
    /// desfragmentação em HDD — o Windows escolhe pela mídia.
    /// Nunca lança; exige elevação.
    /// </summary>
    public static OptimizeResult OptimizeSystemDrive(CancellationToken ct = default)
    {
        if (!ScanTargets.IsElevated)
            return new OptimizeResult(false, "Requer administrador para otimizar o disco.", null);

        if (ct.IsCancellationRequested)
            return new OptimizeResult(false, "Otimização cancelada.", null);

        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            var letra = string.IsNullOrEmpty(root)
                ? "C:"
                : root.TrimEnd(Path.DirectorySeparatorChar);

            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "defrag.exe"),
                Arguments = $"\"{letra}\" /O",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = new Process { StartInfo = info };

            proc.Start();

            // Cancelar derruba a ÁRVORE do defrag: nenhum processo filho
            // continua mexendo no disco depois que o chamador desistiu.
            using var registration = ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            // Lê antes do WaitForExit: pipe cheio travaria o filho para sempre.
            // A saída do defrag é pequena, então a leitura síncrona é segura.
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();

            proc.WaitForExit();

            if (ct.IsCancellationRequested)
                return new OptimizeResult(false, "Otimização cancelada.", null);

            var output = ComposeOutput(stdout, stderr);
            var exitCode = proc.ExitCode;

            return exitCode == 0
                ? new OptimizeResult(true, null, output)
                : new OptimizeResult(false, $"defrag retornou código {exitCode}.", output);
        }
        catch (Exception ex)
        {
            return new OptimizeResult(false, $"Não foi possível otimizar o disco: {ex.Message}", null);
        }
    }

    /// <summary>Junta stdout e stderr e mantém só a cauda, até <see cref="OutputLimit"/>.</summary>
    private static string ComposeOutput(string stdout, string stderr)
    {
        var output = stderr.Length == 0
            ? stdout
            : stdout + Environment.NewLine + stderr;

        return output.Length > OutputLimit ? output[^OutputLimit..] : output;
    }
}
