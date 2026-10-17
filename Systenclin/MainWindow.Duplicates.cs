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

}
