using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>Uma amostra instantânea do uso do sistema: CPU, memória física usada e total.</summary>
public sealed record SystemSample(double CpuPercent, double MemoryPercent, long MemoryUsedBytes, long MemoryTotalBytes);

/// <summary>
/// Lê o uso de CPU e de memória do Windows por P/Invoke. Nunca lança: qualquer
/// falha do sistema vira uma amostra com zeros (a UI segue mostrando o que der).
/// </summary>
public static class SystemMonitor
{
    // Baseline da CPU: primeiro Sample() só registra os contadores; o uso real
    // aparece no delta da chamada seguinte (1 s depois, pelo timer da UI).
    private static readonly object Gate = new();
    private static bool _hasBaseline;
    private static ulong _lastIdle;
    private static ulong _lastKernel;
    private static ulong _lastUser;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>Uma amostra do sistema. Nunca lança; falha vira zeros.</summary>
    public static SystemSample Sample()
    {
        var cpuPercent = SampleCpu();

        double memoryPercent = 0;
        long usedBytes = 0;
        long totalBytes = 0;

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                memoryPercent = status.dwMemoryLoad;
                totalBytes = (long)status.ullTotalPhys;
                usedBytes = totalBytes - (long)status.ullAvailPhys;
            }
        }
        catch
        {
            // Sem memória do sistema: zeros, como no resto do método.
        }

        return new SystemSample(cpuPercent, memoryPercent, usedBytes, totalBytes);
    }

    /// <summary>
    /// Uso de CPU pela diferença de GetSystemTimes entre esta chamada e a
    /// anterior. O kernelTime inclui o idle, por isso o idle sai do numerador.
    /// Primeira chamada (sem baseline) e delta total zero devolvem 0.
    /// </summary>
    private static double SampleCpu()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return 0;

            var idleTicks = Combine(idle);
            var kernelTicks = Combine(kernel);
            var userTicks = Combine(user);

            double percent;
            lock (Gate)
            {
                if (!_hasBaseline)
                {
                    _hasBaseline = true;
                    _lastIdle = idleTicks;
                    _lastKernel = kernelTicks;
                    _lastUser = userTicks;
                    return 0;
                }

                var idleDelta = idleTicks - _lastIdle;
                var kernelDelta = kernelTicks - _lastKernel;
                var userDelta = userTicks - _lastUser;

                _lastIdle = idleTicks;
                _lastKernel = kernelTicks;
                _lastUser = userTicks;

                var totalDelta = kernelDelta + userDelta;
                percent = totalDelta == 0 ? 0 : 100 * (1 - (double)idleDelta / totalDelta);
            }

            return Math.Clamp(percent, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Junta os dois lados do FILETIME num contador de 64 bits.</summary>
    private static ulong Combine(FILETIME time) => ((ulong)time.High << 32) | time.Low;
}
