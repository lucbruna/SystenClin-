using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Systenclin.Core;

/// <summary>Resultado de uma passada de liberação de memória: contagens e bytes estimados liberados.</summary>
public sealed record TrimResult(int ProcessesTrimmed, int ProcessesSkipped, long EstimatedBytesFreed);

/// <summary>Libera RAM dos processos via EmptyWorkingSet. Nunca lança por falha individual.</summary>
public static class MemoryOptimizer
{
    // PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA: o mínimo que o EmptyWorkingSet exige.
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetQuota = 0x0100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Uma passada, sem retries: esvazia o working set dos processos maiores que
    /// <paramref name="minimumWorkingSetBytes"/> e devolve o delta medido de memória.
    /// O próprio processo é ignorado; falha individual conta como pulado.
    /// </summary>
    public static TrimResult TrimAll(int minimumWorkingSetBytes = 20 * 1024 * 1024, CancellationToken ct = default)
    {
        int trimmed = 0;
        int skipped = 0;
        long freed = 0;
        int selfId = Environment.ProcessId;

        foreach (Process process in Process.GetProcesses())
        {
            ct.ThrowIfCancellationRequested();

            using (process)
            {
                if (process.Id == selfId)
                    continue;

                IntPtr handle = IntPtr.Zero;
                try
                {
                    // GetProcesses entrega info em cache; Refresh garante valores atuais.
                    process.Refresh();
                    long before = process.WorkingSet64;
                    if (before < minimumWorkingSetBytes)
                    {
                        skipped++;
                        continue;
                    }

                    handle = OpenProcess(ProcessQueryInformation | ProcessSetQuota, false, process.Id);
                    if (handle == IntPtr.Zero)
                    {
                        skipped++;
                        continue;
                    }

                    if (!EmptyWorkingSet(handle))
                    {
                        skipped++;
                        continue;
                    }

                    process.Refresh();
                    freed += Math.Max(0, before - process.WorkingSet64);
                    trimmed++;
                }
                catch
                {
                    // Acesso negado, processo encerrou no meio, etc.: não derruba a passada.
                    skipped++;
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                        CloseHandle(handle);
                }
            }
        }

        return new TrimResult(trimmed, skipped, freed);
    }
}
