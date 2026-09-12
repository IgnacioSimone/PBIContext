using System.IO;
using System.Windows;
using System.Windows.Threading;
using PBIExplorer.Services;

namespace PBIExplorer;

public partial class App : Application
{
    private static readonly string LogPath = AppPaths.LogPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Sin esto, cualquier excepción en el arranque cierra la app en silencio
        // y no queda rastro de por qué.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        Exit += (_, args) => Log($"App.Exit — ExitCode={args.ApplicationExitCode}");

        try
        {
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Log($"EXCEPCIÓN en el arranque: {ex}");
            throw;
        }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n"); }
        catch { /* best-effort */ }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"EXCEPCIÓN NO CONTROLADA (UI): {e.Exception}");
        MessageBox.Show($"Ocurrió un error inesperado:\n\n{e.Exception.Message}\n\nDetalle completo en:\n{LogPath}",
            "PBI Context", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log($"EXCEPCIÓN NO CONTROLADA (dominio): {e.ExceptionObject}");
    }
}
