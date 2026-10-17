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

        // Paleta de cores: do accent do tema para tons mais escuros.
        var baseColor = Application.Current?.TryFindResource("AccentColor") is Color accent
            ? accent
            : Color.FromRgb(0x2E, 0x7B, 0xFF);
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

}
