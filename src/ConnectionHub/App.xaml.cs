using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ConnectionHub.Services;
using ConnectionHub.Views;

namespace ConnectionHub;

public partial class App : Application
{
    public static ConnectionStore Store { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        string? loadError = null;
        try
        {
            Store.Load();
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }

        // Create the main window first so a dialog never becomes the application's main window.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (loadError != null)
            MessageWindow.Show(window, "Fout bij laden", $"De verbindingen konden niet worden geladen:\n{loadError}", MessageKind.Error, "OK");
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectionHub");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "error.log"), $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch (Exception)
        {
            // Logging must never crash the app.
        }

        e.Handled = true;
        try
        {
            MessageWindow.Show(Current?.MainWindow, "Onverwachte fout", e.Exception.Message, MessageKind.Error, "OK");
        }
        catch (Exception)
        {
            // The themed dialog itself may be what is failing; fall back to the system message box.
            MessageBox.Show(e.Exception.Message, "Connection Hub – onverwachte fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
