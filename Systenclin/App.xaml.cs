using System.Windows;
using System.Windows.Threading;
using Systenclin.Core;

namespace Systenclin;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(a => a.Equals("--headless-clean", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunHeadlessCleanAsync();
            // Não chama base.OnStartup para não abrir janela.
            return;
        }
        base.OnStartup(e);
    }

    private static async Task RunHeadlessCleanAsync()
    {
        try
        {
            var items = ScanTargets.All()
                .Where(t => t.Risk != RiskLevel.High)
                .Select(t => new CleanableItem
                {
                    Path = t.Path,
                    Category = t.Category,
                    SizeBytes = 0,
                    Risk = t.Risk,
                    IsDirectory = t.WholeDirectory,
                })
                .ToList();

            var cleaner = new Cleaner();
            await cleaner.CleanAsync(items);
        }
        catch { }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>Erro na thread da interface: registra em arquivo, avisa e mantém o aplicativo vivo.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = CrashLog.Write("erro na interface (DispatcherUnhandledException)", e.Exception);
        ShowCrash("Ocorreu um erro inesperado", e.Exception, path);

        e.Handled = true;
    }

    /// <summary>Erro fatal fora da thread da interface: registra e avisa; o encerramento não pode ser evitado.</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        var path = ex is null ? null : CrashLog.Write("erro fatal (AppDomain.UnhandledException)", ex);
        ShowCrash("Ocorreu um erro fatal", ex, path);
    }

    /// <summary>Tarefa falha sem observador: registra, avisa e marca como observada.</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var path = CrashLog.Write("erro em segundo plano (TaskScheduler.UnobservedTaskException)", e.Exception);
        ShowCrash("Ocorreu um erro em segundo plano", e.Exception, path);

        e.SetObserved();
    }

    /// <summary>Aviso de erro com o caminho do log anexado; falha do aviso não propaga.</summary>
    private static void ShowCrash(string title, Exception? ex, string? logPath)
    {
        try
        {
            var details = logPath is null ? "" : $"\n\nDetalhes salvos em:\n{logPath}";
            MessageBox.Show($"{title}:\n\n{ex?.Message ?? "Erro desconhecido."}{details}",
                "Systenclin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
