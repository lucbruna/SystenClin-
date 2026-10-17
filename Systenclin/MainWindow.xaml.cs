using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Systenclin.Core;
using Systenclin.L10n;

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
    private bool _langInit;

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

    // Verificação de volume (chkdsk /scan), no cartão "Saúde do disco".
    private CancellationTokenSource? _scanCts;
    private bool _scanBusy;

    // Relatórios de energia/bateria (powercfg), no cartão "Relatório de energia".
    private CancellationTokenSource? _powerCts;
    private bool _powerBusy;

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

    // Fase ao vivo da atualização: base ("Atualizando 2/5 — X") + rótulo
    // ("baixando…") + tempo decorrido, para a UI nunca parecer travada.
    private string? _appsPhaseBase;
    private string? _appsPhaseLabel;
    private DateTime _appsPhaseStart;
    private int _appsPhaseVersion;
    private DispatcherTimer? _appsTicker;

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
        _langInit = true;
        Loc.Init();
        InitializeComponent();
        ItemsList.ItemsSource = _fileRows;
        SpaceList.ItemsSource = _spaceRows;
        AppsList.ItemsSource = _appRows;
        DuplicatesList.ItemsSource = _duplicateGroups;
        ShortcutsList.ItemsSource = _shortcutRows;
        LargeFilesList.ItemsSource = _largeRows;
        Loaded += (_, _) => { UpdateFreeSpace(); StartStatusBar(); ApplyLanguage(); };
        Loc.Changed += ApplyLanguage;

        RestorePointCheck.IsChecked = _elevated;
        if (!_elevated)
        {
            RestorePointCheck.IsEnabled = false;
            RestorePointCheck.ToolTip = "Requer administrador. Abra o programa como administrador.";
        }

        OptimizeDiskButton.IsEnabled = _elevated;
        if (!_elevated)
            OptimizeDiskButton.ToolTip = "Requer administrador";

        DiskScanButton.IsEnabled = _elevated;
        if (!_elevated)
            DiskScanButton.ToolTip = "Requer administrador";

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
            try { _scanCts?.Cancel(); } catch { }
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
            if (!drive.IsReady)
            {
                FreeSpaceText.Text = string.Empty;
                DiskUsageGauge.Value = 0;
                return;
            }

            double used = (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100;
            DiskUsageGauge.Value = used;
            FreeSpaceText.Text =
                $"{drive.Name}  {CleanableItem.FormatBytes(drive.AvailableFreeSpace)} livres de " +
                $"{CleanableItem.FormatBytes(drive.TotalSize)}  ({used:F0}% em uso)";
        }
        catch
        {
            FreeSpaceText.Text = string.Empty;
            DiskUsageGauge.Value = 0;
        }
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
    //  Idioma da interface (PT / EN / ES)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Re-aplica os textos traduzidos e sincroniza os botões de idioma.</summary>
    private void ApplyLanguage()
    {
        Tr.Apply(this);

        _langInit = true;
        LangPt.IsChecked = Loc.Current == "pt-BR";
        LangEn.IsChecked = Loc.Current == "en";
        LangEs.IsChecked = Loc.Current == "es";
        _langInit = false;
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_langInit || sender is not RadioButton radio || radio.Tag is not string code) return;
        Loc.Set(code);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Chrome escuro (barra de título)
    // ════════════════════════════════════════════════════════════════════════

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    /// <summary>
    /// Ativa a barra de título escura pelo atributo do DWM (Windows 10 1809+).
    /// Em versões sem suporte o app mantém a barra padrão — nada de
    /// AllowsTransparency nem chrome customizado.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var build = Environment.OSVersion.Version.Build;
            if (build < 17763) return;

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Atributo 19: 1809–1909. Atributo 20: 2004+ / Windows 11.
            var attribute = build >= 18985 ? 20 : 19;
            var enabled = 1;
            _ = DwmSetWindowAttribute(hwnd, attribute, ref enabled, sizeof(int));
        }
        catch
        {
            // Sem DWM (ou versão sem suporte): mantém a barra padrão.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

}
