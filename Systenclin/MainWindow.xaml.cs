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
    private readonly Scanner _fileScanner = new();
    private readonly Cleaner _fileCleaner = new();
    private readonly PrivacyScanner _privacyScanner = new();
    private readonly PrivacyCleaner _privacyCleaner = new();

    // Última leitura de navegadores abertos, atualizada fora da UI thread.
    private IReadOnlyList<string> _runningBrowsers = [];

    private ObservableCollection<ItemRow> _fileRows = [];
    private ObservableCollection<ItemRow> _privacyRows = [];

    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _syncing;

    // Otimização: carregamento e ações próprias, independentes do fluxo
    // de análise/limpeza (o _busy continua sendo só das abas de arquivos).
    private bool _optimizeLoaded;
    private bool _optimizeLoading;
    private bool _startupBusy;
    private bool _trimBusy;
    private bool _dnsBusy;
    private bool _boostBusy;
    private bool _netBusy;
    private bool _netLoaded;

    // Monitor de uso (aba Otimização): amostras de 1 s com histórico de 60 s.
    private const int MonitorHistoryLimit = 60;
    private DispatcherTimer? _monitorTimer;
    private readonly Queue<double> _monitorCpuHistory = new();
    private readonly Queue<double> _monitorMemHistory = new();

    // Lixeira (aba Otimização): consulta e limpeza pela API do shell, com
    // confirmação em dois cliques; sem CTS, porque a chamada não é cancelável.
    private bool _binLoaded;
    private bool _binBusy;
    private DateTime _binConfirmUntil;
    private DispatcherTimer? _binConfirmTimer;

    // Barra de status fixa no rodapé: CPU/RAM ao vivo. Amostragem mais espaçada
    // que o monitor da aba Otimização. Sample() é seguro para chamadas
    // concorrentes (baseline sob lock); com a aba Otimização visível as janelas
    // de delta ficam menores — aceitável.
    private DispatcherTimer? _statusTimer;

    // Duplicados: fluxo próprio, também independente do _busy das abas de arquivos.
    private readonly ObservableCollection<DuplicateGroupRow> _duplicateGroups = [];
    private CancellationTokenSource? _duplicatesCts;
    private bool _dupBusy;
    private string? _duplicateRoot;

    // Disco do sistema (aba Otimização).
    private CancellationTokenSource? _diskCts;
    private bool _diskBusy;

    // Processos (aba Otimização): lista e encerramento próprios, sem CTS.
    private bool _procLoaded;
    private bool _procBusy;
    private int? _procConfirmPid;
    private DateTime _procConfirmUntil;
    private ProcRow? _procConfirmRow;
    private DispatcherTimer? _procConfirmTimer;

    // Atalhos: busca e reciclagem próprias, independentes do _busy das abas de arquivos.
    private readonly ObservableCollection<ShortcutRow> _shortcutRows = [];
    private CancellationTokenSource? _shortcutsCts;
    private bool _shortcutBusy;

    // Aplicativos: listagem e atualização via winget, também independentes do _busy.
    private readonly ObservableCollection<AppRow> _appRows = [];
    private CancellationTokenSource? _appsCts;
    private bool _appsBusy;
    private bool _appsLoaded;
    private bool _appsLoading;
    private bool _wingetAvailable;

    // Desinstalação (aba Aplicativos): lista própria via winget, independente
    // do _busy e com confirmação em dois cliques, como o card de Processos.
    private List<InstalledApp> _uninsAll = [];
    private CancellationTokenSource? _uninsCts;
    private bool _uninsBusy;
    private bool _uninsLoaded;
    private bool _uninsAvailable;
    private string? _uninsConfirmPid;
    private DateTime _uninsConfirmUntil;
    private UninsRow? _uninsConfirmRow;
    private DispatcherTimer? _uninsConfirmTimer;

    // Arquivos grandes: varredura e reciclagem próprias, independentes do _busy.
    private readonly ObservableCollection<LargeFileRow> _largeRows = [];
    private CancellationTokenSource? _largeCts;
    private bool _largeBusy;
    private bool _largeLoaded;

    // Espaço em disco: análise e navegação próprias, independentes do _busy.
    private readonly ObservableCollection<SpaceRow> _spaceRows = [];
    private CancellationTokenSource? _spaceCts;
    private bool _spaceBusy;
    private bool _spaceLoaded;
    private string _spaceRoot = "";

    // Ponto de restauração: aviso de falha pendente para reexibir ao fim da limpeza.
    private string? _restorePointWarning;

    private readonly bool _elevated = ScanTargets.IsElevated;

    public MainWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _fileRows;
        SpaceList.ItemsSource = _spaceRows;
        AppsList.ItemsSource = _appRows;
        DuplicatesList.ItemsSource = _duplicateGroups;
        ShortcutsList.ItemsSource = _shortcutRows;
        LargeFilesList.ItemsSource = _largeRows;
        Loaded += (_, _) => { UpdateFreeSpace(); StartStatusBar(); };

        RestorePointCheck.IsChecked = _elevated;
        if (!_elevated)
        {
            RestorePointCheck.IsEnabled = false;
            RestorePointCheck.ToolTip = "Requer administrador. Abra o programa como administrador.";
        }

        OptimizeDiskButton.IsEnabled = _elevated;
        if (!_elevated)
            OptimizeDiskButton.ToolTip = "Requer administrador";

        NetRenewButton.IsEnabled = _elevated;
        NetArpButton.IsEnabled = _elevated;
        if (!_elevated)
        {
            NetRenewButton.ToolTip = "Requer administrador. Abra o programa como administrador.";
            NetArpButton.ToolTip = "Requer administrador. Abra o programa como administrador.";
        }

        // Fechar a janela no meio de uma operação precisa cancelá-la — sem isso
        // o processo morre no meio de uma limpeza.
        Closing += (_, _) =>
        {
            try { _cts?.Cancel(); } catch { }
            try { _duplicatesCts?.Cancel(); } catch { }
            try { _diskCts?.Cancel(); } catch { }
            try { _shortcutsCts?.Cancel(); } catch { }
            try { _appsCts?.Cancel(); } catch { }
            try { _uninsCts?.Cancel(); } catch { }
            try { _largeCts?.Cancel(); } catch { }
            try { _spaceCts?.Cancel(); } catch { }
            try { _monitorTimer?.Stop(); } catch { }
            try { _statusTimer?.Stop(); } catch { }
        };
    }

    private bool IsPrivacyTab => TabPrivacy.IsChecked == true;

    private bool IsOptimizeTab => TabOptimize.IsChecked == true;

    private bool IsDuplicatesTab => TabDuplicates.IsChecked == true;

    private bool IsShortcutsTab => TabShortcuts.IsChecked == true;

    private bool IsAppsTab => TabApps.IsChecked == true;

    private bool IsLargeFilesTab => TabLargeFiles.IsChecked == true;

    private bool IsSpaceTab => TabSpace.IsChecked == true;

    private ObservableCollection<ItemRow> ActiveRows => IsPrivacyTab ? _privacyRows : _fileRows;

    /// <summary>
    /// Troca a coleção inteira de uma vez: uma única notificação em vez de
    /// uma por linha (e o campo acompanha, porque a coleção é substituída).
    /// </summary>
    private void SetRows(bool privacy, IEnumerable<ItemRow> rows)
    {
        var collection = new ObservableCollection<ItemRow>(rows);

        if (privacy) _privacyRows = collection;
        else _fileRows = collection;

        if (IsPrivacyTab == privacy)
            ItemsList.ItemsSource = collection;
    }

    private ItemRow CreateRow(CleanableItem item)
    {
        var row = new ItemRow(item);
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    private ItemRow CreateRow(PrivacyItem item)
    {
        var row = new ItemRow(item);
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    /// <summary>
    /// Caminho único para o resumo da seleção: dispara quando uma linha muda
    /// IsChecked (o antigo Checked="ItemCheck_Changed" do template disparava
    /// também na reciclagem de containers e em mudanças programáticas).
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(ItemRow.IsChecked)) return;
        RefreshSelectionSummary();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Abas
    // ════════════════════════════════════════════════════════════════════════

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        SwitchTabPanels();

        // O monitor de uso vive só enquanto a aba Otimização está visível;
        // nada de timer girando (nem gráfico se redesenhando) escondido.
        if (IsLoaded && !_busy)
        {
            if (IsOptimizeTab) StartMonitor();
            else StopMonitor();
        }
    }

    /// <summary>
    /// Troca os painéis visíveis conforme a aba marcada. Os guards preservam o
    /// comportamento original: sem a janela montada ou com operação em
    /// andamento, nada muda.
    /// </summary>
    private void SwitchTabPanels()
    {
        if (!IsLoaded) return;
        if (_busy) return;

        // Nenhum painel sobrevive à troca de aba: cada Show* reabre só o seu.
        DuplicatesPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Collapsed;
        LargeFilesPanel.Visibility = Visibility.Collapsed;
        SpacePanel.Visibility = Visibility.Collapsed;

        if (IsDuplicatesTab)
        {
            ShowDuplicatesTab();
            return;
        }

        DuplicatesPanel.Visibility = Visibility.Collapsed;

        if (IsShortcutsTab)
        {
            ShowShortcutsTab();
            return;
        }

        ShortcutsPanel.Visibility = Visibility.Collapsed;

        if (IsAppsTab)
        {
            ShowAppsTab();
            return;
        }

        AppsPanel.Visibility = Visibility.Collapsed;

        if (IsLargeFilesTab)
        {
            ShowLargeFilesTab();
            return;
        }

        LargeFilesPanel.Visibility = Visibility.Collapsed;

        if (IsSpaceTab)
        {
            ShowSpaceTab();
            return;
        }

        SpacePanel.Visibility = Visibility.Collapsed;

        if (IsOptimizeTab)
        {
            ShowOptimizeTab();
            return;
        }

        OptimizePanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Collapsed;
        ItemsList.ItemsSource = ActiveRows;
        HeaderTitle.Text = IsPrivacyTab
            ? "Privacidade dos navegadores"
            : "Limpeza de arquivos temporários";

        _ = UpdateBrowserWarning();
        RefreshView();
    }

    /// <summary>
    /// O aviso de navegador aberto não é decorativo: dados de banco (histórico,
    /// cookies, formulários) não podem ser apagados com o navegador rodando.
    /// Cache e abas salvas são arquivos em disco e podem.
    /// </summary>
    private async Task UpdateBrowserWarning()
    {
        if (!IsPrivacyTab)
        {
            BrowserWarning.Visibility = Visibility.Collapsed;
            return;
        }

        // Enumeração de processos fora da UI thread; o snapshot também alimenta
        // o resumo da seleção (PrivacyNote) sem prender a interface.
        var running = await PrivacyCleaner.RunningBrowsersAsync();
        _runningBrowsers = running;

        if (running.Count == 0)
        {
            BrowserWarning.Visibility = Visibility.Collapsed;
            return;
        }

        BrowserWarning.Visibility = Visibility.Visible;
        BrowserWarningTitle.Text = running.Count == 1
            ? $"{running[0]} está aberto"
            : $"Estes navegadores estão abertos: {string.Join(", ", running)}";

        BrowserWarningBody.Text =
            "Histórico, cookies, formulários e permissões não podem ser apagados com o navegador aberto — " +
            "ele manteria os dados na memória e os regravaria. Cache e abas salvas são arquivos em disco e " +
            "podem ser limpos agora. Feche o navegador para limpar o resto.";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Análise
    // ════════════════════════════════════════════════════════════════════════

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
    }

    private async Task ScanAsync()
    {
        if (_busy) return;

        bool privacy = IsPrivacyTab;
        _busy = true;
        _cts = new CancellationTokenSource();

        ActiveRows.Clear();
        SetUi(scanning: true);

        try
        {
            if (privacy)
            {
                var progress = new Progress<PrivacyScanner.Progress>(p =>
                {
                    ScanProgress.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total * 100;
                    ScanProgressText.Text = p.Total == 0
                        ? "Procurando perfis…"
                        : $"{p.Done}/{p.Total} — {p.Found} item(ns) · {p.Current}";
                });

                var items = await _privacyScanner.ScanAsync(progress, _cts.Token);
                SetRows(privacy: true, items.Select(i => CreateRow(i)));
            }
            else
            {
                var progress = new Progress<Scanner.Progress>(p =>
                {
                    ScanProgress.Value = p.TargetsTotal == 0
                        ? 0
                        : (double)p.TargetsDone / p.TargetsTotal * 100;

                    ScanProgressText.Text = p.TargetsTotal == 0
                        ? "Procurando…"
                        : $"{p.TargetsDone}/{p.TargetsTotal} — {CleanableItem.FormatBytes(p.BytesFound)} em {p.ItemsFound} itens";
                });

                var items = await _fileScanner.ScanAsync(progress, _cts.Token);
                SetRows(privacy: false, items.Select(i => CreateRow(i)));
            }

            SetUi(scanning: false, hasResults: ActiveRows.Count > 0);
            RefreshSelectionSummary();
            UpdateBadges();
        }
        catch (OperationCanceledException)
        {
            SetUi(scanning: false);
        }
        catch (Exception ex)
        {
            SetUi(scanning: false);
            MessageBox.Show(this, $"A análise falhou:\n\n{ex.Message}",
                "Systenclin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            UpdateFreeSpace();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Limpeza
    // ════════════════════════════════════════════════════════════════════════

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var selected = ActiveRows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0) return;

        if (IsPrivacyTab) await CleanPrivacyAsync(selected);
        else await CleanFilesAsync(selected);
    }

    // ── Arquivos ────────────────────────────────────────────────────────────

    private async Task CleanFilesAsync(List<ItemRow> selected)
    {
        var items = selected.Select(r => r.FileItem!).ToList();

        long totalBytes = items.Sum(i => i.SizeBytes);
        bool hasDirectory = items.Any(i => i.IsDirectory);
        bool hasMedium = items.Any(i => i.Risk != RiskLevel.Low);

        var message = $"Liberar {CleanableItem.FormatBytes(totalBytes)} em {items.Count} item(ns)?\n\n";

        if (hasDirectory)
            message += "• Pastas de cache são removidas definitivamente (não vão para a Lixeira).\n";
        if (items.Any(i => !i.IsDirectory))
            message += "• Arquivos soltos vão para a Lixeira e podem ser restaurados.\n";
        if (hasMedium)
            message += "• Você incluiu itens de risco médio ou alto.\n";

        if (MessageBox.Show(this, message, "Confirmar limpeza",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _busy = true;
        SetUi(cleaning: true);
        _cts = new CancellationTokenSource();

        var progress = new Progress<Cleaner.Progress>(p =>
        {
            SelectionSummary.Text = $"Limpando… {p.Done}/{p.Total}";
            SelectionNote.Text = $"{CleanableItem.FormatBytes(p.BytesFreed)} liberados até agora";
        });

        try
        {
            // Ponto de restauração antes de tocar em qualquer coisa — se falhar,
            // avisa e segue: nunca bloqueia a limpeza.
            await CreateRestorePointIfRequestedAsync();

            var results = await _fileCleaner.CleanAsync(items, progress, _cts.Token);

            long freed = results.Where(r => r.Succeeded).Sum(r => r.BytesFreed);
            int failedCount = results.Count(r => !r.Succeeded);

            var cleaned = results.Where(r => r.Succeeded).Select(r => r.Item.Path)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

            SetRows(privacy: false, _fileRows.Where(r => !cleaned.Contains(r.FileItem!.Path)));

            SetUi(cleaning: false, hasResults: _fileRows.Count > 0);
            UpdateBadges();

            ShowOutcome(freed, failedCount,
                results.Where(r => !r.Succeeded)
                       .Select(r => $"• {Path.GetFileName(r.Item.Path.TrimEnd('\\'))}: {r.Error}"));

            RefreshSelectionSummary();
            ShowRestoreWarningIfAny();
        }
        catch (OperationCanceledException)
        {
            SetUi(cleaning: false, hasResults: _fileRows.Count > 0);
            SelectionSummary.Text = "Cancelado";
            SelectionNote.Text = "Os itens já removidos continuam removidos.";
            SubtitleText.Text = "Cancelado — os itens já removidos continuam removidos.";
        }
        catch (Exception ex)
        {
            SetUi(cleaning: false, hasResults: _fileRows.Count > 0);
            MessageBox.Show(this, $"A limpeza falhou:\n\n{ex.Message}",
                "Systenclin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _restorePointWarning = null;
            _cts?.Dispose();
            _cts = null;
            UpdateFreeSpace();
        }
    }

    // ── Privacidade ─────────────────────────────────────────────────────────

    private async Task CleanPrivacyAsync(List<ItemRow> selected)
    {
        var items = selected.Select(r => r.PrivacyItem!).ToList();

        // Itens de banco exigem navegador fechado — avisa antes, não depois.
        var running = await PrivacyCleaner.RunningBrowsersAsync();
        var blockedByRunning = items
            .Where(i => i.Kind is not (PrivacyKind.BrowserCache or PrivacyKind.SessionTabs)
                     && running.Contains(i.Profile.Browser.DisplayName()))
            .ToList();

        long bytes = items.Sum(i => i.SizeBytes);
        int records = items.Sum(i => i.RecordCount);

        var message = $"Apagar {records:N0} registro(s) de {items.Count} item(ns)?\n\n";
        message += "• Um backup de cada banco é criado antes de apagar.\n";
        message += "• Use o botão Backups para restaurar se precisar.\n";

        if (items.Any(i => i.Kind == PrivacyKind.Cookies))
            message += "• ATENÇÃO: apagar cookies desconecta você de todas as contas desses sites.\n";

        if (blockedByRunning.Count > 0)
        {
            message += $"\n{blockedByRunning.Count} item(ns) serão PULADOS porque o navegador está aberto:\n";
            foreach (var b in blockedByRunning.Take(5))
                message += $"  – {b.Kind.DisplayName()} ({b.Profile.Browser.DisplayName()})\n";
        }

        if (MessageBox.Show(this, message, "Confirmar limpeza de privacidade",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        _busy = true;
        SetUi(cleaning: true);
        _cts = new CancellationTokenSource();

        var progress = new Progress<PrivacyCleaner.Progress>(p =>
        {
            SelectionSummary.Text = $"Limpando… {p.Done}/{p.Total}";
            SelectionNote.Text = $"{p.Records:N0} registro(s) removidos — {p.Current}";
        });

        try
        {
            // Mesma regra dos arquivos: ponto de restauração antes, falha nunca bloqueia.
            await CreateRestorePointIfRequestedAsync();

            var results = await _privacyCleaner.CleanAsync(items, progress, _cts.Token);

            int removed = results.Where(r => r.Succeeded).Sum(r => r.RecordsRemoved);
            int skipped = results.Count(r => r.Skipped);
            int failedCount = results.Count(r => !r.Succeeded && !r.Skipped);
            int backups = results.Count(r => r.BackupPath is not null);

            // Recarrega a aba: o que sobrou precisa refletir a realidade.
            await ReloadPrivacyAsync();

            var summary = $"Pronto. {removed:N0} registro(s) removidos.";
            if (backups > 0) summary += $" {backups} backup(s) criado(s).";
            if (skipped > 0) summary += $" {skipped} item(ns) pulados.";
            if (failedCount > 0) summary += $" {failedCount} falharam.";

            var details = results
                .Where(r => r.Skipped || !r.Succeeded)
                .Select(r => $"• {r.Item.Title}: {r.Error}")
                .Take(10)
                .ToList();

            if (details.Count > 0)
                MessageBox.Show(this, summary + "\n\n" + string.Join("\n", details),
                    "Limpeza de privacidade", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(this, summary, "Limpeza de privacidade",
                    MessageBoxButton.OK, MessageBoxImage.Information);

            await UpdateBrowserWarning();
            UpdateBadges();
            ShowRestoreWarningIfAny();
        }
        catch (OperationCanceledException)
        {
            SetUi(cleaning: false, hasResults: _privacyRows.Count > 0);
            SelectionSummary.Text = "Cancelado";
            SelectionNote.Text = "Os itens já removidos continuam removidos.";
            SubtitleText.Text = "Cancelado — os itens já removidos continuam removidos.";
        }
        catch (Exception ex)
        {
            SetUi(cleaning: false, hasResults: _privacyRows.Count > 0);
            MessageBox.Show(this, $"A limpeza falhou:\n\n{ex.Message}",
                "Systenclin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _restorePointWarning = null;
            _cts?.Dispose();
            _cts = null;
            UpdateFreeSpace();
        }
    }

    /// <summary>
    /// Cria o ponto de restauração pedido — no máximo uma vez por ação de limpeza.
    /// Falha nunca bloqueia: vira aviso inline e a limpeza segue normalmente.
    /// </summary>
    private async Task CreateRestorePointIfRequestedAsync()
    {
        if (RestorePointCheck.IsChecked != true || !_elevated) return;

        SelectionSummary.Text = "Criando ponto de restauração…";
        SelectionNote.Text = "Isso pode levar alguns segundos.";

        RestorePointResult result;
        try
        {
            result = await Task.Run(() => RestorePoint.Create("Systenclin — antes da limpeza"));
        }
        catch (Exception ex)
        {
            result = new RestorePointResult(false, ex.Message);
        }

        if (result.Success) return;

        _restorePointWarning =
            $"Não foi possível criar o ponto de restauração: {result.Error} — continuando a limpeza.";
        SubtitleText.Text = _restorePointWarning;
    }

    private void ShowRestoreWarningIfAny()
    {
        if (_restorePointWarning is null) return;

        SubtitleText.Text = _restorePointWarning;
        _restorePointWarning = null;
    }

    private async Task ReloadPrivacyAsync()
    {
        SetRows(privacy: true, []);

        var progress = new Progress<PrivacyScanner.Progress>(p =>
        {
            ScanProgress.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total * 100;
            ScanProgressText.Text = p.Total == 0
                ? "Procurando perfis…"
                : $"{p.Done}/{p.Total} — {p.Found} item(ns) · {p.Current}";
        });

        string? reloadError = null;

        try
        {
            var items = await _privacyScanner.ScanAsync(progress, _cts?.Token ?? CancellationToken.None);
            SetRows(privacy: true, items.Select(i => CreateRow(i)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reloadError = $"Não foi possível recarregar a lista: {ex.Message}";
        }

        SetUi(hasResults: _privacyRows.Count > 0);
        RefreshSelectionSummary();

        if (reloadError is not null)
            SubtitleText.Text = reloadError;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Seleção
    // ════════════════════════════════════════════════════════════════════════

    private void SelectAllCheck_Changed(object sender, RoutedEventArgs e)
    {
        var rows = ActiveRows;
        if (rows.Count == 0) return;

        bool check = SelectAllCheck.IsChecked == true;

        _syncing = true;
        try
        {
            foreach (var row in rows)
                if (row.CanToggle)
                    row.IsChecked = check;
        }
        finally { _syncing = false; }

        RefreshSelectionSummary();
    }

    private void RefreshSelectionSummary()
    {
        var rows = ActiveRows;
        var selected = rows.Where(r => r.IsChecked).ToList();

        long bytes = selected.Sum(r => r.ItemSizeBytes);
        long allBytes = rows.Sum(r => r.ItemSizeBytes);
        int records = selected.Sum(r => r.ItemRecordCount);

        SelectionSummary.Text = selected.Count == 0
            ? $"Nada selecionado — {rows.Count} item(ns) disponíveis, {CleanableItem.FormatBytes(allBytes)}"
            : IsPrivacyTab
                ? $"{records:N0} registro(s) em {selected.Count} item(ns) — {CleanableItem.FormatBytes(bytes)}"
                : $"{CleanableItem.FormatBytes(bytes)} em {selected.Count} item(ns) — {records:N0} arquivo(s)";

        SelectionNote.Text = IsPrivacyTab ? PrivacyNote(selected) : FilesNote(selected);

        CleanButton.IsEnabled = selected.Count > 0 && !_busy;
    }

    private static string FilesNote(List<ItemRow> selected)
    {
        if (selected.Count == 0) return "A Lixeira não é apagada por padrão.";

        var hasDir = selected.Any(r => r.FileItem!.IsDirectory);
        var hasFiles = selected.Any(r => !r.FileItem!.IsDirectory);

        return (hasDir, hasFiles) switch
        {
            (true, true) => "Pastas de cache saem de vez; arquivos soltos vão para a Lixeira.",
            (true, false) => "As pastas de cache serão removidas definitivamente.",
            (false, true) => "Os arquivos vão para a Lixeira e podem ser restaurados.",
            _ => "Nada selecionado.",
        };
    }

    private string PrivacyNote(List<ItemRow> selected)
    {
        if (selected.Count == 0) return "Histórico, cookies e formulários exigem o navegador fechado.";

        var running = _runningBrowsers;
        var blocked = selected.Count(i =>
            i.PrivacyItem!.Kind is not (PrivacyKind.BrowserCache or PrivacyKind.SessionTabs)
            && running.Contains(i.PrivacyItem.Profile.Browser.DisplayName()));

        var parts = new List<string> { "Backup automático antes de apagar." };

        if (selected.Any(i => i.PrivacyItem!.Kind == PrivacyKind.Cookies))
            parts.Add("Cookies desconectam você das contas.");

        if (blocked > 0)
            parts.Add($"{blocked} item(ns) serão pulados (navegador aberto).");

        return string.Join(" ", parts);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Backups e histórico
    // ════════════════════════════════════════════════════════════════════════

    private async void Backups_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backups = await Task.Run(() => PrivacyCleaner.ListBackups());

            if (backups.Count == 0)
            {
                MessageBox.Show(this,
                    "Nenhum backup ainda.\n\nUm backup é criado automaticamente antes de cada limpeza de privacidade.\n\n" +
                    PrivacyCleaner.BackupLocation,
                    "Backups", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long total = backups.Sum(b => b.Size);

            var list = backups.Take(20).Select(b =>
                $"{b.When:dd/MM/yyyy HH:mm}   {CleanableItem.FormatBytes(b.Size),9}   {Path.GetFileName(b.Path)}");

            MessageBox.Show(this,
                $"{backups.Count} backup(s), {CleanableItem.FormatBytes(total)} no total:\n\n" +
                string.Join("\n", list) +
                $"\n\nPasta:\n{PrivacyCleaner.BackupLocation}\n\n" +
                "Para restaurar: feche o navegador, copie o arquivo de volta por cima do banco original.",
                "Backups de privacidade", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível ler os backups:\n\n{ex.Message}",
                "Backups", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await Task.Run(() => new CleanLog().Load());

            if (entries.Count == 0)
            {
                MessageBox.Show(this,
                    "Nenhuma limpeza de arquivos registrada ainda.\n\n" + CleanLog.Location,
                    "Histórico", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var lines = entries.TakeLast(40).Reverse().Select(en =>
            {
                var mark = en.Succeeded ? "ok " : "falhou";
                return $"{en.When:dd/MM HH:mm}  [{mark}]  {CleanableItem.FormatBytes(en.SizeBytes),9}  {en.Category}\n      {en.Path}" +
                       (en.Error is null ? "" : $"\n      → {en.Error}");
            });

            MessageBox.Show(this,
                $"Últimas {Math.Min(40, entries.Count)} operações:\n\n{string.Join("\n\n", lines)}\n\n" +
                $"Histórico completo: {CleanLog.Location}",
                "Histórico de limpeza de arquivos", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível ler o histórico:\n\n{ex.Message}",
                "Histórico", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        if (_diskBusy || _boostBusy || !_elevated) return;

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

    // ── Aceleração em 1 clique ──────────────────────────────────────────────

    /// <summary>Só libera o botão quando nenhuma operação de otimização roda.</summary>
    private bool AnyOptimizeBusy => _boostBusy || _trimBusy || _dnsBusy || _diskBusy || _netBusy;

    /// <summary>
    /// Sincroniza o estado dos botões da aba Otimização: Boost e rede ficam
    /// desabilitados durante qualquer operação; as ações de rede que exigem
    /// administrador só habilitam com o programa elevado.
    /// </summary>
    private void SyncBoostEnabled()
    {
        bool idle = !AnyOptimizeBusy;
        BoostButton.IsEnabled = idle;
        NetRefreshButton.IsEnabled = idle;
        NetRenewButton.IsEnabled = idle && _elevated;
        NetArpButton.IsEnabled = idle && _elevated;
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

    // ── Rede ────────────────────────────────────────────────────────────────

    private void NetRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshNetworkAsync();

    /// <summary>
    /// Lista os adaptadores. Carregada uma única vez na primeira abertura da aba;
    /// depois, só pelo botão Atualizar. Sempre na UI thread, protegida por _netBusy.
    /// </summary>
    private async Task RefreshNetworkAsync()
    {
        if (_netBusy) return;

        _netBusy = true;
        SyncBoostEnabled();
        NetStatus.Visibility = Visibility.Visible;
        NetStatus.Text = "Verificando adaptadores…";

        try
        {
            var adapters = await Task.Run(() => NetworkInfo.List());
            NetList.ItemsSource = adapters.Select(a => new NetRow(a)).ToList();

            NetStatus.Text = adapters.Count == 1
                ? "1 adaptador encontrado."
                : $"{adapters.Count} adaptadores encontrados.";
        }
        catch (Exception ex)
        {
            NetStatus.Text = $"Não foi possível listar os adaptadores: {ex.Message}";
        }
        finally
        {
            _netBusy = false;
            SyncBoostEnabled();
        }
    }

    private async void NetRenew_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOptimizeBusy) return;

        _netBusy = true;
        SyncBoostEnabled();
        NetStatus.Visibility = Visibility.Visible;
        NetStatus.Text = "Renovando o endereço IP…";

        try
        {
            var result = await NetworkInfo.RenewIpAsync();

            NetStatus.Text = result.Success
                ? "Endereço IP renovado."
                : result.Error ?? "Não foi possível renovar o endereço IP.";
        }
        catch (Exception ex)
        {
            NetStatus.Text = $"Não foi possível renovar o endereço IP: {ex.Message}";
        }
        finally
        {
            _netBusy = false;
            SyncBoostEnabled();
        }
    }

    private async void NetArp_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOptimizeBusy) return;

        _netBusy = true;
        SyncBoostEnabled();
        NetStatus.Visibility = Visibility.Visible;
        NetStatus.Text = "Limpando o cache ARP…";

        try
        {
            var result = await Task.Run(() => NetworkInfo.ClearArpCache());

            NetStatus.Text = result.Success
                ? "Cache ARP limpo."
                : result.Error ?? "Não foi possível limpar o cache ARP.";
        }
        catch (Exception ex)
        {
            NetStatus.Text = $"Não foi possível limpar o cache ARP: {ex.Message}";
        }
        finally
        {
            _netBusy = false;
            SyncBoostEnabled();
        }
    }

    // ── Monitor de uso ──────────────────────────────────────────────────────

    /// <summary>
    /// Liga o monitor: cria o timer (1 s) na primeira vez, faz uma leitura
    /// imediata (para o card não abrir vazio) e começa a atualizar. Idempotente
    /// — chamar de novo não duplica o Tick.
    /// </summary>
    private void StartMonitor()
    {
        if (_monitorTimer is null)
        {
            _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _monitorTimer.Tick += MonitorTimer_Tick;
        }

        MonitorTimer_Tick(null, EventArgs.Empty);
        _monitorTimer.Start();
    }

    /// <summary>Desliga o monitor ao sair da aba: para o timer e zera o histórico.</summary>
    private void StopMonitor()
    {
        _monitorTimer?.Stop();

        _monitorCpuHistory.Clear();
        _monitorMemHistory.Clear();
        MonitorCpuLine.Points.Clear();
        MonitorMemLine.Points.Clear();
    }

    /// <summary>
    /// Liga a barra de status do rodapé: cria o timer na primeira chamada e
    /// faz uma atualização imediata, para o rodapé não abrir vazio. Idempotente.
    /// </summary>
    private void StartStatusBar()
    {
        if (_statusTimer is null)
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += StatusTimer_Tick;
        }

        StatusTimer_Tick(null, EventArgs.Empty);
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        var s = SystemMonitor.Sample();

        FooterCpuText.Text = $"CPU {s.CpuPercent:F0}%";
        FooterMemText.Text = s.MemoryTotalBytes > 0
            ? $"RAM {s.MemoryPercent:F0}%"
            : "RAM —";

        FooterMemText.ToolTip = s.MemoryTotalBytes > 0
            ? $"Memória: {CleanableItem.FormatBytes(s.MemoryUsedBytes)} de " +
              $"{CleanableItem.FormatBytes(s.MemoryTotalBytes)} ({s.MemoryPercent:F0}%)"
            : null;
    }

    private void MonitorTimer_Tick(object? sender, EventArgs e)
    {
        var sample = SystemMonitor.Sample();

        Push(_monitorCpuHistory, sample.CpuPercent);
        Push(_monitorMemHistory, sample.MemoryPercent);

        MonitorCpuText.Text = $"CPU: {sample.CpuPercent:F0}%";
        MonitorMemText.Text = sample.MemoryTotalBytes > 0
            ? $"Memória: {CleanableItem.FormatBytes(sample.MemoryUsedBytes)} de " +
              $"{CleanableItem.FormatBytes(sample.MemoryTotalBytes)} ({sample.MemoryPercent:F0}%)"
            : "Memória: —";

        MonitorCpuBar.Value = Math.Clamp(sample.CpuPercent, 0, 100);
        MonitorMemBar.Value = Math.Clamp(sample.MemoryPercent, 0, 100);

        RenderMonitorChart(MonitorCpuChart, MonitorCpuLine, _monitorCpuHistory);
        RenderMonitorChart(MonitorMemChart, MonitorMemLine, _monitorMemHistory);
    }

    /// <summary>Enfileira um valor mantendo no máximo <see cref="MonitorHistoryLimit"/> amostras.</summary>
    private static void Push(Queue<double> history, double value)
    {
        history.Enqueue(value);

        while (history.Count > MonitorHistoryLimit)
            history.Dequeue();
    }

    /// <summary>
    /// Desenha o histórico como uma linha no gráfico: x distribui as amostras
    /// pela largura e y mapeia 0..100% para baixo..cima. Menos de duas amostras
    /// ou gráfico sem largura deixam a linha vazia.
    /// </summary>
    private static void RenderMonitorChart(
        Grid chart, System.Windows.Shapes.Polyline line, Queue<double> history)
    {
        var points = line.Points;
        var width = chart.ActualWidth;
        var height = double.IsNaN(chart.Height) ? chart.ActualHeight : chart.Height;

        if (history.Count < 2 || width <= 0 || height <= 0)
        {
            points.Clear();
            return;
        }

        points.Clear();

        var values = history.ToArray();
        var step = width / (values.Length - 1);

        for (var i = 0; i < values.Length; i++)
        {
            var x = step * i;
            var y = (1 - Math.Clamp(values[i], 0, 100) / 100) * (height - 2) + 1;
            points.Add(new Point(x, y));
        }
    }

    // ── Lixeira ─────────────────────────────────────────────────────────────

    private async void BinRefresh_Click(object sender, RoutedEventArgs e)
        => await RefreshBinAsync();

    /// <summary>
    /// Consulta o conteúdo da lixeira. Carregada uma única vez na primeira
    /// abertura da aba; depois, só pelo botão Verificar.
    /// </summary>
    private async Task RefreshBinAsync()
    {
        if (_binBusy) return;

        _binBusy = true;
        ResetBinConfirmation();
        SetBinBusyUi(true);
        BinStatusText.Text = "Verificando a lixeira…";

        try
        {
            var info = await Task.Run(() => RecycleBinManager.Query());

            BinStatusText.Text = info.ItemCount switch
            {
                0 => "A lixeira está vazia.",
                1 => $"A lixeira tem 1 item ({CleanableItem.FormatBytes(info.SizeBytes)}).",
                _ => $"A lixeira tem {info.ItemCount} itens ({CleanableItem.FormatBytes(info.SizeBytes)}).",
            };
        }
        catch (Exception ex)
        {
            BinStatusText.Text = $"Não foi possível verificar a lixeira: {ex.Message}";
        }
        finally
        {
            _binBusy = false;
            SetBinBusyUi(false);
        }
    }

    private void BinEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (_binBusy) return;

        // Segundo clique dentro da janela de ~3 s confirma de verdade.
        if (DateTime.UtcNow <= _binConfirmUntil)
        {
            ResetBinConfirmation();
            _ = EmptyBinAsync();
            return;
        }

        // Primeiro clique: arma a confirmação e troca o rótulo do próprio botão.
        _binConfirmUntil = DateTime.UtcNow.AddSeconds(3);
        BinEmptyButton.Content = "Confirmar?";

        if (_binConfirmTimer is null)
        {
            _binConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _binConfirmTimer.Tick += BinConfirmTimer_Tick;
        }

        _binConfirmTimer.Stop();
        _binConfirmTimer.Start();
    }

    private async Task EmptyBinAsync()
    {
        if (_binBusy) return;

        _binBusy = true;
        SetBinBusyUi(true);
        BinStatusText.Text = "Esvaziando a lixeira…";

        ProcessActionResult result;
        try
        {
            result = await Task.Run(() => RecycleBinManager.Empty());
        }
        catch (Exception ex)
        {
            result = new ProcessActionResult(false, $"Não foi possível esvaziar a lixeira: {ex.Message}");
        }

        _binBusy = false;

        if (!result.Success)
        {
            BinStatusText.Text = $"Falha ao esvaziar a lixeira: {result.Error}";
            SetBinBusyUi(false);
            return;
        }

        // Sucesso: a consulta de novo confirma a contagem real depois da limpeza.
        await RefreshBinAsync();
        BinStatusText.Text = "Lixeira esvaziada.";
    }

    private void SetBinBusyUi(bool busy)
    {
        BinRefreshButton.IsEnabled = !busy;
        BinEmptyButton.IsEnabled = !busy;
    }

    private void BinConfirmTimer_Tick(object? sender, EventArgs e)
        => ResetBinConfirmation();

    private void ResetBinConfirmation()
    {
        _binConfirmTimer?.Stop();
        _binConfirmUntil = default;
        BinEmptyButton.Content = "Esvaziar";
    }

    // ── Processos ───────────────────────────────────────────────────────────

    private async void ProcRefresh_Click(object sender, RoutedEventArgs e)
        => await LoadProcessesAsync();

    /// <summary>
    /// Lista os maiores consumidores de memória. Carregada uma única vez na
    /// primeira abertura da aba; depois, só pelo botão Atualizar.
    /// </summary>
    private async Task LoadProcessesAsync(string? messageAfterLoad = null)
    {
        if (_procBusy) return;

        _procBusy = true;
        ProcRefreshButton.IsEnabled = false;
        ResetProcConfirmation();
        ProcStatusText.Text = "Carregando processos…";

        try
        {
            var items = await Task.Run(() => ProcessManager.TopByMemory(15));
            ProcList.ItemsSource = items.Select(i => new ProcRow(i)).ToList();

            ProcStatusText.Text = items.Count == 0
                ? "Não foi possível listar processos."
                : messageAfterLoad ?? "";
        }
        catch
        {
            // TopByMemory não lança, mas a lista não pode ficar mentindo se algo falhar.
            ProcList.ItemsSource = null;
            ProcStatusText.Text = "Não foi possível listar processos.";
        }
        finally
        {
            _procBusy = false;
            ProcRefreshButton.IsEnabled = true;
        }
    }

    private void ProcEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_procBusy) return;
        if (sender is not Button button || button.Tag is not int pid ||
            button.DataContext is not ProcRow row)
            return;

        // Segundo clique dentro da janela de ~3 s confirma de verdade.
        if (_procConfirmPid == pid && DateTime.UtcNow <= _procConfirmUntil)
        {
            ResetProcConfirmation();
            _ = EndProcessAsync(row);
            return;
        }

        // Primeiro clique (ou em outro botão): rearma a confirmação neste botão.
        ResetProcConfirmation();

        _procConfirmPid = pid;
        _procConfirmRow = row;
        _procConfirmUntil = DateTime.UtcNow.AddSeconds(3);
        row.IsConfirming = true;

        if (_procConfirmTimer is null)
        {
            _procConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _procConfirmTimer.Tick += ProcConfirmTimer_Tick;
        }

        _procConfirmTimer.Stop();
        _procConfirmTimer.Start();
    }

    private async Task EndProcessAsync(ProcRow row)
    {
        if (_procBusy) return;

        _procBusy = true;
        ProcRefreshButton.IsEnabled = false;
        ProcStatusText.Text = $"Encerrando {row.Name}…";

        ProcessActionResult result;
        try
        {
            result = await Task.Run(() => ProcessManager.End(row.Pid));
        }
        catch (Exception ex)
        {
            result = new ProcessActionResult(false, $"Não foi possível encerrar o processo: {ex.Message}");
        }

        _procBusy = false;
        ProcRefreshButton.IsEnabled = true;

        if (result.Success)
        {
            // A lista precisa refletir a realidade depois do encerramento.
            await LoadProcessesAsync($"Solicitação de encerramento enviada para {row.Name}.");
            return;
        }

        ProcStatusText.Text = result.Error ?? "Não foi possível encerrar o processo.";
    }

    private void ProcConfirmTimer_Tick(object? sender, EventArgs e)
        => ResetProcConfirmation();

    private void ResetProcConfirmation()
    {
        _procConfirmTimer?.Stop();

        if (_procConfirmRow is { } row)
            row.IsConfirming = false;

        _procConfirmRow = null;
        _procConfirmPid = null;
        _procConfirmUntil = default;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Duplicados
    // ════════════════════════════════════════════════════════════════════════

    private void ShowDuplicatesTab()
    {
        HeaderTitle.Text = "Duplicados";
        SubtitleText.Text = "Encontre arquivos idênticos em uma pasta e libere espaço com segurança.";

        BrowserWarning.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        OptimizePanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Collapsed;
        LargeFilesPanel.Visibility = Visibility.Collapsed;
        DuplicatesPanel.Visibility = Visibility.Visible;
    }

    private void DuplicatesBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolher a pasta para procurar duplicados",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        _duplicateRoot = dialog.FolderName;
        DuplicatesPathBox.Text = dialog.FolderName;
        DuplicatesScanButton.IsEnabled = true;
    }

    private void DuplicatesCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _duplicatesCts?.Cancel(); } catch { }
    }

    private async void DuplicatesScan_Click(object sender, RoutedEventArgs e)
    {
        if (_dupBusy || string.IsNullOrWhiteSpace(_duplicateRoot)) return;

        _dupBusy = true;
        _duplicatesCts = new CancellationTokenSource();
        var ct = _duplicatesCts.Token;

        _duplicateGroups.Clear();
        DuplicatesList.Visibility = Visibility.Collapsed;
        DuplicatesEmptyState.Visibility = Visibility.Collapsed;
        DuplicatesSummary.Text = "";
        DuplicatesProgressPanel.Visibility = Visibility.Visible;
        DuplicatesProgressText.Text = "Procurando duplicados…";
        DuplicatesCancelButton.Visibility = Visibility.Visible;
        DuplicatesBrowseButton.IsEnabled = false;
        DuplicatesScanButton.IsEnabled = false;
        UpdateDuplicatesRecycleState();

        var progress = new Progress<string>(text => DuplicatesProgressText.Text = text);

        try
        {
            var groups = await Task.Run(
                () => DuplicateFinder.Find(_duplicateRoot!, progress: progress, ct: ct));

            foreach (var group in groups)
            {
                var row = new DuplicateGroupRow(group);
                foreach (var file in row.Files)
                    file.PropertyChanged += DuplicatesFile_PropertyChanged;
                _duplicateGroups.Add(row);
            }

            if (_duplicateGroups.Count == 0)
            {
                DuplicatesEmptyState.Text = "Nenhum duplicado encontrado.";
                DuplicatesEmptyState.Visibility = Visibility.Visible;
                DuplicatesSummary.Text = "Nenhum duplicado encontrado.";
            }
            else
            {
                DuplicatesList.Visibility = Visibility.Visible;
                DuplicatesSummary.Text = DuplicatesCountText();
            }
        }
        catch (OperationCanceledException)
        {
            DuplicatesEmptyState.Text = "Escolha uma pasta para procurar arquivos duplicados.";
            DuplicatesEmptyState.Visibility = Visibility.Visible;
            DuplicatesSummary.Text = "Busca cancelada.";
        }
        catch (Exception ex)
        {
            DuplicatesEmptyState.Text = "Escolha uma pasta para procurar arquivos duplicados.";
            DuplicatesEmptyState.Visibility = Visibility.Visible;
            DuplicatesSummary.Text = $"Não foi possível procurar duplicados: {ex.Message}";
        }
        finally
        {
            DuplicatesProgressPanel.Visibility = Visibility.Collapsed;
            DuplicatesCancelButton.Visibility = Visibility.Collapsed;
            DuplicatesBrowseButton.IsEnabled = true;
            DuplicatesScanButton.IsEnabled = true;
            _dupBusy = false;
            _duplicatesCts?.Dispose();
            _duplicatesCts = null;
            UpdateDuplicatesRecycleState();
        }
    }

    private async void DuplicatesRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (_dupBusy) return;

        var paths = _duplicateGroups
            .SelectMany(g => g.Files)
            .Where(f => f.IsChecked)
            .Select(f => f.FilePath)
            .ToList();

        if (paths.Count == 0) return;

        _dupBusy = true;
        _duplicatesCts = new CancellationTokenSource();
        var ct = _duplicatesCts.Token;

        DuplicatesBrowseButton.IsEnabled = false;
        DuplicatesScanButton.IsEnabled = false;
        UpdateDuplicatesRecycleState();
        DuplicatesSummary.Text = "Reciclando…";

        try
        {
            int recycled = await Task.Run(() => DuplicateFinder.RecycleFiles(paths, ct));

            // O que sumiu do caminho original é o que foi para a Lixeira.
            var removed = paths.Where(p => !File.Exists(p))
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var group in _duplicateGroups.ToList())
            {
                foreach (var file in group.Files.Where(f => removed.Contains(f.FilePath)).ToList())
                    group.Files.Remove(file);

                if (group.Files.Count < 2)
                    _duplicateGroups.Remove(group);
                else
                    group.NotifyChanged();
            }

            DuplicatesSummary.Text = recycled == paths.Count
                ? $"{recycled} arquivos movidos para a Lixeira."
                : $"{recycled} de {paths.Count} arquivos movidos para a Lixeira.";

            if (_duplicateGroups.Count == 0)
            {
                DuplicatesList.Visibility = Visibility.Collapsed;
                DuplicatesEmptyState.Text = "Nenhum duplicado encontrado.";
                DuplicatesEmptyState.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            DuplicatesSummary.Text = "Reciclagem cancelada.";
        }
        catch (Exception ex)
        {
            DuplicatesSummary.Text = $"Não foi possível reciclar: {ex.Message}";
        }
        finally
        {
            _dupBusy = false;
            DuplicatesBrowseButton.IsEnabled = true;
            DuplicatesScanButton.IsEnabled = true;
            _duplicatesCts?.Dispose();
            _duplicatesCts = null;
            UpdateDuplicatesRecycleState();
        }
    }

    private void DuplicatesFile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DuplicateFileRow.IsChecked))
            UpdateDuplicatesRecycleState();
    }

    private void UpdateDuplicatesRecycleState()
    {
        int selected = _duplicateGroups.Sum(g => g.Files.Count(f => f.IsChecked));

        DuplicatesRecycleButton.Content = $"Reciclar selecionados ({selected})";
        DuplicatesRecycleButton.IsEnabled = selected > 0 && !_dupBusy;
    }

    private string DuplicatesCountText()
    {
        int groups = _duplicateGroups.Count;
        long recoverable = _duplicateGroups.Sum(g => g.Size * (g.Files.Count - 1));
        var groupLabel = groups == 1 ? "1 grupo" : $"{groups} grupos";

        return $"{groupLabel} • ~{CleanableItem.FormatBytes(recoverable)} recuperáveis";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Atalhos
    // ════════════════════════════════════════════════════════════════════════

    private void ShowShortcutsTab()
    {
        HeaderTitle.Text = "Atalhos";
        SubtitleText.Text = "Encontre atalhos que apontam para programas ou pastas que não existem mais.";

        BrowserWarning.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        OptimizePanel.Visibility = Visibility.Collapsed;
        DuplicatesPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Collapsed;
        LargeFilesPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Visible;
    }

    private void ShortcutsCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _shortcutsCts?.Cancel(); } catch { }
    }

    private async void ShortcutsScan_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcutBusy) return;

        _shortcutBusy = true;
        _shortcutsCts = new CancellationTokenSource();
        var ct = _shortcutsCts.Token;

        _shortcutRows.Clear();
        ShortcutsList.Visibility = Visibility.Collapsed;
        ShortcutsEmptyState.Visibility = Visibility.Collapsed;
        ShortcutsSummary.Text = "";
        ShortcutsStatusText.Text = "Procurando…";
        SetShortcutsBusyUi(true);
        UpdateShortcutsRecycleState();

        try
        {
            var items = await Task.Run(() => ShortcutFixer.FindBroken(ct));

            foreach (var item in items)
            {
                var row = new ShortcutRow(item);
                row.PropertyChanged += ShortcutRow_PropertyChanged;
                _shortcutRows.Add(row);
            }

            if (ct.IsCancellationRequested)
            {
                // Busca cancelada devolve o parcial: o que veio fica visível.
                ShortcutsSummary.Text = "Busca cancelada.";
                if (_shortcutRows.Count == 0)
                    ShortcutsEmptyState.Visibility = Visibility.Visible;
                else
                    ShortcutsList.Visibility = Visibility.Visible;
            }
            else if (_shortcutRows.Count == 0)
            {
                ShortcutsEmptyState.Text = "Nenhum atalho quebrado encontrado.";
                ShortcutsEmptyState.Visibility = Visibility.Visible;
                ShortcutsSummary.Text = "Nenhum atalho quebrado encontrado.";
            }
            else
            {
                ShortcutsList.Visibility = Visibility.Visible;
                ShortcutsSummary.Text = ShortcutsCountText();
            }
        }
        catch (OperationCanceledException)
        {
            ShortcutsEmptyState.Visibility = Visibility.Visible;
            ShortcutsSummary.Text = "Busca cancelada.";
        }
        catch (Exception ex)
        {
            ShortcutsEmptyState.Visibility = Visibility.Visible;
            ShortcutsSummary.Text = $"Não foi possível procurar atalhos: {ex.Message}";
        }
        finally
        {
            ShortcutsStatusText.Text = "";
            SetShortcutsBusyUi(false);
            _shortcutBusy = false;
            _shortcutsCts?.Dispose();
            _shortcutsCts = null;
            UpdateShortcutsRecycleState();
        }
    }

    private async void ShortcutsRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcutBusy) return;

        var paths = _shortcutRows.Where(r => r.IsChecked).Select(r => r.Path).ToList();
        if (paths.Count == 0) return;

        _shortcutBusy = true;
        _shortcutsCts = new CancellationTokenSource();
        var ct = _shortcutsCts.Token;

        ShortcutsStatusText.Text = "Reciclando…";
        SetShortcutsBusyUi(true);
        UpdateShortcutsRecycleState();

        try
        {
            int recycled = await Task.Run(() => ShortcutFixer.RecycleItems(paths, ct));

            // O que sumiu do caminho original é o que foi para a Lixeira.
            var removed = paths.Where(p => !File.Exists(p))
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var row in _shortcutRows.Where(r => removed.Contains(r.Path)).ToList())
                _shortcutRows.Remove(row);

            if (ct.IsCancellationRequested && recycled == 0)
                ShortcutsSummary.Text = "Operação cancelada.";
            else
                ShortcutsSummary.Text = recycled == paths.Count
                    ? $"{recycled} atalhos movidos para a Lixeira."
                    : $"{recycled} de {paths.Count} atalhos movidos para a Lixeira.";

            if (_shortcutRows.Count == 0)
            {
                ShortcutsList.Visibility = Visibility.Collapsed;
                ShortcutsEmptyState.Text = "Nenhum atalho quebrado encontrado.";
                ShortcutsEmptyState.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            ShortcutsSummary.Text = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            ShortcutsSummary.Text = $"Não foi possível remover os atalhos: {ex.Message}";
        }
        finally
        {
            ShortcutsStatusText.Text = "";
            SetShortcutsBusyUi(false);
            _shortcutBusy = false;
            _shortcutsCts?.Dispose();
            _shortcutsCts = null;
            UpdateShortcutsRecycleState();
        }
    }

    private void ShortcutRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShortcutRow.IsChecked))
            UpdateShortcutsRecycleState();
    }

    private void UpdateShortcutsRecycleState()
    {
        int selected = _shortcutRows.Count(r => r.IsChecked);

        ShortcutsRecycleButton.Content = $"Remover selecionados ({selected})";
        ShortcutsRecycleButton.IsEnabled = selected > 0 && !_shortcutBusy;
    }

    private void SetShortcutsBusyUi(bool busy)
    {
        ShortcutsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsScanButton.IsEnabled = !busy;
    }

    private string ShortcutsCountText()
        => _shortcutRows.Count == 1
            ? "1 atalho quebrado"
            : $"{_shortcutRows.Count} atalhos quebrados";

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

    // ════════════════════════════════════════════════════════════════════════
    //  Arquivos grandes
    // ════════════════════════════════════════════════════════════════════════

    private void ShowLargeFilesTab()
    {
        HeaderTitle.Text = "Arquivos grandes";
        SubtitleText.Text = "Encontre os maiores arquivos da pasta escolhida. Marque só o que deseja remover.";

        BrowserWarning.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        OptimizePanel.Visibility = Visibility.Collapsed;
        DuplicatesPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Collapsed;
        LargeFilesPanel.Visibility = Visibility.Visible;

        // A pasta inicial (perfil do usuário) aparece já na primeira abertura.
        if (!_largeLoaded)
        {
            _largeLoaded = true;
            LargeFolderText.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    private void LargePick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolher a pasta para procurar arquivos grandes",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        LargeFolderText.Text = dialog.FolderName;
    }

    private void LargeCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _largeCts?.Cancel(); } catch { }
    }

    private async void LargeScan_Click(object sender, RoutedEventArgs e)
    {
        if (_largeBusy) return;

        string root = LargeFolderText.Text;
        if (string.IsNullOrWhiteSpace(root)) return;

        _largeBusy = true;
        _largeCts = new CancellationTokenSource();
        var ct = _largeCts.Token;

        long minBytes = SelectedLargeMinBytes();
        string minLabel = CleanableItem.FormatBytes(minBytes);
        string emptyText = $"Nenhum arquivo acima de {minLabel} encontrado.";

        _largeRows.Clear();
        LargeFilesList.Visibility = Visibility.Collapsed;
        LargeEmptyState.Visibility = Visibility.Collapsed;
        LargeSummary.Text = "";
        LargeStatusText.Text = "Escaneando…";
        SetLargeBusyUi(true);
        UpdateLargeRecycleState();

        var progress = new Progress<string>(dir => LargeStatusText.Text = $"Escaneando: {dir}");

        try
        {
            var items = await Task.Run(() => LargeFileFinder.Find(root, minBytes, progress, ct));

            foreach (var item in items)
            {
                var row = new LargeFileRow(item);
                row.PropertyChanged += LargeRow_PropertyChanged;
                _largeRows.Add(row);
            }

            if (ct.IsCancellationRequested)
            {
                // Cancelamento no fim da varredura: o que veio fica visível.
                LargeSummary.Text = "Busca cancelada.";
                if (_largeRows.Count == 0)
                    LargeEmptyState.Visibility = Visibility.Visible;
                else
                    LargeFilesList.Visibility = Visibility.Visible;
            }
            else if (_largeRows.Count == 0)
            {
                LargeEmptyState.Text = emptyText;
                LargeEmptyState.Visibility = Visibility.Visible;
                LargeSummary.Text = emptyText;
            }
            else
            {
                LargeFilesList.Visibility = Visibility.Visible;
                LargeSummary.Text = LargeCountText();
            }
        }
        catch (OperationCanceledException)
        {
            LargeEmptyState.Visibility = Visibility.Visible;
            LargeSummary.Text = "Busca cancelada.";
        }
        catch (Exception ex)
        {
            LargeEmptyState.Text = "Escolha uma pasta e clique em Procurar.";
            LargeEmptyState.Visibility = Visibility.Visible;
            LargeSummary.Text = $"Não foi possível procurar arquivos grandes: {ex.Message}";
        }
        finally
        {
            LargeStatusText.Text = "";
            SetLargeBusyUi(false);
            _largeBusy = false;
            _largeCts?.Dispose();
            _largeCts = null;
            UpdateLargeRecycleState();
        }
    }

    private async void LargeRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (_largeBusy) return;

        var paths = _largeRows.Where(r => r.IsChecked).Select(r => r.Path).ToList();
        if (paths.Count == 0) return;

        _largeBusy = true;
        _largeCts = new CancellationTokenSource();
        var ct = _largeCts.Token;

        LargeStatusText.Text = "Reciclando…";
        SetLargeBusyUi(true);
        UpdateLargeRecycleState();

        try
        {
            int recycled = await Task.Run(() => LargeFileFinder.RecycleItems(paths, ct));

            // O que sumiu do caminho original é o que foi para a Lixeira.
            var removed = paths.Where(p => !File.Exists(p))
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var row in _largeRows.Where(r => removed.Contains(r.Path)).ToList())
                _largeRows.Remove(row);

            if (ct.IsCancellationRequested && recycled == 0)
                LargeSummary.Text = "Operação cancelada.";
            else
                LargeSummary.Text = recycled == paths.Count
                    ? $"{recycled} arquivos movidos para a Lixeira."
                    : $"{recycled} de {paths.Count} arquivos movidos para a Lixeira.";

            if (_largeRows.Count == 0)
            {
                LargeFilesList.Visibility = Visibility.Collapsed;
                LargeEmptyState.Text = "Nenhum arquivo na lista.";
                LargeEmptyState.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            LargeSummary.Text = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            LargeSummary.Text = $"Não foi possível remover os arquivos: {ex.Message}";
        }
        finally
        {
            LargeStatusText.Text = "";
            SetLargeBusyUi(false);
            _largeBusy = false;
            _largeCts?.Dispose();
            _largeCts = null;
            UpdateLargeRecycleState();
        }
    }

    private void LargeRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LargeFileRow.IsChecked))
            UpdateLargeRecycleState();
    }

    private void UpdateLargeRecycleState()
    {
        int selected = _largeRows.Count(r => r.IsChecked);

        LargeRecycleButton.Content = $"Remover selecionados ({selected})";
        LargeRecycleButton.IsEnabled = selected > 0 && !_largeBusy;
    }

    private void SetLargeBusyUi(bool busy)
    {
        LargeProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LargeCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LargeScanButton.IsEnabled = !busy;
        LargePickButton.IsEnabled = !busy;
        LargeSizeCombo.IsEnabled = !busy;
        LargeFilesList.IsEnabled = !busy;
    }

    /// <summary>Bytes mínimos da opção escolhida no combo (índice 1 = 100 MB).</summary>
    private long SelectedLargeMinBytes() => LargeSizeCombo.SelectedIndex switch
    {
        0 => 50L * 1024 * 1024,
        2 => 500L * 1024 * 1024,
        3 => 1024L * 1024 * 1024,
        _ => LargeFileFinder.DefaultMinSizeBytes,
    };

    private string LargeCountText()
    {
        long bytes = _largeRows.Sum(r => r.Size);

        return _largeRows.Count == 1
            ? $"1 arquivo • {CleanableItem.FormatBytes(bytes)}"
            : $"{_largeRows.Count} arquivos • {CleanableItem.FormatBytes(bytes)}";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Espaço em disco
    // ════════════════════════════════════════════════════════════════════════

    private void ShowSpaceTab()
    {
        HeaderTitle.Text = "Espaço em disco";
        SubtitleText.Text = "Veja o que está ocupando espaço, pasta por pasta. Duplo clique entra na pasta.";

        BrowserWarning.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        OptimizePanel.Visibility = Visibility.Collapsed;
        DuplicatesPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        AppsPanel.Visibility = Visibility.Collapsed;
        LargeFilesPanel.Visibility = Visibility.Collapsed;
        SpacePanel.Visibility = Visibility.Visible;

        // A pasta inicial (perfil do usuário) só aparece na primeira abertura;
        // a análise começa quando o usuário clicar em Analisar.
        if (!_spaceLoaded)
        {
            _spaceLoaded = true;
            _spaceRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            SpacePathText.Text = _spaceRoot;
            SpaceStatusText.Text = "Escolha uma pasta e clique em Analisar.";
            UpdateSpaceUpState();
        }
    }

    private async void SpacePick_Click(object sender, RoutedEventArgs e)
    {
        if (_spaceBusy) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Escolher a pasta para analisar",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        _spaceRoot = dialog.FolderName;
        SpacePathText.Text = _spaceRoot;
        UpdateSpaceUpState();
        await AnalyzeSpaceAsync();
    }

    private async void SpaceScan_Click(object sender, RoutedEventArgs e) => await AnalyzeSpaceAsync();

    private void SpaceCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _spaceCts?.Cancel(); } catch { }
    }

    private async void SpaceUp_Click(object sender, RoutedEventArgs e)
    {
        if (_spaceBusy) return;

        string? parent = SpaceParent(_spaceRoot);
        if (parent is null) return;

        _spaceRoot = parent;
        SpacePathText.Text = _spaceRoot;
        UpdateSpaceUpState();
        await AnalyzeSpaceAsync();
    }

    private async void SpaceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_spaceBusy) return;
        if (SpaceList.SelectedItem is not SpaceRow row) return;
        if (!row.CanEnter || string.IsNullOrWhiteSpace(row.Path)) return;

        _spaceRoot = row.Path;
        SpacePathText.Text = _spaceRoot;
        UpdateSpaceUpState();
        await AnalyzeSpaceAsync();
    }

    private async Task AnalyzeSpaceAsync()
    {
        if (_spaceBusy) return;

        string root = _spaceRoot;
        if (string.IsNullOrWhiteSpace(root)) return;

        _spaceBusy = true;
        _spaceCts = new CancellationTokenSource();
        var ct = _spaceCts.Token;

        _spaceRows.Clear();
        SpaceTreemap.Children.Clear();
        SpaceTreemap.Visibility = Visibility.Collapsed;
        SpaceList.Visibility = Visibility.Collapsed;
        SpaceEmptyState.Visibility = Visibility.Collapsed;
        SpaceSummary.Text = "";
        SpacePathText.Text = root;
        SpaceStatusText.Text = "Analisando…";
        SetSpaceBusyUi(true);

        var progress = new Progress<string>(dir => SpaceStatusText.Text = $"Analisando: {dir}");

        try
        {
            var analysis = await Task.Run(() => FolderAnalyzer.Analyze(root, progress, ct));

            FillSpaceRows(analysis);
            UpdateSpaceTreemap();

            if (ct.IsCancellationRequested)
            {
                // Cancelamento no fim da análise: o que veio fica visível.
                SpaceSummary.Text = "Análise cancelada.";
                if (_spaceRows.Count == 0)
                {
                    SpaceEmptyState.Text = "Análise cancelada.";
                    SpaceEmptyState.Visibility = Visibility.Visible;
                }
                else
                {
                    SpaceList.Visibility = Visibility.Visible;
                }
            }
            else if (_spaceRows.Count == 0)
            {
                SpaceEmptyState.Text = "Pasta vazia.";
                SpaceEmptyState.Visibility = Visibility.Visible;
                SpaceSummary.Text = "Pasta vazia.";
            }
            else
            {
                SpaceList.Visibility = Visibility.Visible;
                SpaceSummary.Text = SpaceSummaryText(analysis);
            }
        }
        catch (OperationCanceledException)
        {
            SpaceEmptyState.Text = "Análise cancelada.";
            SpaceEmptyState.Visibility = Visibility.Visible;
            SpaceSummary.Text = "Análise cancelada.";
        }
        catch
        {
            SpaceEmptyState.Text = "Não foi possível analisar esta pasta.";
            SpaceEmptyState.Visibility = Visibility.Visible;
            SpaceSummary.Text = "Não foi possível analisar esta pasta.";
        }
        finally
        {
            _spaceBusy = false;
            SpaceStatusText.Text = "";
            SetSpaceBusyUi(false);
            _spaceCts?.Dispose();
            _spaceCts = null;
        }
    }

    private void FillSpaceRows(FolderAnalysis analysis)
    {
        long total = analysis.TotalBytes;

        foreach (var child in analysis.Children)
        {
            _spaceRows.Add(new SpaceRow(
                child.Name, child.Path, child.Bytes, child.FileCount, total, canEnter: true));
        }

        if (analysis.LooseFiles > 0)
        {
            // Pseudo-linha da raiz: mede, mas não navega.
            _spaceRows.Add(new SpaceRow(
                "Arquivos soltos (na raiz)", "", analysis.LooseBytes, analysis.LooseFiles,
                total, canEnter: false));
        }
    }

    /// <summary>
    /// Desenha um treemap simplificado (slice-and-dice) dos filhos da pasta
    /// analisada. Cada retângulo é proporcional ao tamanho; clique entra na pasta.
    /// </summary>
    private void UpdateSpaceTreemap()
    {
        SpaceTreemap.Children.Clear();

        if (_spaceRows.Count == 0)
        {
            SpaceTreemap.Visibility = Visibility.Collapsed;
            return;
        }

        // Aguarda o layout para ter a largura real.
        SpaceTreemap.UpdateLayout();
        double w = SpaceTreemap.ActualWidth;
        if (w < 50) w = 600;
        double h = SpaceTreemap.Height;
        if (h <= 0) h = 200;

        // Coleta os nós (top 12 + "Outros").
        var nodes = new List<TreemapNode>();
        foreach (var row in _spaceRows)
        {
            if (row.Size <= 0) continue;
            nodes.Add(new TreemapNode
            {
                Name = row.Name,
                Value = row.Size,
                Path = row.Path,
                CanEnter = row.CanEnter,
            });
        }

        if (nodes.Count == 0)
        {
            SpaceTreemap.Visibility = Visibility.Collapsed;
            return;
        }

        // Agrupa cauda em "Outros" para não poluir.
        if (nodes.Count > 12)
        {
            var head = nodes.OrderByDescending(n => n.Value).Take(12).ToList();
            var tail = nodes.OrderByDescending(n => n.Value).Skip(12).ToList();
            double tailBytes = tail.Sum(n => n.Value);
            if (tailBytes > 0)
            {
                head.Add(new TreemapNode { Name = $"Outros ({tail.Count})", Value = tailBytes, Path = "", CanEnter = false });
            }
            nodes = head;
        }

        // Layout slice-and-dice.
        var rects = SliceAndDice(nodes, 0, 0, w, h);

        // Paleta de cores: do accent para tons mais escuros.
        var baseColor = (Color)ColorConverter.ConvertFromString("#3B82F6");
        int idx = 0;
        int totalNodes = rects.Count;
        foreach (var (node, rect) in rects)
        {
            if (rect.Width < 2 || rect.Height < 2) continue;

            var factor = 1.0 - (double)idx / Math.Max(1, totalNodes) * 0.55;
            var fill = Color.FromRgb(
                (byte)(baseColor.R * factor + 40),
                (byte)(baseColor.G * factor + 40),
                (byte)(baseColor.B * factor + 40));

            var r = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, rect.Width - 2),
                Height = Math.Max(1, rect.Height - 2),
                Fill = new SolidColorBrush(fill),
                Stroke = new SolidColorBrush(Color.FromRgb(20, 20, 30)),
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4,
                Cursor = node.CanEnter ? Cursors.Hand : Cursors.Arrow,
                Tag = node,
                ToolTip = $"{node.Name}\n{FormatBytes((long)node.Value)}",
            };
            Canvas.SetLeft(r, rect.X + 1);
            Canvas.SetTop(r, rect.Y + 1);
            r.MouseLeftButtonDown += Treemap_Rectangle_Click;
            SpaceTreemap.Children.Add(r);

            // Rótulo (só se couber).
            if (rect.Width > 46 && rect.Height > 26)
            {
                var pct = nodes.Sum(n => n.Value) > 0
                    ? node.Value / nodes.Sum(n => n.Value) * 100 : 0;
                var label = new TextBlock
                {
                    Text = rect.Width > 90
                        ? $"{node.Name}  {pct:0}%"
                        : $"{pct:0}%",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    IsHitTestVisible = false,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Math.Max(20, rect.Width - 6),
                };
                Canvas.SetLeft(label, rect.X + 5);
                Canvas.SetTop(label, rect.Y + 4);
                SpaceTreemap.Children.Add(label);
            }

            idx++;
        }

        SpaceTreemap.Visibility = Visibility.Visible;
    }

    private sealed class TreemapNode
    {
        public string Name = "";
        public double Value;
        public string Path = "";
        public bool CanEnter;
    }

    private static List<(TreemapNode node, Rect rect)> SliceAndDice(
        List<TreemapNode> nodes, double x, double y, double w, double h)
    {
        var result = new List<(TreemapNode, Rect)>();
        if (nodes.Count == 0 || w <= 0 || h <= 0) return result;

        double total = nodes.Sum(n => n.Value);
        if (total <= 0) return result;

        // Ordena por valor desc (já deve estar, mas garante).
        var sorted = nodes.OrderByDescending(n => n.Value).ToList();

        if (sorted.Count == 1)
        {
            result.Add((sorted[0], new Rect(x, y, w, h)));
            return result;
        }

        // Slice vertical se largura >= altura, senão horizontal.
        if (w >= h)
        {
            double half = total / 2;
            double acc = 0;
            int split = 0;
            foreach (var n in sorted)
            {
                if (acc >= half) break;
                acc += n.Value;
                split++;
                if (split >= sorted.Count - 1) break;
            }
            if (split < 1) split = 1;
            if (split > sorted.Count - 1) split = sorted.Count - 1;

            var left = sorted.Take(split).ToList();
            var right = sorted.Skip(split).ToList();
            double leftArea = left.Sum(n => n.Value);
            double leftW = (leftArea / total) * w;

            result.AddRange(SliceAndDice(left, x, y, leftW, h));
            result.AddRange(SliceAndDice(right, x + leftW, y, w - leftW, h));
        }
        else
        {
            double half = total / 2;
            double acc = 0;
            int split = 0;
            foreach (var n in sorted)
            {
                if (acc >= half) break;
                acc += n.Value;
                split++;
                if (split >= sorted.Count - 1) break;
            }
            if (split < 1) split = 1;
            if (split > sorted.Count - 1) split = sorted.Count - 1;

            var top = sorted.Take(split).ToList();
            var bottom = sorted.Skip(split).ToList();
            double topArea = top.Sum(n => n.Value);
            double topH = (topArea / total) * h;

            result.AddRange(SliceAndDice(top, x, y, w, topH));
            result.AddRange(SliceAndDice(bottom, x, y + topH, w, h - topH));
        }

        return result;
    }

    private void Treemap_Rectangle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Rectangle { Tag: TreemapNode node }) return;
        if (!node.CanEnter || string.IsNullOrWhiteSpace(node.Path)) return;
        if (_spaceBusy) return;

        _spaceRoot = node.Path;
        SpacePathText.Text = _spaceRoot;
        UpdateSpaceUpState();
        _ = AnalyzeSpaceAsync();
    }

    private static string FormatBytes(long bytes)
    {
        // Reusa o formatador existente se disponível; fallback simples.
        return CleanableItem.FormatBytes(bytes);
    }

    private void SetSpaceBusyUi(bool busy)
    {
        SpaceProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SpaceCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SpaceScanButton.IsEnabled = !busy;
        SpacePickButton.IsEnabled = !busy;
        SpaceList.IsEnabled = !busy;
        UpdateSpaceUpState();
    }

    private void UpdateSpaceUpState()
        => SpaceUpButton.IsEnabled = !_spaceBusy && SpaceParent(_spaceRoot) is not null;

    /// <summary>Pasta pai de <paramref name="path"/>, ou null na raiz do volume.</summary>
    private static string? SpaceParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try { return Directory.GetParent(path)?.FullName; }
        catch { return null; }
    }

    private static string SpaceSummaryText(FolderAnalysis analysis)
    {
        string files = analysis.TotalFiles == 1
            ? "1 arquivo"
            : $"{analysis.TotalFiles} arquivos";

        return $"Total: {CleanableItem.FormatBytes(analysis.TotalBytes)} • {files}";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Estado da interface
    // ════════════════════════════════════════════════════════════════════════

    private void RefreshView()
    {
        var rows = ActiveRows;
        SetUi(hasResults: rows.Count > 0);
        RefreshSelectionSummary();
    }

    private void UpdateBadges()
    {
        void Set(Border badge, TextBlock text, int count, long bytes)
        {
            if (count == 0) { badge.Visibility = Visibility.Collapsed; return; }
            badge.Visibility = Visibility.Visible;
            text.Text = bytes > 0
                ? $"{count} · {CleanableItem.FormatBytes(bytes)}"
                : count.ToString();
        }

        Set(FilesBadge, FilesBadgeText, _fileRows.Count, _fileRows.Sum(r => r.ItemSizeBytes));
        Set(PrivacyBadge, PrivacyBadgeText, _privacyRows.Count, _privacyRows.Sum(r => r.ItemSizeBytes));
    }

    private void SetUi(bool scanning = false, bool cleaning = false, bool hasResults = false)
    {
        bool idle = !scanning && !cleaning;

        EmptyState.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
        ResultsPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;

        ScanProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressText.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;

        // O botão de analisar é a única porta de entrada: sempre visível
        // quando ocioso, escondido durante a análise.
        ScanButton.Visibility = (idle && !hasResults) ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = idle;

        // Cancelar vale para os dois fluxos (análise e limpeza).
        CancelButton.Visibility = (scanning || cleaning) ? Visibility.Visible : Visibility.Collapsed;

        // Abas desabilitadas durante operações: trocar no meio da limpeza
        // dessincronizaria o estado visível do que está sendo operado.
        TabFiles.IsEnabled = idle;
        TabPrivacy.IsEnabled = idle;
        TabOptimize.IsEnabled = idle;
        TabDuplicates.IsEnabled = idle;
        TabShortcuts.IsEnabled = idle;
        TabApps.IsEnabled = idle;
        TabLargeFiles.IsEnabled = idle;
        TabSpace.IsEnabled = idle;

        // O ponto de restauração acompanha o liga/desliga geral (e continua
        // indisponível sem elevação).
        RestorePointCheck.IsEnabled = idle && _elevated;

        string what = IsPrivacyTab ? "os dados de privacidade dos navegadores" : "as pastas de cache";

        EmptyIcon.Text = scanning ? "⏳" : "🔍";
        EmptyTitle.Text = scanning
            ? "Analisando…"
            : hasResults ? "" : "Pronto para analisar";
        EmptyBody.Text = scanning
            ? $"Lendo {what}. Nada está sendo apagado."
            : IsPrivacyTab
                ? "A análise só lê os bancos dos navegadores. Cada item mostra o que contém antes de você decidir, e um backup é criado antes de qualquer limpeza."
                : "A análise só lê o disco: mede o tamanho das pastas de cache e lista o que pode ser liberado. Nenhum arquivo é tocado até você confirmar.";

        RescanButton.IsEnabled = idle;
        CleanButton.IsEnabled = idle && ActiveRows.Any(r => r.IsChecked);
        SelectAllCheck.IsEnabled = idle && ActiveRows.Count > 0;

        SubtitleText.Text = cleaning
            ? "Limpando o que você selecionou…"
            : hasResults
                ? $"{ActiveRows.Count} item(ns) encontrados. Revise, marque o que quer apagar e confirme."
                : "Nada foi apagado ainda. Clique em Analisar para ver o que dá para liberar.";
    }

    private void UpdateFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady) { FreeSpaceText.Text = string.Empty; return; }

            double used = (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100;
            FreeSpaceText.Text =
                $"{drive.Name}  {CleanableItem.FormatBytes(drive.AvailableFreeSpace)} livres de " +
                $"{CleanableItem.FormatBytes(drive.TotalSize)}  ({used:F0}% em uso)";
        }
        catch { FreeSpaceText.Text = string.Empty; }
    }

    private void ShowOutcome(long freed, int failedCount, IEnumerable<string> reasons)
    {
        var summary = failedCount == 0
            ? $"Pronto. {CleanableItem.FormatBytes(freed)} liberados."
            : $"Pronto. {CleanableItem.FormatBytes(freed)} liberados, {failedCount} item(ns) não puderam ser removidos.";

        MessageBox.Show(this, summary, "Limpeza concluída",
            MessageBoxButton.OK, MessageBoxImage.Information);

        var list = reasons.Take(8).ToList();
        if (list.Count > 0)
            MessageBox.Show(this, "Motivos das falhas:\n\n" + string.Join("\n", list),
                "Itens não removidos", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de inicialização
    // ════════════════════════════════════════════════════════════════════════

    public sealed class StartupRow
    {
        private readonly StartupItem _item;
        private readonly bool _elevated;

        public StartupRow(StartupItem item, bool elevated)
        {
            _item = item;
            _elevated = elevated;
        }

        public string Id => _item.Id;
        public string Name => _item.Name;
        public string Command => _item.Command;
        public bool ItemEnabled => _item.Enabled;

        public string SourceLabel => _item.Source switch
        {
            StartupSource.UserRunKey => "Usuário — Registro",
            StartupSource.MachineRunKey => "Máquina — Registro (64 bits)",
            StartupSource.MachineRunKey32 => "Máquina — Registro (32 bits)",
            StartupSource.UserStartupFolder => "Usuário — Pasta Inicializar",
            _ => "Máquina — Pasta Inicializar",
        };

        public string ActionLabel => _item.Enabled ? "Desativar" : "Ativar";

        public bool ActionEnabled => !(_item.RequiresAdmin && !_elevated);

        public string? ActionToolTip => ActionEnabled ? null : "Requer administrador";

        public Visibility DisabledBadgeVisibility =>
            _item.Enabled ? Visibility.Collapsed : Visibility.Visible;

        public double RowOpacity => _item.Enabled ? 1.0 : 0.55;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linhas da lista de duplicados
    // ════════════════════════════════════════════════════════════════════════

    public sealed class DuplicateGroupRow : INotifyPropertyChanged
    {
        public DuplicateGroupRow(DuplicateGroup group)
        {
            Size = group.Size;

            // O mais antigo (índice 0, já ordenado pelo Core) fica desmarcado:
            // é o candidato natural a "original" que permanece.
            Files = new ObservableCollection<DuplicateFileRow>(
                group.Files.Select((file, index) => new DuplicateFileRow(file, isChecked: index > 0)));
        }

        public long Size { get; }

        public ObservableCollection<DuplicateFileRow> Files { get; }

        public string Header =>
            $"{Files.Count} arquivos • {CleanableItem.FormatBytes(Size)} cada • " +
            $"recuperável ~{CleanableItem.FormatBytes(Size * Math.Max(0, Files.Count - 1))}";

        public void NotifyChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class DuplicateFileRow : INotifyPropertyChanged
    {
        private bool _isChecked;

        public DuplicateFileRow(DuplicateFile file, bool isChecked)
        {
            FilePath = file.Path;
            Size = file.Size;
            DateDisplay = file.LastWriteTimeUtc.ToLocalTime().ToString("dd/MM/yy HH:mm");
            _isChecked = isChecked;
        }

        public string FilePath { get; }
        public long Size { get; }
        public string DateDisplay { get; }
        public string SizeDisplay => CleanableItem.FormatBytes(Size);

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de atalhos
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ShortcutRow : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public ShortcutRow(ShortcutItem item)
        {
            Path = item.Path;
            Name = item.Name;
            Location = item.Location;
            Target = item.Target;
        }

        public string Path { get; }
        public string Name { get; }
        public ShortcutLocation Location { get; }
        public string? Target { get; }

        public string LocationLabel => Location switch
        {
            ShortcutLocation.UserDesktop => "Área de trabalho",
            ShortcutLocation.CommonDesktop => "Área de trabalho pública",
            ShortcutLocation.UserStartMenu => "Menu Iniciar",
            _ => "Menu Iniciar (todos os usuários)",
        };

        public string TargetLabel => string.IsNullOrWhiteSpace(Target)
            ? "Destino não informado"
            : $"Destino: {Target.Trim()}";

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de aplicativos
    // ════════════════════════════════════════════════════════════════════════

    public sealed class AppRow
    {
        public AppRow(WingetPackage package)
        {
            Name = package.Name;
            Id = package.Id;
            CurrentVersion = package.CurrentVersion;
            AvailableVersion = package.AvailableVersion;
        }

        public string Name { get; }
        public string Id { get; }
        public string CurrentVersion { get; }
        public string AvailableVersion { get; }

        public string VersionsLabel =>
            $"{(string.IsNullOrWhiteSpace(CurrentVersion) ? "—" : CurrentVersion)} → " +
            $"{(string.IsNullOrWhiteSpace(AvailableVersion) ? "—" : AvailableVersion)}";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de arquivos grandes
    // ════════════════════════════════════════════════════════════════════════

    public sealed class LargeFileRow : INotifyPropertyChanged
    {
        // Desmarcada por padrão: remover arquivo grande é decisão consciente.
        private bool _isChecked;

        public LargeFileRow(LargeFileItem item)
        {
            Path = item.Path;
            Size = item.Size;
            Name = System.IO.Path.GetFileName(item.Path);
            DateDisplay = item.LastWriteTimeUtc.ToLocalTime().ToString("dd/MM/yyyy");
        }

        public string Path { get; }
        public string Name { get; }
        public long Size { get; }
        public string DateDisplay { get; }

        public string SizeDisplay => CleanableItem.FormatBytes(Size);

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de espaço em disco
    // ════════════════════════════════════════════════════════════════════════

    public sealed class SpaceRow : INotifyPropertyChanged
    {
        private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

        private string _name;
        private double _percent;
        private bool _canEnter;

        public SpaceRow(
            string name, string path, long size, int fileCount, long totalBytes, bool canEnter)
        {
            _name = name;
            Path = path;
            Size = size;
            FileCount = fileCount;
            // Fatia do total; total zero (pasta vazia) vira 0% em vez de NaN.
            _percent = totalBytes > 0 ? size * 100.0 / totalBytes : 0;
            _canEnter = canEnter;
        }

        public string Path { get; }
        public long Size { get; }
        public int FileCount { get; }

        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value; OnPropertyChanged(); }
        }

        /// <summary>Percentual do total (0–100).</summary>
        public double Percent
        {
            get => _percent;
            set
            {
                if (_percent == value) return;
                _percent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentDisplay));
            }
        }

        /// <summary>Só subpastas navegam (duplo clique); a linha de soltos não.</summary>
        public bool CanEnter
        {
            get => _canEnter;
            set { if (_canEnter == value) return; _canEnter = value; OnPropertyChanged(); }
        }

        public string SizeDisplay => CleanableItem.FormatBytes(Size);

        public string CountDisplay => FileCount == 1 ? "1 arquivo" : $"{FileCount} arquivos";

        public string PercentDisplay => _percent.ToString("0.0", PtBr) + "%";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de processos
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ProcRow : INotifyPropertyChanged
    {
        private bool _confirming;

        public ProcRow(ProcessItem item)
        {
            Pid = item.Pid;
            Name = item.Name;
            WorkingSetBytes = item.WorkingSetBytes;
        }

        public int Pid { get; }
        public string Name { get; }
        public long WorkingSetBytes { get; }

        public string PidLabel => $"PID {Pid}";
        public string MemoryDisplay => CleanableItem.FormatBytes(WorkingSetBytes);

        /// <summary>Dois cliques para encerrar: o próprio botão vira "Confirmar".</summary>
        public string ActionLabel => _confirming ? "Confirmar" : "Encerrar";

        public bool IsConfirming
        {
            get => _confirming;
            set
            {
                if (_confirming == value) return;
                _confirming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionLabel));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista de desinstalação
    // ════════════════════════════════════════════════════════════════════════

    public sealed class UninsRow : INotifyPropertyChanged
    {
        private bool _confirming;

        public UninsRow(InstalledApp app)
        {
            Id = app.Id;
            Name = app.Name;
            Version = app.Version;
        }

        public string Id { get; }
        public string Name { get; }
        public string Version { get; }

        public string VersionLabel => string.IsNullOrWhiteSpace(Version)
            ? Id
            : $"Versão {Version} · {Id}";

        /// <summary>Dois cliques para desinstalar: o próprio botão vira "Confirmar?".</summary>
        public string ActionLabel => _confirming ? "Confirmar?" : "Desinstalar";

        public bool IsConfirming
        {
            get => _confirming;
            set
            {
                if (_confirming == value) return;
                _confirming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionLabel));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Linha da lista de adaptadores de rede (só exibição).</summary>
    public sealed class NetRow
    {
        public NetRow(NetworkAdapterInfo adapter)
        {
            Name = adapter.Name;
            Detail = $"{adapter.Type} · {adapter.Status} · {adapter.Speed}";
            Ipv4 = adapter.Ipv4 is { Length: > 0 } ip ? ip : "sem endereço IPv4";
        }

        public string Name { get; }
        public string Detail { get; }
        public string Ipv4 { get; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Linha da lista
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ItemRow : INotifyPropertyChanged
    {
        private bool _isChecked;

        public CleanableItem? FileItem { get; }
        public PrivacyItem? PrivacyItem { get; }

        /// <summary>Linha de arquivo (v0.1).</summary>
        public ItemRow(CleanableItem item)
        {
            FileItem = item;
            _isChecked = item.Risk != RiskLevel.High && item.Category.DefaultChecked();
        }

        /// <summary>Linha de privacidade (v0.2).</summary>
        public ItemRow(PrivacyItem item)
        {
            PrivacyItem = item;
            // Item bloqueado nunca vem marcado: não dá para apagar mesmo.
            _isChecked = item.BlockedReason is null && item.Kind.DefaultChecked();
        }

        public bool IsPrivacy => PrivacyItem is not null;

        public string Title => PrivacyItem?.Title ?? FileItem!.Category.DisplayName();

        public string Subtitle => PrivacyItem is not null
            ? PrivacyItem.Profile.Display
            : FileItem!.IsDirectory
                ? $"{FileItem.Path}  ·  {FileItem.FileCount:N0} arquivo(s)"
                : FileItem!.Path;

        public long ItemSizeBytes => PrivacyItem?.SizeBytes ?? FileItem!.SizeBytes;
        public int ItemRecordCount => PrivacyItem?.RecordCount ?? FileItem!.FileCount;

        public string SizeDisplay => CleanableItem.FormatBytes(ItemSizeBytes);

        public bool CanToggle => PrivacyItem is not null
            ? PrivacyItem.BlockedReason is null
            : FileItem!.Risk != RiskLevel.High;

        public RiskLevel Risk => PrivacyItem?.Risk ?? FileItem!.Risk;

        public string Detail
        {
            get
            {
                if (PrivacyItem is not null)
                {
                    var parts = new List<string>();

                    if (PrivacyItem.BlockedReason is not null)
                        parts.Add($"Não foi possível ler: {PrivacyItem.BlockedReason}.");

                    parts.Add(PrivacyItem.Kind.Explanation());

                    if (PrivacyItem.RecordCount > 0)
                        parts.Add($"{PrivacyItem.RecordCount:N0} registro(s).");

                    if (PrivacyItem.Risk == RiskLevel.High)
                        parts.Add("Risco alto: não vem marcado.");

                    return string.Join("  ", parts);
                }

                var fileParts = new List<string>();
                if (!string.IsNullOrEmpty(FileItem!.Note)) fileParts.Add(FileItem.Note);
                fileParts.Add(FileItem.Category.Explanation());
                if (FileItem.Risk == RiskLevel.Medium)
                    fileParts.Add("Risco médio: pode deixar a próxima operação mais lenta.");
                return string.Join("  ", fileParts);
            }
        }

        public IReadOnlyList<string> Preview => PrivacyItem?.Preview ?? [];

        public Visibility PreviewVisibility =>
            PrivacyItem is not null && PrivacyItem.Preview.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string RiskLabel => Risk switch
        {
            RiskLevel.Low => "baixo",
            RiskLevel.Medium => "médio",
            _ => "alto",
        };

        // Pincéis congelados e compartilhados: antes era uma alocação nova
        // a cada leitura de propriedade, uma por linha realizada na tela.
        private static readonly SolidColorBrush LowRiskBackground = Frozen(0x1B, 0x3A, 0x24);
        private static readonly SolidColorBrush MediumRiskBackground = Frozen(0x3A, 0x30, 0x12);
        private static readonly SolidColorBrush HighRiskBackground = Frozen(0x3A, 0x1B, 0x1A);
        private static readonly SolidColorBrush LowRiskForeground = Frozen(0x7E, 0xE0, 0x8F);
        private static readonly SolidColorBrush MediumRiskForeground = Frozen(0xF0, 0xC6, 0x74);
        private static readonly SolidColorBrush HighRiskForeground = Frozen(0xFF, 0x9A, 0x94);

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public Brush RiskBackground => Risk switch
        {
            RiskLevel.Low => LowRiskBackground,
            RiskLevel.Medium => MediumRiskBackground,
            _ => HighRiskBackground,
        };

        public Brush RiskForeground => Risk switch
        {
            RiskLevel.Low => LowRiskForeground,
            RiskLevel.Medium => MediumRiskForeground,
            _ => HighRiskForeground,
        };

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
