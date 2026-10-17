using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Systenclin.Core;

namespace Systenclin;

public partial class MainWindow : Window
{
    // ════════════════════════════════════════════════════════════════════════
    //  Otimização
    // ════════════════════════════════════════════════════════════════════════

    private void ShowOptimizeTab()
    {
        HeaderTitle.Text = "Otimização do sistema";
        SubtitleText.Text = "Inicialização, memória e rede — ajustes rápidos, sem instalar nada.";

        BrowserWarning.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        OptimizePanel.Visibility = Visibility.Visible;
        DuplicatesPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Collapsed;
        LargeFilesPanel.Visibility = Visibility.Collapsed;

        // Carregamento preguiçoso: nada é lido no startup da janela.
        if (!_optimizeLoaded && !_optimizeLoading)
            _ = LoadOptimizeAsync();

        // Processos: mesma regra, uma única vez na primeira abertura da aba.
        if (!_procLoaded)
        {
            _procLoaded = true;
            _ = LoadProcessesAsync();
        }

        // Lixeira: contagem inicial uma única vez, sem travar a abertura da aba.
        if (!_binLoaded)
        {
            _binLoaded = true;
            _ = RefreshBinAsync();
        }

        // Rede: lista os adaptadores uma única vez na primeira abertura da aba.
        if (!_netLoaded)
        {
            _netLoaded = true;
            _ = RefreshNetworkAsync();
        }
    }

    private async Task LoadOptimizeAsync()
    {
        if (_optimizeLoading) return;
        _optimizeLoading = true;

        try
        {
            var boot = await Task.Run(() => BootTimeReader.GetLastBoot());
            BootTimeText.Text = $"Última inicialização: {FormatBoot(boot)}";

            var recentBoots = await Task.Run(() => BootTimeReader.GetRecent(5));
            BootHistoryText.Text = FormatBootHistory(recentBoots);

            var media = await Task.Run(() => DiskOptimizer.GetSystemDriveMediaType());
            DiskMediaTypeText.Text = DescribeMediaType(media);

            var health = await Task.Run(() => DiskHealth.GetHealth());
            DiskHealthText.Text = FormatDiskHealth(health);

            await LoadStartupListAsync();
            _optimizeLoaded = true;
        }
        catch (Exception ex)
        {
            BootTimeText.Text = "Última inicialização: Indisponível";
            BootHistoryText.Text = "Histórico: —";
            SetStartupMessage($"Não foi possível carregar a lista: {ex.Message}");
        }
        finally
        {
            _optimizeLoading = false;
        }
    }

    private bool _winsxsBusy;
    private CancellationTokenSource? _winsxsCts;

