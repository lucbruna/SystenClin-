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
        AppsSummary.ToolTip = null;
        AppsStatusText.Text = "Procurando atualizações…";
        AppsProgress.IsIndeterminate = true;
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

        // Um pacote só não tem total conhecido: a barra segue indeterminada,
        // mas a fase + o tempo decorrido mostram que o winget está trabalhando.
        AppsProgress.IsIndeterminate = true;
        SetAppsPhase($"Atualizando {row.Name}");
        SetAppsBusyUi(true);

        string message;
        bool reload = false;
        bool notApplicable = false;
        string? details = null;

        try
        {
            var result = await Task.Run(() => AppUpdater.Update(id, ct, LinePhaseCallback()));

            if (result.Success)
            {
                reload = true;
                message = $"{row.Name} atualizado.";
            }
            else if (result.NotApplicable)
            {
                // winget: há versão nova, mas ela não se aplica (escopo/arquitetura/idioma).
                reload = true;
                notApplicable = true;
                message = $"{row.Name}: a atualização disponível não se aplica a este sistema " +
                          "(escopo, arquitetura ou idioma diferentes da instalação atual).";
                details = result.Output;
            }
            else
            {
                message = result.Error ?? $"Não foi possível atualizar {row.Name}.";
                var hint = AppUpdater.FirstErrorLine(result.Output);
                if (hint is not null)
                    message += $" {hint}";
                if (result.Output?.Contains("elevation", StringComparison.OrdinalIgnoreCase) == true)
                    message += " Execute o Systenclin como administrador para atualizar este pacote.";
                details = result.Output;
            }
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

        if (reload)
        {
            SetAppsPhase(null);

            // A lista precisa refletir a realidade depois da atualização.
            await LoadAppsAsync(message);

            // Atualizou, mas o winget segue listando: a versão instalada não foi
            // confirmada (ex.: pacote com versão desconhecida). Melhor dizer isso
            // do que deixar o usuário achando que nada aconteceu.
            if (!notApplicable && _appRows.Any(r => r.Id == id))
                AppsSummary.Text += " O winget ainda lista este aplicativo: a versão instalada não pôde ser confirmada.";
        }
        else
        {
            SetAppsPhase(null);
            AppsStatusText.Text = "";
            AppsSummary.Text = message;
            AppsSummary.ToolTip = details;
            SetAppsBusyUi(false);
        }
    }

    private async void AppsUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_appsBusy || _uninsBusy || !_wingetAvailable || _appRows.Count == 0) return;

        _appsBusy = true;
        _appsCts = new CancellationTokenSource();
        var ct = _appsCts.Token;

        SetAppsBusyUi(true);

        // Atualiza um por um (em vez de "winget upgrade --all") para mostrar o
        // progresso real de cada pacote e citar por nome os que falharem.
        var targets = _appRows.ToList();

        // O total é conhecido: a barra vira determinada (progresso real).
        AppsProgress.IsIndeterminate = false;
        AppsProgress.Maximum = targets.Count;
        AppsProgress.Value = 0;

        var notApplicable = new List<string>();
        var failed = new List<string>();
        var details = new List<string>();
        int done = 0, updated = 0;
        bool cancelled = false;

        try
        {
            foreach (var row in targets)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                SetAppsPhase($"Atualizando {done + 1}/{targets.Count} — {row.Name}");

                var result = await Task.Run(() => AppUpdater.Update(row.Id, ct, LinePhaseCallback()));
                done++;
                AppsProgress.Value = done;

                if (result.Success)
                {
                    updated++;
                }
                else if (result.NotApplicable)
                {
                    notApplicable.Add(row.Name);
                    details.Add($"{row.Name}: {result.Output ?? "sem saída"}");
                }
                else if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                else
                {
                    var hint = AppUpdater.FirstErrorLine(result.Output);
                    failed.Add(hint is null ? row.Name : $"{row.Name} ({hint})");
                    details.Add($"{row.Name}: {result.Output ?? "sem saída"}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            details.Add($"Erro inesperado: {ex.Message}");
        }
        finally
        {
            _appsBusy = false;
            _appsCts?.Dispose();
            _appsCts = null;
        }

        string message;
        if (cancelled && done == 0)
        {
            message = "Operação cancelada.";
        }
        else
        {
            var parts = new List<string> { $"{updated} de {targets.Count} atualizados." };
            if (notApplicable.Count > 0) parts.Add($"Não aplicáveis: {Summarize(notApplicable)}.");
            if (failed.Count > 0) parts.Add($"Falhas: {Summarize(failed)}.");
            if (cancelled) parts.Insert(0, $"Cancelado ({done} de {targets.Count} tentados).");
            message = string.Join(" ", parts);
        }

        SetAppsPhase(null);
        await LoadAppsAsync(message);
        AppsSummary.ToolTip = details.Count == 0 ? null : string.Join(Environment.NewLine, details);
    }

    private void SetAppsBusyUi(bool busy)
    {
        AppsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AppsCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AppsScanButton.IsEnabled = !busy && _wingetAvailable;
        AppsUpdateAllButton.IsEnabled = !busy && _appRows.Count > 0;

        // Nada de IsEnabled na lista: o estado desabilitado do ListBox pinta cores
        // de tema fora da paleta escura (fundo claro, texto ilegível). Bloquear o
        // clique e esmaecer mantém tudo legível durante a operação.
        AppsList.IsHitTestVisible = !busy;
        AppsList.Opacity = busy ? 0.55 : 1.0;
    }

    // ── Fase ao vivo durante a atualização ──────────────────────────────
    // O winget não emite percentual quando a saída é redirecionada (a barra
    // animada é só do console). O que ele emite são marcos ("Baixando…",
    // "Iniciando a instalação…"): mostramos a fase + tempo decorrido, que
    // prova que o trabalho continua mesmo nos silêncios longos.

    /// <summary>
    /// Callback de linha do winget: mapeia marcos de fase e aplica na thread da
    /// janela; callback atrasado de um pacote já concluído é ignorado (versão).
    /// </summary>
    private Action<string> LinePhaseCallback() => line =>
    {
        var phase = AppUpdater.PhaseFromLine(line);
        if (phase is null) return;

        var version = _appsPhaseVersion;
        Dispatcher.BeginInvoke(new Action(() => ApplyAppsPhase(version, phase)));
    };

    /// <summary>Inicia (base não nulo) ou encerra (base nulo) a linha de fase do update.</summary>
    private void SetAppsPhase(string? baseText)
    {
        _appsPhaseVersion++; // invalida callbacks pendentes do pacote anterior

        if (baseText is null)
        {
            _appsPhaseBase = null;
            _appsPhaseLabel = null;
            _appsTicker?.Stop();
            return;
        }

        if (_appsPhaseBase != baseText)
        {
            _appsPhaseBase = baseText;
            _appsPhaseLabel = null;
            _appsPhaseStart = DateTime.UtcNow;
        }

        if (_appsTicker is null)
        {
            _appsTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _appsTicker.Tick += (_, _) => RenderAppsPhase();
        }

        _appsTicker.Start();
        RenderAppsPhase();
    }

    /// <summary>Aplica um novo rótulo de fase se ele ainda for do pacote em andamento.</summary>
    private void ApplyAppsPhase(int version, string label)
    {
        if (version != _appsPhaseVersion || _appsPhaseBase is null)
            return;

        _appsPhaseLabel = label;
        RenderAppsPhase();
    }

    private void RenderAppsPhase()
    {
        if (_appsPhaseBase is null)
            return;

        var elapsed = DateTime.UtcNow - _appsPhaseStart;
        var time = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

        AppsStatusText.Text = _appsPhaseLabel is null
            ? $"{_appsPhaseBase} · {time}"
            : $"{_appsPhaseBase} · {_appsPhaseLabel} · {time}";
    }

    /// <summary>"a; b; c…" — resumo curto de nomes/motivos para a barra de status.</summary>
    private static string Summarize(List<string> items)
        => items.Count <= 3
            ? string.Join("; ", items)
            : string.Join("; ", items.Take(3)) + "…";

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
        UninsFilterBox.IsHitTestVisible = !busy;
        UninsList.IsHitTestVisible = !busy;
        UninsList.Opacity = busy ? 0.55 : 1.0;
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
