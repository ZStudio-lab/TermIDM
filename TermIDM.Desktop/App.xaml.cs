using Microsoft.UI.Xaml;
using System.IO;
using System.Reflection;

namespace TermIDM.Desktop;

public partial class App : Application
{
    private Window? mainWindow;

    public App()
    {
        UnhandledException += (_, args) => WriteStartupLog(args.Exception);
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            mainWindow = new MainWindow();
            mainWindow.Activate();
            if (mainWindow is MainWindow dashboard && !await dashboard.EnsureLicensedAsync())
                Exit();
        }
        catch (Exception ex)
        {
            WriteStartupLog(ex);
            throw;
        }
    }

    private static void WriteStartupLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermIDM", "logs");
            Directory.CreateDirectory(logDirectory);
            var diagnostics = exception.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property =>
                {
                    try { return $"{property.Name}: {property.GetValue(exception)}"; }
                    catch { return null; }
                })
                .Where(value => !string.IsNullOrWhiteSpace(value));
            File.AppendAllText(Path.Combine(logDirectory, "startup.log"),
                $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* Logging must never hide the original startup failure. */ }
    }
}
