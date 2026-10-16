using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Systenclin.Core;

/// <summary>
/// Limpeza segura do registro: remove apenas entradas órfãs (software desinstalado
/// cujo caminho não existe mais) e rastros de arquivos recentes (MRU). Faz backup
/// da chave antes de remover. Nunca toca em chaves do sistema.
/// </summary>
public static class RegistryCleaner
{
    public sealed record Result(bool Success, int Removed, int Scanned, string? Error);

    public static Result Clean(CancellationToken ct = default)
    {
        int removed = 0, scanned = 0;
        try
        {
            // 1. Entradas de desinstalação órfãs.
            var roots = new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            foreach (var (root, sub) in roots)
            {
                ct.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(sub);
                if (key is null) continue;

                foreach (var name in key.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;
                    try
                    {
                        using var subKey = key.OpenSubKey(name);
                        if (subKey is null) continue;

                        var installLoc = subKey.GetValue("InstallLocation") as string;
                        var uninstallStr = subKey.GetValue("UninstallString") as string;
                        var displayName = subKey.GetValue("DisplayName") as string;

                        // Só remove se tiver nome e o caminho não existir.
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        bool orphan = false;
                        if (!string.IsNullOrWhiteSpace(installLoc) && !Directory.Exists(installLoc))
                            orphan = true;

                        if (!orphan && !string.IsNullOrWhiteSpace(uninstallStr))
                        {
                            // Extrai o executável do UninstallString.
                            var exe = uninstallStr.Trim().Trim('"');
                            var firstSpace = exe.IndexOf(' ');
                            if (firstSpace > 0) exe = exe[..firstSpace].Trim('"');
                            if (exe.Length > 0 && !File.Exists(exe)) orphan = true;
                        }

                        if (!orphan) continue;

                        // Backup da chave antes de remover.
                        BackupKey(root, $@"{sub}\{name}");

                        root.DeleteSubKeyTree($@"{sub}\{name}", throwOnMissingSubKey: false);
                        removed++;
                    }
                    catch
                    {
                        // Item individual não derruba o resto.
                    }
                }
            }

            // 2. Rastros de arquivos recentes (MRU) — limpa valores, mantém a chave.
            var mruPaths = new[]
            {
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RunMRU"),
            };

            foreach (var (root, sub) in mruPaths)
            {
                ct.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(sub, writable: true);
                if (key is null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;
                    try
                    {
                        key.DeleteValue(valueName, throwOnMissingValue: false);
                        removed++;
                    }
                    catch { }
                }
            }

            return new Result(true, removed, scanned, null);
        }
        catch (Exception ex)
        {
            return new Result(false, removed, scanned, ex.Message);
        }
    }

    private static void BackupKey(RegistryKey root, string subKey)
    {
        try
        {
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Systenclin", "backup-registro");
            Directory.CreateDirectory(backupDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var safeName = subKey.Replace('\\', '_').Replace('/', '_');
            foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
            var file = Path.Combine(backupDir, $"{stamp}_{safeName}.reg");

            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{root.Name}\\{subKey}\" \"{file}\" /y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch
        {
            // Backup é melhor esforço.
        }
    }
}
