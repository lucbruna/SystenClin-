using System.ComponentModel;
using System.Diagnostics;

namespace Systenclin.Core;

/// <summary>Linha da lista de processos: identificação e consumo atual de memória.</summary>
public sealed record ProcessItem(int Pid, string Name, long WorkingSetBytes);

/// <summary>Resultado de uma tentativa de encerrar um processo.</summary>
public sealed record ProcessActionResult(bool Success, string? Error);

/// <summary>Lista os maiores consumidores de memória e encerra processos escolhidos. Nunca lança.</summary>
public static class ProcessManager
{
    /// <summary>
    /// Os <paramref name="count"/> processos que mais ocupam memória (working set),
    /// excluindo os PIDs do sistema (0 e 4) e o próprio app. Processos protegidos
    /// ou encerrados no meio do caminho são pulados.
    /// </summary>
    public static IReadOnlyList<ProcessItem> TopByMemory(int count = 15)
    {
        if (count <= 0) return [];

        var items = new List<ProcessItem>();
        int selfId = Environment.ProcessId;

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return []; }

        foreach (Process process in processes)
        {
            using (process)
            {
                try
                {
                    int pid = process.Id;
                    if (pid == 0 || pid == 4 || pid == selfId) continue;

                    items.Add(new ProcessItem(pid, process.ProcessName, process.WorkingSet64));
                }
                catch
                {
                    // Processo protegido ou já encerrado: fica fora da lista.
                }
            }
        }

        return items
            .OrderByDescending(i => i.WorkingSetBytes)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Encerra o processo: com janela principal, tenta o fechamento gracioso e, se
    /// ainda vivo após 2 s, mata. Sem janela, mata direto. PIDs do sistema (0–4)
    /// e o próprio app são recusados. Nunca lança.
    /// </summary>
    public static ProcessActionResult End(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId)
            return new ProcessActionResult(false, "Processo protegido.");

        try
        {
            using Process process = Process.GetProcessById(pid);

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                process.CloseMainWindow();
                process.WaitForExit(2000);
            }

            if (!process.HasExited)
                process.Kill();

            return new ProcessActionResult(true, null);
        }
        catch (ArgumentException)
        {
            return new ProcessActionResult(false, "Processo não encontrado (talvez já tenha sido encerrado).");
        }
        catch (Win32Exception)
        {
            return new ProcessActionResult(false, "Acesso negado — talvez seja um processo do sistema.");
        }
        catch (Exception)
        {
            return new ProcessActionResult(false, "Não foi possível encerrar o processo.");
        }
    }
}
