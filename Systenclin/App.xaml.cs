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

    /// <summary>Erro na thread da interface: avisa e mantém o aplicativo vivo.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            MessageBox.Show($"Ocorreu um erro inesperado:\n\n{e.Exception.Message}",
                "Systenclin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }

        e.Handled = true;
    }

    /// <summary>Erro fatal fora da thread da interface: avisa; o encerramento não pode ser evitado.</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var message = (e.ExceptionObject as Exception)?.Message ?? "Erro desconhecido.";
            MessageBox.Show($"Ocorreu um erro fatal:\n\n{message}",
                "Systenclin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    /// <summary>Tarefa falha sem observador: marca como observada para não derrubar o aplicativo.</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            MessageBox.Show($"Ocorreu um erro em segundo plano:\n\n{e.Exception.Message}",
                "Systenclin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }

        e.SetObserved();
    }
}
