using System.Diagnostics;

namespace Systenclin.Core;

/// <summary>
/// Limpeza de drivers antigos: enumera os drivers de terceiros instalados no
/// DriverStore (pnputil) e remove as versões antigas, mantendo a mais recente
/// de cada fornecedor/classe. Libera espaço em disco e evita conflitos.
/// </summary>
public static class DriverCleaner
{
    public sealed record Result(bool Success, int Removed, int Kept, string? Error);

    /// <summary>Remove drivers obsoletos. Nunca lança.</summary>
    public static Result Clean(CancellationToken ct = default)
    {
        int removed = 0, kept = 0;
        try
        {
            var output = RunPnputil("/enum-drivers", ct);
            if (output is null) return new Result(false, 0, 0, "pnputil não disponível");

            var drivers = Parse(output);
            if (drivers.Count == 0) return new Result(true, 0, 0, null);

            // Agrupa por (Provider, Class) e mantém o mais recente.
            var groups = drivers.GroupBy(d => (d.Provider, d.Class));
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                if (group.Count() <= 1) { kept += group.Count(); continue; }

                var ordered = group.OrderByDescending(d => d.Version).ThenByDescending(d => d.Date).ToList();
                kept++;
                foreach (var old in ordered.Skip(1))
                {
                    ct.ThrowIfCancellationRequested();
                    if (RemoveDriver(old.PublishedName, ct)) removed++;
                    else kept++;
                }
            }

            return new Result(true, removed, kept, null);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, removed, kept, "cancelado");
        }
        catch (Exception ex)
        {
            return new Result(false, removed, kept, ex.Message);
        }
    }

    private static string? RunPnputil(string args, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var proc = new Process { StartInfo = info };
            proc.Start();
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool RemoveDriver(string publishedName, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/delete-driver {publishedName} /uninstall /force",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = new Process { StartInfo = info };
            proc.Start();
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record DriverInfo(string PublishedName, string Provider, string Class, string Version, string Date);

    private static List<DriverInfo> Parse(string output)
    {
        var list = new List<DriverInfo>();
        var lines = output.Split('\n');
        string? published = null, provider = null, cls = null, version = null, date = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                if (published is not null && provider is not null)
                {
                    list.Add(new DriverInfo(published, provider, cls ?? "", version ?? "0", date ?? ""));
                }
                published = provider = cls = version = date = null;
                continue;
            }

            if (line.StartsWith("Published Name", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Nome publicado", StringComparison.OrdinalIgnoreCase))
            {
                published = ValueAfterColon(line);
            }
            else if (line.StartsWith("Original Name", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Nome original", StringComparison.OrdinalIgnoreCase))
            {
                // ignore
            }
            else if (line.StartsWith("Provider Name", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Nome do fornecedor", StringComparison.OrdinalIgnoreCase))
            {
                provider = ValueAfterColon(line);
            }
            else if (line.StartsWith("Class Name", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Nome da classe", StringComparison.OrdinalIgnoreCase))
            {
                cls = ValueAfterColon(line);
            }
            else if (line.StartsWith("Driver Version", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Versão do driver", StringComparison.OrdinalIgnoreCase))
            {
                version = ValueAfterColon(line);
            }
            else if (line.StartsWith("Signer Name", StringComparison.OrdinalIgnoreCase))
            {
                // ignore
            }
            else if (line.StartsWith("Driver Date", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Data do driver", StringComparison.OrdinalIgnoreCase))
            {
                date = ValueAfterColon(line);
            }
        }

        if (published is not null && provider is not null)
        {
            list.Add(new DriverInfo(published, provider, cls ?? "", version ?? "0", date ?? ""));
        }

        return list;
    }

    private static string ValueAfterColon(string line)
    {
        var idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim() : line.Trim();
    }
}
