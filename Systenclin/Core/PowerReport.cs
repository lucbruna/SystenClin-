using System.Diagnostics;
using System.IO;

namespace Systenclin.Core;

/// <summary>Resultado de um relatório do powercfg: sucesso, erro em PT, saída crua e caminho do HTML.</summary>
public sealed record PowerReportResult(bool Success, string? Error, string? Output, string? Path);

/// <summary>
/// Relatórios de energia e bateria via powercfg: /energy (traça 60 s de
/// atividade, exige administrador) e /batteryreport (sem elevação). Os HTMLs
/// vão para a pasta de relatórios do app. Nunca lança.
/// </summary>
public static class PowerReport
{
    // Guarda só a cauda da saída, como o resto do app: resumo e erros ficam no fim.
    private const int OutputLimit = 4000;

    /// <summary>Pasta dos relatórios gerados (criada só ao usar).</summary>
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Systenclin", "reports");

    /// <summary>O processo atual está elevado? Mesma checagem usada pelo chkdsk.</summary>
    public static bool IsElevated => ScanTargets.IsElevated;

    /// <summary>Argumentos do "powercfg /energy"; null se o caminho for inválido.</summary>
    public static string? BuildEnergyArguments(string outputPath)
        => BuildArguments("/energy /duration 60", outputPath);

    /// <summary>Argumentos do "powercfg /batteryreport"; null se o caminho for inválido.</summary>
    public static string? BuildBatteryArguments(string outputPath)
        => BuildArguments("/batteryreport", outputPath);

    /// <summary>
    /// Monta a linha de comando validando o caminho: aspas quebrariam o
    /// argumento e caracteres de controle injetariam comandos.
    /// </summary>
    private static string? BuildArguments(string mode, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;

        if (outputPath.Contains('"') || outputPath.Any(char.IsControl))
            return null;

        return $"powercfg {mode} /output \"{outputPath}\"";
    }

    /// <summary>Gera o relatório de energia (60 s). Exige administrador. Nunca lança.</summary>
    public static PowerReportResult GenerateEnergyReport(CancellationToken ct = default)
    {
        if (!IsElevated)
            return new PowerReportResult(
                false,
                "O relatório de energia exige administrador. Execute o Systenclin como administrador.",
                null, null);

        return Generate(energy: true, "energia", ct);
    }

    /// <summary>Gera o relatório de bateria (HTML). Não exige administrador. Nunca lança.</summary>
    public static PowerReportResult GenerateBatteryReport(CancellationToken ct = default)
        => Generate(energy: false, "bateria", ct);

    private static PowerReportResult Generate(bool energy, string prefix, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new PowerReportResult(false, "Geração cancelada.", null, null);

        string path;
        try
        {
            Directory.CreateDirectory(DefaultDir);
            path = Path.Combine(DefaultDir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        }
        catch (Exception ex)
        {
            return new PowerReportResult(false, $"Não foi possível preparar a pasta dos relatórios: {ex.Message}", null, null);
        }

        var arguments = energy ? BuildEnergyArguments(path) : BuildBatteryArguments(path);
        if (arguments is null)
            return new PowerReportResult(false, "Caminho do relatório inválido.", null, null);

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = new Process { StartInfo = info };

            proc.Start();

            // Cancelar derruba a ÁRVORE do powercfg: a coleta de 60 s para na hora.
            using var registration = ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            // Drena os dois fluxos EM PARALELO: ler um só depois do outro pode
            // travar o filho se ele encher o pipe que ainda não está sendo lido.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);

            proc.WaitForExit();

            if (ct.IsCancellationRequested)
                return new PowerReportResult(false, "Geração cancelada.", null, null);

            var output = ComposeOutput(stdoutTask.Result, stderrTask.Result);

            return proc.ExitCode == 0 && File.Exists(path)
                ? new PowerReportResult(true, null, output, path)
                : new PowerReportResult(false, $"powercfg retornou código {proc.ExitCode}.", output, null);
        }
        catch (Exception ex)
        {
            return new PowerReportResult(false, $"Não foi possível gerar o relatório: {ex.Message}", null, null);
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
