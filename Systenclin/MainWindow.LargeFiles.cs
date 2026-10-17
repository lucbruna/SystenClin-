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

}
