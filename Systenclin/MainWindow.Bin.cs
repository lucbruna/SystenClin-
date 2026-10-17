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

}
