using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Systenclin.Core;

/// <summary>Estado de saúde de um disco físico.</summary>
public sealed record PhysicalDiskInfo(
    string Name,
    string MediaType,
    string Health,
    string Operational,
    long Size);

/// <summary>Resultado da consulta de saúde dos discos físicos.</summary>
public sealed record DiskHealthResult(bool Success, IReadOnlyList<PhysicalDiskInfo> Disks, string? Error);

/// <summary>Resultado da verificação do volume do sistema (chkdsk /scan).</summary>
public sealed record VolumeScanResult(bool Success, string? Error, string? Output);

/// <summary>
/// Saúde dos discos: estado dos discos físicos via Get-PhysicalDisk
/// (HealthStatus/OperationalStatus, derivados do SMART) e verificação
/// online do volume do sistema com chkdsk /scan. Nunca lança.
/// </summary>
public static class DiskHealth
{
    // Guarda só os ÚLTIMOS caracteres da saída: é no fim que ficam o
    // resumo e os erros, e o corte evita texto sem limite na interface.
    private const int OutputLimit = 4000;

    /// <summary>Consulta os discos físicos e o estado de saúde de cada um.</summary>
    public static DiskHealthResult GetHealth()
    {
        try
        {
            var stdout = RunPowerShell(
                "Get-PhysicalDisk | Select-Object FriendlyName, MediaType, HealthStatus, OperationalStatus, Size | ConvertTo-Json -Compress");

            if (stdout is null)
                return new DiskHealthResult(false, [], "não foi possível consultar os discos");

            stdout = stdout.Trim();
            if (stdout.Length == 0)
                return new DiskHealthResult(false, [], "nenhum disco físico retornado");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var disks = new List<PhysicalDiskInfo>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                // ConvertTo-Json devolve objeto único quando há um só disco;
                // aqui só passam arrays com mais de um item.
                foreach (var item in root.EnumerateArray())
                    AddDisk(disks, item);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddDisk(disks, root);
            }

            return disks.Count == 0
                ? new DiskHealthResult(false, [], "nenhum disco físico retornado")
                : new DiskHealthResult(true, disks, null);
        }
        catch (Exception ex)
        {
            return new DiskHealthResult(false, [], ex.Message);
        }
    }

    private static void AddDisk(List<PhysicalDiskInfo> disks, JsonElement item)
    {
        var name = ReadString(item, "FriendlyName");
        if (name.Length == 0) return; // registro sem nome não conta

        disks.Add(new PhysicalDiskInfo(
            name,
            ReadString(item, "MediaType"),
            ReadString(item, "HealthStatus"),
            ReadString(item, "OperationalStatus"),
            ReadLong(item, "Size")));
    }

    private static string ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadLong(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    /// <summary>
    /// Verificação online do volume do sistema com "chkdsk /scan":
    /// lê a estrutura do sistema de arquivos sem desmontar o volume.
    /// Exige administrador; nunca lança.
    /// </summary>
    public static VolumeScanResult ScanSystemVolume(CancellationToken ct = default)
    {
        if (!ScanTargets.IsElevated)
            return new VolumeScanResult(false, "Requer administrador para verificar o volume.", null);

        if (ct.IsCancellationRequested)
            return new VolumeScanResult(false, "Verificação cancelada.", null);

        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            var letra = string.IsNullOrEmpty(root)
                ? "C:"
                : root.TrimEnd(Path.DirectorySeparatorChar);

            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "chkdsk.exe"),
                Arguments = $"\"{letra}\" /scan",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = new Process { StartInfo = info };

            proc.Start();

            // Cancelar derruba a ÁRVORE do chkdsk: nenhum processo filho
            // continua mexendo no volume depois que o chamador desistiu.
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
                return new VolumeScanResult(false, "Verificação cancelada.", null);

            var output = ComposeOutput(stdoutTask.Result, stderrTask.Result);

            return proc.ExitCode switch
            {
                0 => new VolumeScanResult(true, null, output),
                1 => new VolumeScanResult(false,
                    $"O Windows encontrou problemas no volume. Repare com \"chkdsk {letra} /f\" (com reinicialização).",
                    output),
                _ => new VolumeScanResult(false, $"chkdsk retornou código {proc.ExitCode}.", output),
            };
        }
        catch (Exception ex)
        {
            return new VolumeScanResult(false, $"Não foi possível verificar o volume: {ex.Message}", null);
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

    /// <summary>Roda um comando no PowerShell e devolve o stdout, ou null em falha.</summary>
    private static string? RunPowerShell(string command)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = new Process { StartInfo = info };
            proc.Start();

            // Mesma drenagem paralela do chkdsk: pipe cheio não pode travar o filho.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);

            proc.WaitForExit();

            return proc.ExitCode == 0 ? stdoutTask.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
