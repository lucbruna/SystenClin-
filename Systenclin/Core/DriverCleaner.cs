using System.Diagnostics;

namespace Systenclin.Core;

/// <summary>
/// Limpeza de drivers antigos: enumera os drivers de terceiros instalados no
/// DriverStore (pnputil) e remove apenas as versões antigas que não estão em
/// uso por nenhum dispositivo, mantendo a mais recente de cada fornecedor/classe.
/// Libera espaço em disco e evita conflitos.
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
            // Sem a lista de drivers em uso não se remove nada (fail-safe).
            var used = GetUsedInfNames(ct);
            if (used is null)
                return new Result(false, 0, 0, "não foi possível verificar os drivers em uso; nada foi removido");

            var output = RunPnputil("/enum-drivers", ct);
            if (output is null) return new Result(false, 0, 0, "pnputil não disponível");

            var drivers = Parse(output);
            if (drivers.Count == 0) return new Result(true, 0, 0, null);

            // Agrupa por (Provider, Class) e mantém o mais recente. As versões
            // antigas só são removidas quando nenhum dispositivo as usa.
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
                    if (used.Contains(old.PublishedName)) { kept++; continue; }
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

    /// <summary>
    /// Nomes (oemXX.inf) dos drivers em uso por algum dispositivo, via CIM.
    /// Devolve null quando não for possível verificar — nesse caso nada é
    /// removido. Lista vazia também vira null (indica falha silenciosa do CIM).
    /// </summary>
    private static HashSet<string>? GetUsedInfNames(CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.InfName } | Select-Object -ExpandProperty InfName\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = new Process { StartInfo = info };
            proc.Start();
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            var stdout = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) return null;

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in stdout.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length > 0) used.Add(name);
            }
            return used.Count == 0 ? null : used;
        }
        catch
        {
            return null;
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
                // Sem /force: se o pacote ainda estiver em uso, o pnputil recusa e o driver é mantido.
                Arguments = $"/delete-driver {publishedName}",
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
