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
        MonitorCpuArea.Points.Clear();
        MonitorMemArea.Points.Clear();
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

        RenderMonitorChart(MonitorCpuChart, MonitorCpuLine, MonitorCpuArea, _monitorCpuHistory);
        RenderMonitorChart(MonitorMemChart, MonitorMemLine, MonitorMemArea, _monitorMemHistory);
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
    /// pela largura e y mapeia 0..100% para baixo..cima. O polígono recebe a
    /// mesma linha fechada no fundo, formando a área preenchida (sparkline).
    /// Menos de duas amostras ou gráfico sem largura deixam ambos vazios.
    /// </summary>
    private static void RenderMonitorChart(
        Grid chart, System.Windows.Shapes.Polyline line,
        System.Windows.Shapes.Polygon area, Queue<double> history)
    {
        var points = line.Points;
        var areaPoints = area.Points;
        var width = chart.ActualWidth;
        var height = double.IsNaN(chart.Height) ? chart.ActualHeight : chart.Height;

        if (history.Count < 2 || width <= 0 || height <= 0)
        {
            points.Clear();
            areaPoints.Clear();
            return;
        }

        points.Clear();
        areaPoints.Clear();

        var values = history.ToArray();
        var step = width / (values.Length - 1);

        for (var i = 0; i < values.Length; i++)
        {
            var x = step * i;
            var y = (1 - Math.Clamp(values[i], 0, 100) / 100) * (height - 2) + 1;
            points.Add(new Point(x, y));
        }

        // Área preenchida: a linha + o fecho no fundo do gráfico.
        foreach (var point in points)
            areaPoints.Add(point);

        areaPoints.Add(new Point(width, height));
        areaPoints.Add(new Point(0, height));
    }

}
