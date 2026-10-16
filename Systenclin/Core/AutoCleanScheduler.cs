using System.Diagnostics;
using System.IO;

namespace Systenclin.Core;

/// <summary>
/// Agendamento de limpeza automática via Tarefa Agendada do Windows.
/// A tarefa executa o Systenclin em modo headless (--headless-clean).
/// </summary>
public static class AutoCleanScheduler
{
    private const string TaskName = "Systenclin_AutoClean";

    public static bool Schedule(int intervalMinutes, string exePath, out string error)
    {
        error = string.Empty;
        if (intervalMinutes < 5) intervalMinutes = 5;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            error = "executável não encontrado";
            return false;
        }

        // Remove tarefa anterior (idempotente).
        Unschedule(out _);

        try
        {
            var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --headless-clean\" /SC MINUTE /MO {intervalMinutes} /F";
            var (code, output) = RunSchtasks(args);
            if (code != 0)
            {
                error = string.IsNullOrWhiteSpace(output) ? $"schtasks retornou {code}" : output;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Unschedule(out string error)
    {
        error = string.Empty;
        try
        {
            var (code, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            // 0 = sucesso; 1 com "não existe" é aceitável.
            if (code != 0 && !output.Contains("não existe", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                error = output;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsScheduled()
    {
        try
        {
            var (code, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        var info = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var proc = new Process { StartInfo = info };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
        return (proc.ExitCode, output);
    }
}
