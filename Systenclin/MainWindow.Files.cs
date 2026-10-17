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

}