    private async void WinSxS_Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_winsxsBusy) return;

        if (!ScanTargets.IsElevated)
        {
            WinSxSStatus.Text = "Esta operação exige administrador. Reabra o Systenclin como administrador.";
            WinSxSStatus.Visibility = Visibility.Visible;
            return;
        }

        _winsxsBusy = true;
        _winsxsCts = new CancellationTokenSource();
        var ct = _winsxsCts.Token;

        WinSxSCleanButton.IsEnabled = false;
        WinSxSCancelButton.Visibility = Visibility.Visible;
        WinSxSProgress.Visibility = Visibility.Visible;
        WinSxSStatus.Text = "Executando DISM… pode levar vários minutos.";
        WinSxSStatus.Visibility = Visibility.Visible;

        bool deep = WinSxSDeepCheck.IsChecked == true;
        string args = deep
            ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"
            : "/Online /Cleanup-Image /StartComponentCleanup";

        try
        {
            var result = await Task.Run(() =>
            {
                var info = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dism.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                using var proc = new System.Diagnostics.Process { StartInfo = info };
                proc.Start();
                using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return (proc.ExitCode, stdout, stderr);
            }, ct);

            if (ct.IsCancellationRequested)
            {
                WinSxSStatus.Text = "Operação cancelada.";
                return;
            }

            if (result.ExitCode == 0)
            {
                WinSxSStatus.Text = "Limpeza concluída com sucesso.";
            }
            else
            {
                var tail = (result.stderr.Length > 0 ? result.stderr : result.stdout);
                if (tail.Length > 600) tail = tail[^600..];
                WinSxSStatus.Text = $"DISM retornou código {result.ExitCode}. {tail}";
            }
        }
        catch (OperationCanceledException)
        {
            WinSxSStatus.Text = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            WinSxSStatus.Text = $"Falha ao executar DISM: {ex.Message}";
        }
        finally
        {
            _winsxsBusy = false;
            _winsxsCts?.Dispose();
            _winsxsCts = null;
            WinSxSCleanButton.IsEnabled = true;
            WinSxSCancelButton.Visibility = Visibility.Collapsed;
            WinSxSProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void WinSxS_Cancel_Click(object sender, RoutedEventArgs e)
    {
        try { _winsxsCts?.Cancel(); } catch { }
    }

    private void Reinforce_Click(object sender, RoutedEventArgs e)
    {
        ReinforceButton.IsEnabled = false;
        ReinforceStatus.Text = "Aplicando…";
        try
        {
            bool deep = ReinforceDeepCheck.IsChecked == true;
            var r = SecurityReinforce.Apply(deep);
            ReinforceStatus.Text = r.Success
                ? $"Concluído: {r.Applied} ajuste(s) aplicado(s), {r.Skipped} ignorado(s)."
                : $"Falha: {r.Error}";
        }
        catch (Exception ex)
        {
            ReinforceStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            ReinforceButton.IsEnabled = true;
        }
    }

    private async void DriverClean_Click(object sender, RoutedEventArgs e)
    {
        DriverCleanButton.IsEnabled = false;
        DriverCleanStatus.Text = "Limpando…";
        try
        {
            var r = await Task.Run(() => DriverCleaner.Clean());
            DriverCleanStatus.Text = r.Success
                ? $"Concluído: {r.Removed} removido(s), {r.Kept} mantido(s)."
                : $"Falha: {r.Error}";
        }
        catch (Exception ex)
        {
            DriverCleanStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            DriverCleanButton.IsEnabled = true;
        }
    }

    private void QuickSettings_Click(object sender, RoutedEventArgs e)
    {
        QuickSettingsButton.IsEnabled = false;
        QuickSettingsStatus.Text = "Aplicando…";
        try
        {
            var r = QuickSettings.Apply(
                QsShowExtensions.IsChecked == true,
                QsShowHidden.IsChecked == true,
                QsDarkMode.IsChecked == true,
                QsDisableAnimations.IsChecked == true,
                QsDisableTransparency.IsChecked == true);
            QuickSettingsStatus.Text = r.Success
                ? $"Concluído: {r.Applied} ajuste(s) aplicado(s)."
                : $"Falha: {r.Error}";
        }
        catch (Exception ex)
        {
            QuickSettingsStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            QuickSettingsButton.IsEnabled = true;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _autoRamTimer;

    private void AutoRam_Toggle(object sender, RoutedEventArgs e)
    {
        if (AutoRamEnabled.IsChecked == true)
        {
            if (!int.TryParse(AutoRamInterval.Text, out int mins) || mins < 1) mins = 10;
            _autoRamTimer?.Stop();
            _autoRamTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(mins),
            };
            _autoRamTimer.Tick += (_, _) =>
            {
                try
                {
                    MemoryOptimizer.TrimAll();
                    AutoRamStatus.Text = $"Última limpeza: {DateTime.Now:HH:mm:ss}";
                }
                catch (Exception ex)
                {
                    AutoRamStatus.Text = $"Falha: {ex.Message}";
                }
            };
            _autoRamTimer.Start();
            AutoRamStatus.Text = $"Habilitado (a cada {mins} min).";
        }
        else
        {
            _autoRamTimer?.Stop();
            _autoRamTimer = null;
            AutoRamStatus.Text = "Desabilitado.";
        }
    }

    private void AutoCleanSchedule_Click(object sender, RoutedEventArgs e)
    {
        AutoCleanScheduleButton.IsEnabled = false;
        AutoCleanStatus.Text = "Agendando…";
        try
        {
            if (!int.TryParse(AutoCleanInterval.Text, out int mins) || mins < 5) mins = 60;
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe))
            {
                AutoCleanStatus.Text = "Não foi possível localizar o executável.";
                return;
            }
            if (AutoCleanScheduler.Schedule(mins, exe, out var err))
                AutoCleanStatus.Text = $"Agendado a cada {mins} min.";
            else
                AutoCleanStatus.Text = $"Falha: {err}";
        }
        catch (Exception ex)
        {
            AutoCleanStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            AutoCleanScheduleButton.IsEnabled = true;
        }
    }

    private void AutoCleanUnschedule_Click(object sender, RoutedEventArgs e)
    {
        AutoCleanUnscheduleButton.IsEnabled = false;
        try
        {
            if (AutoCleanScheduler.Unschedule(out var err))
                AutoCleanStatus.Text = "Agendamento removido.";
            else
                AutoCleanStatus.Text = $"Falha: {err}";
        }
        catch (Exception ex)
        {
            AutoCleanStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            AutoCleanUnscheduleButton.IsEnabled = true;
        }
    }

    private async void RegistryClean_Click(object sender, RoutedEventArgs e)
    {
        RegistryCleanButton.IsEnabled = false;
        RegistryCleanStatus.Text = "Limpando…";
        try
        {
            var r = await Task.Run(() => RegistryCleaner.Clean());
            RegistryCleanStatus.Text = r.Success
                ? $"Concluído: {r.Removed} entrada(s) removida(s) de {r.Scanned} verificada(s)."
                : $"Falha: {r.Error}";
        }
        catch (Exception ex)
        {
            RegistryCleanStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            RegistryCleanButton.IsEnabled = true;
        }
    }

    private void SafeFolder_Click(object sender, RoutedEventArgs e)
    {
        SafeFolderButton.IsEnabled = false;
        SafeFolderStatus.Text = "Processando…";
        try
        {
            if (SafeFolder.Open(out var err))
                SafeFolderStatus.Text = $"Pasta: {SafeFolder.DefaultPath}";
            else
                SafeFolderStatus.Text = $"Falha: {err}";
        }
        catch (Exception ex)
        {
            SafeFolderStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            SafeFolderButton.IsEnabled = true;
        }
    }

    private void RegDefrag_Click(object sender, RoutedEventArgs e)
    {
        RegDefragButton.IsEnabled = false;
        RegDefragStatus.Text = "Medindo…";
        try
        {
            var hive = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "NTUSER.DAT");
            if (!File.Exists(hive))
            {
                RegDefragStatus.Text = "Hive não encontrado.";
                return;
            }
            var info = new FileInfo(hive);
            RegDefragStatus.Text = $"Hive: {CleanableItem.FormatBytes(info.Length)} ({hive}).";
        }
        catch (Exception ex)
        {
            RegDefragStatus.Text = $"Falha: {ex.Message}";
        }
        finally
        {
            RegDefragButton.IsEnabled = true;
        }
    }

    private static string FormatBoot(BootPerformance? boot) => boot is null
        ? "Indisponível"
        : $"{boot.BootDuration.TotalSeconds:N1} s (às {boot.BootTime:HH:mm})";

    /// <summary>Só as durações das últimas inicializações, no mesmo formato do boot atual.</summary>
    private static string FormatBootHistory(IReadOnlyList<BootPerformance> recent)
        => recent.Count == 0
            ? "Histórico: —"
            : "Histórico: " + string.Join(" • ", recent.Select(b => $"{b.BootDuration.TotalSeconds:N1} s"));

    private async void SecureDelete_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolher arquivo para destruir",
            Filter = "Todos os arquivos (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var confirm = MessageBox.Show(this,
            $"Destruir permanentemente (irreversível):\n{path}\n\nContinuar?",
            "Destruição segura", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SecureDeleteButton.IsEnabled = false;
        SecureDeleteStatus.Text = "Destruindo…";
        try
        {
            string err = "";
            var result = await Task.Run(() => SecureDelete.Shred(path, 3, out err));
            if (result)
                SecureDeleteStatus.Text = "Arquivo destruído.";
            else
                SecureDeleteStatus.Text = $"Falha: {err}";
        }
        catch (Exception ex)
        {
            SecureDeleteStatus.Text = $"Falha: {ex.Message}";
        }
        finally { SecureDeleteButton.IsEnabled = true; }
    }

    private async void StartupRefresh_Click(object sender, RoutedEventArgs e)
        => await LoadStartupListAsync();

    private async Task LoadStartupListAsync(string? messageAfterLoad = null)
    {
        if (_startupBusy) return;

        _startupBusy = true;
        StartupRefreshButton.IsEnabled = false;
        StartupList.IsEnabled = false;
        SetStartupMessage("Carregando…");

        try
        {
            var items = await Task.Run(() => StartupManager.List());
            bool elevated = ScanTargets.IsElevated;

            StartupList.ItemsSource = items.Select(i => new StartupRow(i, elevated)).ToList();

            SetStartupMessage(items.Count == 0
                ? "Nenhum item de inicialização encontrado."
                : messageAfterLoad);
        }
        catch (Exception ex)
        {
            SetStartupMessage($"Não foi possível carregar a lista: {ex.Message}");
        }
        finally
        {
            _startupBusy = false;
            StartupRefreshButton.IsEnabled = true;
            StartupList.IsEnabled = true;
        }
    }

    private async void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_startupBusy) return;
        if (sender is not Button button || button.Tag is not string id ||
            button.DataContext is not StartupRow row || !row.ActionEnabled)
            return;

        _startupBusy = true;
        SetStartupMessage(row.ItemEnabled ? "Desativando…" : "Ativando…");

        string message;
        try
        {
            var result = await Task.Run(() => row.ItemEnabled
                ? StartupManager.Disable(id)
                : StartupManager.Enable(id));

            message = result.Success
                ? (row.ItemEnabled ? "Item desativado." : "Item ativado.")
                : (result.Error ?? "Não foi possível alterar o item.");
        }
        catch (Exception ex)
        {
            message = $"Não foi possível alterar o item: {ex.Message}";
        }
        finally
        {
            _startupBusy = false;
        }

        await LoadStartupListAsync(message);
    }

    private void SetStartupMessage(string? message)
    {
        StartupMessage.Text = message ?? "";
        StartupMessage.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_trimBusy || _boostBusy) return;

        _trimBusy = true;
        SyncBoostEnabled();
        TrimButton.IsEnabled = false;
        TrimResult.Visibility = Visibility.Collapsed;
        TrimProgress.Visibility = Visibility.Visible;

        try
        {
            var result = await Task.Run(() => MemoryOptimizer.TrimAll());

            var countText = result.ProcessesTrimmed == 1
                ? "1 processo ajustado"
                : $"{result.ProcessesTrimmed} processos ajustados";

            if (result.EstimatedBytesFreed > 0)
            {
                double freedMb = result.EstimatedBytesFreed / (1024.0 * 1024.0);
                TrimResult.Text = $"{countText} • ~{freedMb:N1} MB liberados";
            }
            else
            {
                TrimResult.Text = countText;
            }
        }
        catch (Exception ex)
        {
            TrimResult.Text = $"Não foi possível liberar memória: {ex.Message}";
        }
        finally
        {
            TrimResult.Visibility = Visibility.Visible;
            TrimProgress.Visibility = Visibility.Collapsed;
            TrimButton.IsEnabled = true;
            _trimBusy = false;
            SyncBoostEnabled();
        }
    }

    private async void Dns_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsBusy || _boostBusy) return;

        _dnsBusy = true;
        SyncBoostEnabled();
        DnsButton.IsEnabled = false;
        DnsMessage.Visibility = Visibility.Collapsed;

        try
        {
            var (success, error) = await Task.Run(() =>
            {
                bool ok = DnsFlusher.Flush(out var err);
                return (ok, err);
            });

            DnsMessage.Text = success
                ? "Cache DNS limpo."
                : (error ?? "Não foi possível limpar o cache DNS.");
        }
        catch (Exception ex)
        {
            DnsMessage.Text = $"Não foi possível limpar o cache DNS: {ex.Message}";
        }
        finally
        {
            DnsMessage.Visibility = Visibility.Visible;
            DnsButton.IsEnabled = true;
            _dnsBusy = false;
            SyncBoostEnabled();
        }
    }

    private static string DescribeMediaType(DiskMediaType type) => type switch
    {
        DiskMediaType.Ssd => "SSD — será executado TRIM",
        DiskMediaType.Hdd => "HDD — será executada desfragmentação",
        _ => "Tipo de disco: desconhecido",
    };

    private void DiskCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _diskCts?.Cancel(); } catch { }
    }

    private async void OptimizeDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_diskBusy || _boostBusy || _scanBusy || !_elevated) return;

        _diskBusy = true;
        SyncBoostEnabled();
        _diskCts = new CancellationTokenSource();
        var ct = _diskCts.Token;

        OptimizeDiskButton.IsEnabled = false;
        DiskProgress.Visibility = Visibility.Visible;
        DiskStatus.Visibility = Visibility.Visible;
        DiskStatus.Text = "Otimizando… pode levar alguns minutos.";
        DiskCancelButton.Visibility = Visibility.Visible;

        try
        {
            var result = await Task.Run(() => DiskOptimizer.OptimizeSystemDrive(ct));

            DiskStatus.Text = result.Success
                ? "Disco otimizado com sucesso."
                : result.Error ?? "Não foi possível otimizar o disco.";
        }
        catch (OperationCanceledException)
        {
            DiskStatus.Text = "Otimização cancelada.";
        }
        catch (Exception ex)
        {
            DiskStatus.Text = $"Não foi possível otimizar o disco: {ex.Message}";
        }
        finally
        {
            DiskProgress.Visibility = Visibility.Collapsed;
            DiskCancelButton.Visibility = Visibility.Collapsed;
            OptimizeDiskButton.IsEnabled = _elevated;
            _diskBusy = false;
            SyncBoostEnabled();
            _diskCts?.Dispose();
            _diskCts = null;
        }
    }

    // ── Saúde do disco ──────────────────────────────────────────────────────

    /// <summary>Uma linha por disco físico: nome, tipo, estado (SMART) e tamanho.</summary>
    private static string FormatDiskHealth(DiskHealthResult health)
    {
        if (!health.Success || health.Disks.Count == 0)
            return $"Saúde indisponível: {health.Error ?? "erro desconhecido"}.";

        var lines = new List<string>();
        foreach (var disk in health.Disks)
            lines.Add(
                $"{disk.Name} — {DescribeHealthMedia(disk.MediaType)}, " +
                $"{DescribeHealthState(disk.Health, disk.Operational)} " +
                $"({CleanableItem.FormatBytes(disk.Size)})");

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeHealthMedia(string media) => media switch
    {
        "SSD" => "SSD",
        "HDD" => "HDD",
        "" or "Unspecified" => "tipo desconhecido",
        _ => media,
    };

    private static string DescribeHealthState(string health, string operational)
    {
        var state = health switch
        {
            "Healthy" => "saudável",
            "Warning" => "em atenção",
            "Unhealthy" => "com problema",
            "" or "Unknown" => "estado desconhecido",
            _ => health,
        };

        var op = operational switch
        {
            "" or "OK" or "Unknown" => string.Empty,
            "Degraded" => "degradado",
            "Warning" => "em atenção",
            "Offline" => "offline",
            _ => operational,
        };

        return op.Length == 0 || op == state ? state : $"{state}, {op}";
    }

    private async void DiskHealthRefresh_Click(object sender, RoutedEventArgs e)
    {
        DiskHealthRefreshButton.IsEnabled = false;
        DiskHealthText.Text = "Verificando discos…";

        try
        {
            var health = await Task.Run(() => DiskHealth.GetHealth());
            DiskHealthText.Text = FormatDiskHealth(health);
        }
        finally
        {
            DiskHealthRefreshButton.IsEnabled = true;
        }
    }

    private void DiskScanCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _scanCts?.Cancel(); } catch { }
    }

    private async void DiskScan_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOptimizeBusy || !_elevated) return;

        _scanBusy = true;
        SyncBoostEnabled();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        OptimizeDiskButton.IsEnabled = false;
        DiskScanProgress.Visibility = Visibility.Visible;
        DiskScanStatus.Visibility = Visibility.Visible;
        DiskScanStatus.Text = "Verificando o volume… pode levar alguns minutos.";
        DiskScanCancelButton.Visibility = Visibility.Visible;

        try
        {
            var result = await Task.Run(() => DiskHealth.ScanSystemVolume(ct));

            DiskScanStatus.Text = result.Success
                ? "Nenhum problema encontrado no volume do sistema."
                : result.Error ?? "Não foi possível verificar o volume.";
        }
        catch (OperationCanceledException)
        {
            DiskScanStatus.Text = "Verificação cancelada.";
        }
        catch (Exception ex)
        {
            DiskScanStatus.Text = $"Não foi possível verificar o volume: {ex.Message}";
        }
        finally
        {
            DiskScanProgress.Visibility = Visibility.Collapsed;
            DiskScanCancelButton.Visibility = Visibility.Collapsed;
            OptimizeDiskButton.IsEnabled = _elevated;
            _scanBusy = false;
            SyncBoostEnabled();
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    // ── Relatório de energia (powercfg) ─────────────────────────────────────

    private void PowerCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _powerCts?.Cancel(); } catch { }
    }

    private async void PowerEnergy_Click(object sender, RoutedEventArgs e)
        => await GenerateReportAsync(energy: true);

    private async void PowerBattery_Click(object sender, RoutedEventArgs e)
        => await GenerateReportAsync(energy: false);

    /// <summary>
    /// Gera o relatório de energia (60 s de coleta, exige administrador) ou o
    /// de bateria, abrindo o HTML no navegador ao terminar. Enquanto roda,
    /// bloqueia as demais operações de otimização (a coleta do /energy é
    /// sensível a atividade concorrente).
    /// </summary>
    private async Task GenerateReportAsync(bool energy)
    {
        if (AnyOptimizeBusy) return;

        _powerBusy = true;
        SyncBoostEnabled();
        _powerCts = new CancellationTokenSource();
        var ct = _powerCts.Token;

        PowerProgress.Visibility = Visibility.Visible;
        PowerStatus.Visibility = Visibility.Visible;
        PowerStatus.Text = energy
            ? "Gerando relatório de energia… a coleta leva 60 s."
            : "Gerando relatório de bateria…";
        PowerCancelButton.Visibility = Visibility.Visible;

        try
        {
            var result = await Task.Run(() => energy
                ? PowerReport.GenerateEnergyReport(ct)
                : PowerReport.GenerateBatteryReport(ct));

            if (result.Success && result.Path is not null)
            {
                OpenWithShell(result.Path);
                PowerStatus.Text = $"Relatório salvo e aberto: {result.Path}";
            }
            else
            {
                PowerStatus.Text = result.Error ?? "Não foi possível gerar o relatório.";
            }
        }
        catch (OperationCanceledException)
        {
            PowerStatus.Text = "Geração cancelada.";
        }
        catch (Exception ex)
        {
            PowerStatus.Text = $"Não foi possível gerar o relatório: {ex.Message}";
        }
        finally
        {
            PowerProgress.Visibility = Visibility.Collapsed;
            PowerCancelButton.Visibility = Visibility.Collapsed;
            _powerBusy = false;
            SyncBoostEnabled();
            _powerCts?.Dispose();
            _powerCts = null;
        }
    }

    private void PowerOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        PowerStatus.Visibility = Visibility.Visible;

        try
        {
            Directory.CreateDirectory(PowerReport.DefaultDir);
            OpenWithShell(PowerReport.DefaultDir);
            PowerStatus.Text = $"Pasta dos relatórios: {PowerReport.DefaultDir}";
        }
        catch (Exception ex)
        {
            PowerStatus.Text = $"Não foi possível abrir a pasta dos relatórios: {ex.Message}";
        }
    }

    /// <summary>Abre arquivo ou pasta no shell padrão; falha de associação não quebra a UI.</summary>
    private static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Sem navegador/pasta associada: o caminho já aparece no status.
        }
    }

    // ── Aceleração em 1 clique ──────────────────────────────────────────────

    /// <summary>Só libera o botão quando nenhuma operação de otimização roda.</summary>
    private bool AnyOptimizeBusy => _boostBusy || _trimBusy || _dnsBusy || _diskBusy || _netBusy || _scanBusy || _powerBusy;

    /// <summary>
    /// Sincroniza o estado dos botões da aba Otimização: Boost, rede, a
    /// verificação de volume e os relatórios ficam desabilitados durante
    /// qualquer operação; as ações que exigem administrador só habilitam com
    /// o programa elevado.
    /// </summary>
    private void SyncBoostEnabled()
    {
        bool idle = !AnyOptimizeBusy;
        BoostButton.IsEnabled = idle;
        NetRefreshButton.IsEnabled = idle;
        NetRenewButton.IsEnabled = idle && _elevated;
        NetArpButton.IsEnabled = idle && _elevated;
        DiskScanButton.IsEnabled = idle && _elevated;
        PowerEnergyButton.IsEnabled = idle && _elevated;
        PowerBatteryButton.IsEnabled = idle;
    }

    /// <summary>
    /// Roda, em sequência, o ajuste de memória (MemoryOptimizer.TrimAll) e a
    /// limpeza do cache DNS (DnsFlusher.Flush), mostrando uma única linha com o
    /// resultado consolidado. O botão só funciona quando nenhuma outra operação
    /// de otimização está em andamento.
    /// </summary>
    private async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOptimizeBusy) return;

        _boostBusy = true;
        SyncBoostEnabled();
        BoostStatus.Visibility = Visibility.Visible;
        BoostStatus.Text = "Liberando memória…";

        try
        {
            var (trim, dnsOk, dnsError) = await Task.Run(() =>
            {
                var t = MemoryOptimizer.TrimAll();
                bool ok = DnsFlusher.Flush(out var err);
                return (t, ok, err);
            });

            var parts = new List<string>();

            parts.Add(trim.ProcessesTrimmed == 1
                ? "1 processo ajustado"
                : $"{trim.ProcessesTrimmed} processos ajustados");

            if (trim.EstimatedBytesFreed > 0)
                parts.Add($"~{CleanableItem.FormatBytes(trim.EstimatedBytesFreed)} liberados");

            parts.Add(dnsOk
                ? "cache DNS limpo"
                : $"não foi possível limpar o cache DNS{(dnsError is null ? "" : $" ({dnsError})")}");

            BoostStatus.Text = string.Join(" · ", parts);

            // Seguro: RefreshBinAsync é protegido por _binBusy e só roda na UI
            // thread — não há corrida com um esvaziamento em andamento.
            _ = RefreshBinAsync();
        }
        catch (Exception ex)
        {
            BoostStatus.Text = $"Não foi possível acelerar: {ex.Message}";
        }
        finally
        {
            _boostBusy = false;
            SyncBoostEnabled();
        }
    }

}
