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
    //  Aplicativos
    // ════════════════════════════════════════════════════════════════════════

    private void ShowAppsTab()
    {
        HeaderTitle.Text = "Aplicativos";
        SubtitleText.Text = "Mantenha os programas instalados atualizados pelo winget.";

        BrowserWarning.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        OptimizePanel.Visibility = Visibility.Collapsed;
        DuplicatesPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        LargeFilesPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Visible;

        // A sondagem do winget pode levar ~1 s: roda uma única vez, sem travar a UI.
        if (!_appsLoaded && !_appsLoading)
            _ = InitializeAppsAsync();
    }

    private void AppsCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _appsCts?.Cancel(); } catch { }
    }

    private async Task InitializeAppsAsync()
    {
        if (_appsLoading) return;
        _appsLoading = true;

        AppsScanButton.IsEnabled = false;
        AppsStatusText.Text = "Verificando o winget…";

        try
        {
            _wingetAvailable = await Task.Run(() => AppUpdater.IsAvailable);

            // O desinstalador usa o mesmo winget, mas mantém a própria sondagem.
            if (_wingetAvailable)
                _uninsAvailable = await Task.Run(() => AppUninstaller.IsAvailable);

            if (_wingetAvailable)
            {
                AppsStatusText.Text = "Clique em Procurar atualizações.";
                AppsEmptyState.Text = "Clique em Procurar atualizações.";
            }
            else
            {
                AppsStatusText.Text = "winget não encontrado neste sistema.";
                AppsEmptyState.Text = "winget não encontrado neste sistema.";
            }

            if (_uninsAvailable)
            {
                if (!_uninsLoaded)
                    UninsStatusText.Text = "Clique em Carregar lista para exibir os programas instalados.";
            }
            else
            {
                UninsStatusText.Text = "winget não está disponível neste sistema.";
                UninsLoadButton.IsEnabled = false;
            }

            _appsLoaded = true;
        }
        catch (Exception ex)
        {
            AppsStatusText.Text = $"Não foi possível verificar o winget: {ex.Message}";
            AppsEmptyState.Text = "Não foi possível verificar o winget.";
            UninsStatusText.Text = "Não foi possível verificar o winget.";
        }
        finally
        {
            _appsLoading = false;
            AppsScanButton.IsEnabled = _wingetAvailable && !_appsBusy;
        }
    }

    private async void AppsScan_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private async Task LoadAppsAsync(string? summaryAfterLoad = null)
    {
        if (_appsBusy || _uninsBusy || !_wingetAvailable) return;

        _appsBusy = true;
        _appsCts = new CancellationTokenSource();
        var ct = _appsCts.Token;

        _appRows.Clear();
        AppsList.Visibility = Visibility.Collapsed;
        AppsEmptyState.Visibility = Visibility.Collapsed;
        AppsSummary.Text = "";
        AppsStatusText.Text = "Procurando atualizações…";
        SetAppsBusyUi(true);

        try
        {
            var result = await Task.Run(() => AppUpdater.ListUpgrades(ct));

            if (result.Success)
            {
                foreach (var package in result.Packages)
                    _appRows.Add(new AppRow(package));

                if (_appRows.Count == 0)
                {
                    AppsEmptyState.Text = "Nenhuma atualização disponível.";
                    AppsEmptyState.Visibility = Visibility.Visible;
                    AppsSummary.Text = summaryAfterLoad ?? "Nenhuma atualização disponível.";
                }
                else
                {
                    AppsList.Visibility = Visibility.Visible;
                    AppsSummary.Text = summaryAfterLoad ?? (_appRows.Count == 1
                        ? "1 atualização disponível."
                        : $"{_appRows.Count} atualizações disponíveis.");
                }
            }
            else
            {
                AppsEmptyState.Text = "Clique em Procurar atualizações.";
                AppsEmptyState.Visibility = Visibility.Visible;
                AppsSummary.Text = result.Error ?? "Não foi possível procurar atualizações.";
            }
        }
        catch (OperationCanceledException)
        {
            AppsEmptyState.Text = "Clique em Procurar atualizações.";
            AppsEmptyState.Visibility = Visibility.Visible;
            AppsSummary.Text = "Busca cancelada.";
        }
        catch (Exception ex)
        {
            AppsEmptyState.Text = "Clique em Procurar atualizações.";
            AppsEmptyState.Visibility = Visibility.Visible;
            AppsSummary.Text = $"Não foi possível procurar atualizações: {ex.Message}";
        }
        finally
        {
            AppsStatusText.Text = _wingetAvailable ? "" : "winget não encontrado neste sistema.";
            _appsBusy = false;
            _appsCts?.Dispose();
            _appsCts = null;
            SetAppsBusyUi(false);
        }
    }

    private async void AppsUpdateOne_Click(object sender, RoutedEventArgs e)
    {
        if (_appsBusy || _uninsBusy) return;
        if (sender is not Button { Tag: string id } button) return;
        if (button.DataContext is not AppRow row) return;

        _appsBusy = true;
        _appsCts = new CancellationTokenSource();
        var ct = _appsCts.Token;

        AppsStatusText.Text = $"Atualizando {row.Name}…";
        SetAppsBusyUi(true);

        string message;
        bool success = false;

        try
        {
            var result = await Task.Run(() => AppUpdater.Update(id, ct));
            success = result.Success;
            message = result.Success
                ? $"{row.Name} atualizado."
                : result.Error ?? $"Não foi possível atualizar {row.Name}.";
        }
        catch (OperationCanceledException)
        {
            message = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            message = $"Não foi possível atualizar {row.Name}: {ex.Message}";
        }
        finally
        {
            _appsBusy = false;
            _appsCts?.Dispose();
            _appsCts = null;
        }

        if (success)
        {
            // A lista precisa refletir a realidade depois da atualização.
            await LoadAppsAsync(message);
        }
        else
        {
            AppsStatusText.Text = "";
            AppsSummary.Text = message;
            SetAppsBusyUi(false);
        }
    }

    private async void AppsUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_appsBusy || _uninsBusy || !_wingetAvailable || _appRows.Count == 0) return;

        _appsBusy = true;
        _appsCts = new CancellationTokenSource();
        var ct = _appsCts.Token;

        AppsStatusText.Text = "Atualizando tudo…";
        SetAppsBusyUi(true);

        string message;
        bool success = false;

        try
        {
            var result = await Task.Run(() => AppUpdater.UpdateAll(ct));
            success = result.Success;
            message = result.Success
                ? "Atualizações concluídas."
                : result.Error ?? "Não foi possível atualizar os aplicativos.";
        }
        catch (OperationCanceledException)
        {
            message = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            message = $"Não foi possível atualizar os aplicativos: {ex.Message}";
        }
        finally
        {
            _appsBusy = false;
            _appsCts?.Dispose();
            _appsCts = null;
        }

        if (success)
        {
            // A lista precisa refletir a realidade depois da atualização.
            await LoadAppsAsync(message);
        }
        else
        {
            AppsStatusText.Text = "";
            AppsSummary.Text = message;
            SetAppsBusyUi(false);
        }
    }

    private void SetAppsBusyUi(bool busy)
    {
        AppsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AppsCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AppsScanButton.IsEnabled = !busy && _wingetAvailable;
        AppsUpdateAllButton.IsEnabled = !busy && _appRows.Count > 0;
        AppsList.IsEnabled = !busy;
    }

    // ── Desinstalação ───────────────────────────────────────────────────────

    private async void UninsLoad_Click(object sender, RoutedEventArgs e)
        => await LoadInstalledAsync();

    /// <summary>
    /// Carrega a lista de programas instalados. Sob demanda (clique), porque a
    /// consulta ao winget custa alguns segundos.
    /// </summary>
    private async Task LoadInstalledAsync()
    {
        if (_uninsBusy || _appsBusy) return;

        if (!_uninsAvailable)
        {
            UninsStatusText.Text = "winget não está disponível neste sistema.";
            UninsLoadButton.IsEnabled = false;
            return;
        }

        _uninsBusy = true;
        _uninsCts = new CancellationTokenSource();
        var ct = _uninsCts.Token;

        ResetUninsConfirmation();
        SetUninsBusyUi(true);

        var progress = new Progress<string>(text => UninsStatusText.Text = text);

        try
        {
            var apps = await AppUninstaller.ListInstalledAsync(progress, ct);

            _uninsAll = apps.ToList();
            _uninsLoaded = true;
            FillUninsRows();

            UninsStatusText.Text = _uninsAll.Count == 1
                ? "1 programa encontrado."
                : $"{_uninsAll.Count} programas encontrados.";
        }
        catch (OperationCanceledException)
        {
            UninsStatusText.Text = "Carregamento cancelado.";
        }
        catch (Exception ex)
        {
            UninsStatusText.Text = $"Não foi possível listar os programas: {ex.Message}";
        }
        finally
        {
            _uninsBusy = false;
            _uninsCts?.Dispose();
            _uninsCts = null;
            SetUninsBusyUi(false);
        }
    }

    /// <summary>Aplica o filtro pelo nome e repovoa a lista de programas.</summary>
    private void FillUninsRows()
    {
        var filter = UninsFilterBox.Text.Trim();

        var rows = string.IsNullOrEmpty(filter)
            ? _uninsAll
            : _uninsAll.Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        UninsList.ItemsSource = rows.Select(a => new UninsRow(a)).ToList();
    }

    private void UninsFilter_TextChanged(object sender, TextChangedEventArgs e)
        => FillUninsRows();

    private void SetUninsBusyUi(bool busy)
    {
        UninsFilterBox.IsEnabled = !busy;
        UninsList.IsEnabled = !busy;
        UninsLoadButton.IsEnabled = !busy && _uninsAvailable;
    }

    private void UninsItem_Click(object sender, RoutedEventArgs e)
    {
        if (_uninsBusy || _appsBusy) return;
        if (sender is not Button { Tag: string id } button) return;
        if (button.DataContext is not UninsRow row) return;

        // Segundo clique dentro da janela de ~3 s confirma de verdade.
        if (_uninsConfirmPid == id && DateTime.UtcNow <= _uninsConfirmUntil)
        {
            ResetUninsConfirmation();
            _ = UninstallAppAsync(row);
            return;
        }

        // Primeiro clique (ou em outro botão): rearma a confirmação neste botão.
        ResetUninsConfirmation();

        _uninsConfirmPid = id;
        _uninsConfirmRow = row;
        _uninsConfirmUntil = DateTime.UtcNow.AddSeconds(3);
        row.IsConfirming = true;

        if (_uninsConfirmTimer is null)
        {
            _uninsConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _uninsConfirmTimer.Tick += UninsConfirmTimer_Tick;
        }

        _uninsConfirmTimer.Stop();
        _uninsConfirmTimer.Start();
    }

    private async Task UninstallAppAsync(UninsRow row)
    {
        if (_uninsBusy || _appsBusy) return;

        _uninsBusy = true;
        _uninsCts = new CancellationTokenSource();
        var ct = _uninsCts.Token;

        var id = row.Id;
        var name = row.Name;

        UninsStatusText.Text = $"Desinstalando {name}…";
        SetUninsBusyUi(true);

        try
        {
            var result = await AppUninstaller.UninstallAsync(id, ct);

            if (result.Success)
            {
                // A lista precisa refletir a realidade depois da desinstalação.
                _uninsAll.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
                FillUninsRows();
                UninsStatusText.Text = $"{name} removido com sucesso.";
            }
            else
            {
                UninsStatusText.Text = $"Falha ao desinstalar {name}: {result.Error ?? "erro desconhecido."}";
            }
        }
        catch (OperationCanceledException)
        {
            UninsStatusText.Text = "Desinstalação cancelada.";
        }
        catch (Exception ex)
        {
            UninsStatusText.Text = $"Falha ao desinstalar {name}: {ex.Message}";
        }
        finally
        {
            _uninsBusy = false;
            _uninsCts?.Dispose();
            _uninsCts = null;
            SetUninsBusyUi(false);
        }
    }

    private void UninsConfirmTimer_Tick(object? sender, EventArgs e)
        => ResetUninsConfirmation();

    private void ResetUninsConfirmation()
    {
        _uninsConfirmTimer?.Stop();

        if (_uninsConfirmRow is { } row)
            row.IsConfirming = false;

        _uninsConfirmRow = null;
        _uninsConfirmPid = null;
        _uninsConfirmUntil = default;
    }

}
