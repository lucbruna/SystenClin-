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

}
