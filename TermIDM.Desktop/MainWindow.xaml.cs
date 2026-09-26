using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using WinRT.Interop;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace TermIDM.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> downloads = new();
    private readonly ObservableCollection<DownloadItem> visibleDownloads = new();
    private readonly DispatcherQueue uiQueue;
    private AppWindow? appWindow;
    private IntPtr? activeSubWindowHandle;
    private bool allowClose;
    private string currentFilter = "All";
    private int defaultConnectionLimit = 16;
    private double defaultSpeedLimitMiB = 0;
    private int networkTimeoutSeconds = 90;
    private string defaultSaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public MainWindow()
    {
        InitializeComponent();
        var logoPath = Path.Combine(AppContext.BaseDirectory, "assets", "termidm.ico");
        if (File.Exists(logoPath)) BrandLogoImage.Source = new BitmapImage(new Uri(logoPath));
        uiQueue = DispatcherQueue.GetForCurrentThread();
        DownloadList.ItemsSource = visibleDownloads;
        Root.RequestedTheme = ElementTheme.Dark;
        // Mica is supported by Windows 11. On Windows 10, avoid assigning a
        // system backdrop and use the opaque dark surface instead.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            SystemBackdrop = new MicaBackdrop();
        else
            Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 19, 24));
        LoadSettings();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = $"{version?.Major}.{version?.Minor}.{version?.Build}";
        Title = $"TermIDM v{versionString} · Downloads";
        VersionText.Text = $"Desktop download manager  ·  {versionString}";

        var hwnd = WindowNative.GetWindowHandle(this);
        appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1180, Height = 760 });
        appWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => Application.Current.Exit();
        SetFilter("All");
        UpdateSummary();
    }

    private DownloadItem? SelectedDownload => DownloadList.SelectedItem as DownloadItem;

    public async Task<bool> EnsureLicensedAsync()
    {
        if (LicenseService.TryLoad(out var cached) && cached is not null) return true;
        while (true)
        {
            var key = new TextBox
            {
                PlaceholderText = "Paste your encrypted string or signed key",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 76
            };
            var machineId = new TextBlock
            {
                Text = LicenseService.MachineId,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.WrapWholeWords,
                IsTextSelectionEnabled = true
            };
            var copyId = new Button { Content = "Copy device ID", HorizontalAlignment = HorizontalAlignment.Left };
            copyId.Click += (_, _) =>
            {
                var package = new DataPackage();
                package.SetText(LicenseService.MachineId);
                Clipboard.SetContent(package);
            };
            var openPortal = new Button { Content = "Open license portal", HorizontalAlignment = HorizontalAlignment.Left };
            openPortal.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("https://zstudio-lab.github.io/TermIDM/") { UseShellExecute = true }); }
                catch (Exception ex) { StatusText.Text = $"Could not open the license page: {ex.Message}"; }
            };
            var body = new StackPanel { Spacing = 10, MaxWidth = 500 };
            body.Children.Add(new TextBlock { Text = "Device activation", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = "TermIDM is free to use. Paste the encrypted string generated for this device, or a signed activation key.", TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = "Device ID", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(machineId);
            body.Children.Add(copyId);
            body.Children.Add(openPortal);
            body.Children.Add(new TextBlock { Text = "Encrypted string or activation key", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(key);
            if (!await ShowSubWindowAsync("Activate TermIDM", body, "Activate", "Exit", 590, 560)) return false;
            if (!LicenseService.Validate(key.Text.Trim(), out var message))
            {
                if (!await ShowSubWindowAsync("Invalid Key", new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                }, "Try Again", "Exit", 480, 220)) return false;
                continue;
            }
            try
            {
                LicenseService.Save(key.Text.Trim());
                StatusText.Text = message;
                return true;
            }
            catch (Exception ex)
            {
                if (!await ShowSubWindowAsync("Activation could not be saved", new TextBlock
                {
                    Text = ex.Message,
                    TextWrapping = TextWrapping.Wrap
                }, "Try Again", "Exit", 480, 220)) return false;
            }
        }
    }

    private async void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = new TextBox { PlaceholderText = "Paste a direct HTTP or HTTPS download link", MinWidth = 420 };
        var folder = new TextBox { Text = defaultSaveFolder, PlaceholderText = "Save location" };
        var category = new ComboBox { SelectedIndex = 0, MinWidth = 180 };
        foreach (var categoryChoice in new[] { "Auto", "General", "Compressed", "Documents", "Videos" }) category.Items.Add(categoryChoice);
        var connections = new ComboBox { SelectedIndex = Math.Clamp(defaultConnectionLimit, 1, 128) - 1, MinWidth = 100 };
        for (var count = 1; count <= 128; count++) connections.Items.Add(count.ToString(CultureInfo.InvariantCulture));
        var speedLimit = new NumberBox { Header = "Speed limit (MiB/s, 0 = unlimited)", Value = defaultSpeedLimitMiB, Minimum = 0, Maximum = 1_000_000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var description = new TextBox { PlaceholderText = "Optional description", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };

        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(folder);
        var browse = new Button { Content = "Browse…", MinWidth = 92 };
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, activeSubWindowHandle ?? WindowNative.GetWindowHandle(this));
                var selected = await picker.PickSingleFolderAsync();
                if (selected is not null) folder.Text = selected.Path;
            }
            catch (Exception ex) { StatusText.Text = $"Folder picker failed: {ex.Message}"; }
        };

        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(Field("URL", url));
        form.Children.Add(Field("Save to", folderRow));
        form.Children.Add(Field("Category", category));
        form.Children.Add(Field("Connections", connections));
        form.Children.Add(speedLimit);
        form.Children.Add(Field("Description", description));
        if (!await ShowSubWindowAsync("Add New Download", form, "Download Now", "Cancel", 560, 650)) return;

        if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText.Text = "Enter a valid HTTP or HTTPS download link.";
            await ShowMessageAsync("Add New Download", "Paste a valid HTTP or HTTPS download URL.");
            return;
        }
        var saveFolder = folder.Text.Trim();
        if (saveFolder.Length == 0)
        {
            StatusText.Text = "Choose a download folder.";
            await ShowMessageAsync("Add New Download", "Choose a folder where TermIDM can save the file.");
            return;
        }
        try { Directory.CreateDirectory(saveFolder); }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not create the download folder: {ex.Message}";
            await ShowMessageAsync("Folder error", ex.Message);
            return;
        }

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "download";
        var categoryName = category.SelectedItem?.ToString() ?? "Auto";
        var connectionLimit = int.TryParse(connections.SelectedItem?.ToString(), out var parsedConnections)
            ? Math.Clamp(parsedConnections, 1, 128) : defaultConnectionLimit;
        var item = new DownloadItem(fileName, uri.ToString(), saveFolder, categoryName)
        {
            Description = description.Text.Trim(),
            ConnectionLimit = connectionLimit,
            SpeedLimitBytesPerSecond = MiBToBytes(speedLimit.Value),
            NetworkTimeoutSeconds = networkTimeoutSeconds
        };
        item.PropertyChanged += DownloadItem_PropertyChanged;
        downloads.Insert(0, item);
        RefreshVisibleDownloads();
        StatusText.Text = $"Starting {fileName}…";
        _ = RunDownloadAsync(item, connectionLimit);
    }

    private static StackPanel Field(string label, UIElement control)
    {
        var section = new StackPanel { Spacing = 5 };
        section.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray) });
        section.Children.Add(control);
        return section;
    }

    private async Task<bool> ShowSubWindowAsync(string title, UIElement content, string? primaryText,
                                                string closeText, int width, int height)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = new Grid
        {
            Padding = new Thickness(22),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 23, 25, 31)),
            RequestedTheme = ElementTheme.Dark
        };
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Children.Add(content);
        Window? window = null;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var close = new Button { Content = closeText, MinWidth = 96 };
        close.Click += (_, _) => { completion.TrySetResult(false); window?.Close(); };
        buttons.Children.Add(close);
        if (primaryText is not null)
        {
            var primary = new Button
            {
                Content = primaryText,
                MinWidth = 124,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 8, 127, 193)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 139, 208))
            };
            primary.Click += (_, _) => { completion.TrySetResult(true); window?.Close(); };
            buttons.Children.Add(primary);
        }
        Grid.SetRow(buttons, 1);
        shell.Children.Add(buttons);

        window = new Window { Title = title, Content = shell };
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) window.SystemBackdrop = new MicaBackdrop();
        var mainHitTest = Root.IsHitTestVisible;
        Root.IsHitTestVisible = false;
        window.Closed += (_, _) => completion.TrySetResult(false);
        var hwnd = WindowNative.GetWindowHandle(window);
        activeSubWindowHandle = hwnd;
        var childWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        childWindow.Resize(new Windows.Graphics.SizeInt32 { Width = width, Height = height });
        window.Activate();
        try { return await completion.Task; }
        finally { activeSubWindowHandle = null; Root.IsHitTestVisible = mainHitTest; }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task RunDownloadAsync(DownloadItem item, int connectionLimit)
    {
        var engine = Path.Combine(AppContext.BaseDirectory, "TermIDM.Engine.exe");
        if (!File.Exists(engine))
        {
            item.Status = "Error";
            item.Details = "TermIDM.Engine.exe is missing beside the desktop app.";
            StatusText.Text = item.Details;
            return;
        }

        var start = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--engine");
        start.ArgumentList.Add(item.Url);
        start.ArgumentList.Add(item.Folder);
        start.ArgumentList.Add(connectionLimit.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(item.SpeedLimitBytesPerSecond.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(item.NetworkTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

        try
        {
            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            item.EngineProcess = process;
            item.Status = "Connecting";
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync();
            string? engineError = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (line.StartsWith("ERROR\t", StringComparison.Ordinal)) engineError = line[6..];
                if (line.StartsWith("PROGRESS\t", StringComparison.Ordinal))
                {
                    if (item.QueueTelemetry(line))
                    {
                        uiQueue.TryEnqueue(() =>
                        {
                            var latest = item.TakeTelemetry();
                            if (latest is not null) HandleEngineMessage(item, latest);
                        });
                    }
                }
                else uiQueue.TryEnqueue(() => HandleEngineMessage(item, line));
            }

            await process.WaitForExitAsync();
            var stderr = await stderrTask;
            await EnqueueUiAsync(() =>
            {
                if (process.ExitCode == 0)
                {
                    item.Status = "Completed";
                    item.Percent = 100;
                    item.SpeedBytesPerSecond = 0;
                    item.Details = "Download completed.";
                }
                else if (item.Status != "Cancelled")
                {
                    item.Status = "Error";
                    item.Details = string.IsNullOrWhiteSpace(item.Details)
                        ? string.IsNullOrWhiteSpace(stderr) ? $"Engine exited with code {process.ExitCode}." : stderr.Trim()
                        : item.Details;
                }
                item.EngineProcess = null;
                StatusText.Text = item.Details;
                RefreshVisibleDownloads();
                UpdateSummary();
            });
        }
        catch (Exception ex)
        {
            await EnqueueUiAsync(() =>
            {
                item.Status = "Error";
                item.Details = ex.Message;
                StatusText.Text = ex.Message;
                item.EngineProcess = null;
                RefreshVisibleDownloads();
            });
        }
    }

    private Task EnqueueUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!uiQueue.TryEnqueue(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        })) completion.SetException(new InvalidOperationException("The UI dispatcher is no longer available."));
        return completion.Task;
    }

    private void HandleEngineMessage(DownloadItem item, string line)
    {
        var fields = line.Split('\t');
        if (fields.Length == 0) return;
        switch (fields[0])
        {
            case "STATUS" when fields.Length >= 2:
                item.Details = fields[1];
                StatusText.Text = fields[1];
                const string fileNamePrefix = "File name: ";
                if (fields[1].StartsWith(fileNamePrefix, StringComparison.OrdinalIgnoreCase))
                    item.FileName = fields[1][fileNamePrefix.Length..];
                if (fields[1].Contains("paused", StringComparison.OrdinalIgnoreCase)) item.Status = "Paused";
                else if (fields[1].StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)) item.Status = "Downloading";
                break;
            case "ERROR" when fields.Length >= 2:
                item.Status = "Error";
                item.Details = fields[1];
                StatusText.Text = fields[1];
                break;
            case "PROGRESS" when fields.Length >= 5:
                if (!long.TryParse(fields[1], out var completed) || !long.TryParse(fields[2], out var total)) return;
                var now = Stopwatch.GetTimestamp();
                if (item.LastProgressBytes >= 0 && item.LastProgressTimestamp != 0)
                {
                    var elapsed = (now - item.LastProgressTimestamp) / (double)Stopwatch.Frequency;
                    if (elapsed > 0) item.SpeedBytesPerSecond = Math.Max(0, (completed - item.LastProgressBytes) / elapsed);
                }
                item.LastProgressBytes = completed;
                item.LastProgressTimestamp = now;
                item.TotalBytes = total;
                item.BytesDownloaded = completed;
                item.Percent = total > 0 ? Math.Clamp(completed * 100.0 / total, 0, 100) : 0;
                item.ActiveConnections = fields.Skip(5).Count(part =>
                    int.TryParse(part.Split(',')[0], out var state) && state is 1 or 2);
                if (item.Status != "Paused") item.Status = "Downloading";
                break;
            case "DONE":
                item.Status = "Completed";
                item.Details = "Download completed.";
                break;
            case "CANCELLED":
                item.Status = "Cancelled";
                item.Details = "Download cancelled. Checkpointed ranges will resume on retry when supported by the server.";
                break;
            case "FAILED":
                if (item.Status != "Error") item.Status = "Error";
                break;
        }
        UpdateSummary();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => SendControl("pause");

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedDownload;
        if (item is null) { StatusText.Text = "Select a download first."; return; }
        if (item.EngineProcess is { HasExited: false }) SendControl("resume");
        else if (item.Status is "Cancelled" or "Error")
        {
            item.LastProgressBytes = -1;
            item.SpeedBytesPerSecond = 0;
            _ = RunDownloadAsync(item, item.ConnectionLimit);
        }
        else StatusText.Text = "Select a paused or interrupted download.";
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => SendControl("cancel");

    private void SendControl(string command)
    {
        var item = SelectedDownload;
        if (item?.EngineProcess is not { HasExited: false } process)
        {
            StatusText.Text = "Select an active download first.";
            return;
        }
        try
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
            item.Status = command switch { "pause" => "Pausing", "resume" => "Downloading", _ => "Cancelling" };
            RefreshVisibleDownloads();
        }
        catch (Exception ex) { StatusText.Text = $"Could not send {command} command: {ex.Message}"; }
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e) => SetFilter("All");
    private void FilterGeneral_Click(object sender, RoutedEventArgs e) => SetFilter("General");
    private void FilterCompressed_Click(object sender, RoutedEventArgs e) => SetFilter("Compressed");
    private void FilterDocuments_Click(object sender, RoutedEventArgs e) => SetFilter("Documents");
    private void FilterVideos_Click(object sender, RoutedEventArgs e) => SetFilter("Videos");
    private void FilterActive_Click(object sender, RoutedEventArgs e) => SetFilter("Active");
    private void FilterCompleted_Click(object sender, RoutedEventArgs e) => SetFilter("Completed");
    private void FilterPaused_Click(object sender, RoutedEventArgs e) => SetFilter("Paused");

    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        var tabs = new TabView { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false };
        tabs.TabItems.Add(new TabViewItem
        {
            Header = "General",
            Content = new TextBlock
            {
                Text = "TermIDM keeps the download engine in a separate native process. Settings are stored locally for this Windows user.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16)
            }
        });
        var connectionLimitBox = new NumberBox
        {
            Header = "Maximum connections per download (1–128)",
            Value = defaultConnectionLimit,
            Minimum = 1,
            Maximum = 128,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Margin = new Thickness(16)
        };
        var speedLimitBox = new NumberBox { Header = "Default per-download speed limit (MiB/s, 0 = unlimited)", Value = defaultSpeedLimitMiB, Minimum = 0, Maximum = 1_000_000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Margin = new Thickness(16) };
        var timeoutBox = new NumberBox { Header = "Network connect timeout (seconds)", Value = networkTimeoutSeconds, Minimum = 5, Maximum = 600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Margin = new Thickness(16) };
        var connectionSettings = new StackPanel { Spacing = 12 };
        connectionSettings.Children.Add(connectionLimitBox);
        connectionSettings.Children.Add(speedLimitBox);
        connectionSettings.Children.Add(timeoutBox);
        tabs.TabItems.Add(new TabViewItem { Header = "Connection", Content = connectionSettings });
        var saveFolderBox = new TextBox { Text = defaultSaveFolder, PlaceholderText = "Default download folder", MinWidth = 360 };
        var folderSettings = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        folderSettings.Children.Add(new TextBlock { Text = "Default save folder" });
        folderSettings.Children.Add(saveFolderBox);
        var browseDefault = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left };
        browseDefault.Click += async (_, _) =>
        {
            try { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, activeSubWindowHandle ?? WindowNative.GetWindowHandle(this)); var selected = await picker.PickSingleFolderAsync(); if (selected is not null) saveFolderBox.Text = selected.Path; }
            catch (Exception ex) { StatusText.Text = $"Folder picker failed: {ex.Message}"; }
        };
        folderSettings.Children.Add(browseDefault);
        tabs.TabItems.Add(new TabViewItem { Header = "Downloads", Content = folderSettings });
        if (await ShowSubWindowAsync("Global Options", tabs, "Save", "Cancel", 660, 620))
        {
            defaultConnectionLimit = (int)Math.Clamp(connectionLimitBox.Value, 1, 128);
            defaultSpeedLimitMiB = Math.Max(0, speedLimitBox.Value);
            networkTimeoutSeconds = (int)Math.Clamp(timeoutBox.Value, 5, 600);
            defaultSaveFolder = string.IsNullOrWhiteSpace(saveFolderBox.Text) ? defaultSaveFolder : saveFolderBox.Text.Trim();
            SaveSettings();
            StatusText.Text = $"Options saved · {defaultConnectionLimit} connections · {(defaultSpeedLimitMiB == 0 ? "unlimited" : $"{defaultSpeedLimitMiB:0.##} MiB/s")}.";
        }
    }

    private async void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView { MinWidth = 480, MinHeight = 230 };
        foreach (var item in downloads)
        {
            list.Items.Add(new TextBlock
            {
                Text = $"{item.FileName}    ·    {item.Status}    ·    {item.SizeText}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(5)
            });
        }
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "TermIDM · Download Queue Manager", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(list);
        await ShowSubWindowAsync("Scheduler", content, null, "Close", 700, 440);
    }

    private void DownloadList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void SetFilter(string filter)
    {
        currentFilter = filter;
        var buttons = new (string Filter, Button Button)[]
        {
            ("All", NavAll), ("Active", NavDownloading), ("Completed", NavCompleted), ("Paused", NavPaused),
            ("General", NavGeneral), ("Compressed", NavCompressed), ("Documents", NavDocuments), ("Videos", NavVideos)
        };
        foreach (var entry in buttons)
        {
            var selected = entry.Filter == filter;
            entry.Button.Background = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 31, 47, 62)
                : Windows.UI.Color.FromArgb(0, 0, 0, 0));
            entry.Button.BorderBrush = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 47, 75, 98)
                : Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
        RefreshVisibleDownloads();
    }

    private void RefreshVisibleDownloads()
    {
        var selected = SelectedDownload;
        visibleDownloads.Clear();
        foreach (var item in downloads)
        {
            var include = currentFilter switch
            {
                "Active" => item.Status is not ("Completed" or "Cancelled" or "Error"),
                "Completed" => item.Status == "Completed",
                "Paused" => item.Status == "Paused",
                "Compressed" or "Documents" or "Videos" or "General" => item.Category == currentFilter,
                _ => true
            };
            if (include) visibleDownloads.Add(item);
        }
        if (selected is not null && visibleDownloads.Contains(selected)) DownloadList.SelectedItem = selected;
        EmptyState.Visibility = visibleDownloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (downloads.Count == 0)
        {
            EmptyTitle.Text = "Your downloads, at a glance";
            EmptyMessage.Text = "Add a link to start a download. Progress, speed and remaining time will appear here.";
        }
        else
        {
            EmptyTitle.Text = "Nothing in this view";
            EmptyMessage.Text = "There are no downloads matching this filter yet.";
        }
        UpdateSummary();
    }

    private void DownloadItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.Status) or nameof(DownloadItem.FileName))
            RefreshVisibleDownloads();
        else UpdateSummary();
    }

    private void UpdateSummary()
    {
        var active = downloads.Count(item => item.Status is not ("Completed" or "Cancelled" or "Error"));
        AllCount.Text = downloads.Count.ToString(CultureInfo.InvariantCulture);
        DownloadingCount.Text = downloads.Count(item => item.Status is "Connecting" or "Downloading" or "Queued" or "Pausing" or "Cancelling").ToString(CultureInfo.InvariantCulture);
        CompletedCount.Text = downloads.Count(item => item.Status == "Completed").ToString(CultureInfo.InvariantCulture);
        PausedCount.Text = downloads.Count(item => item.Status == "Paused").ToString(CultureInfo.InvariantCulture);
        ActiveSummary.Text = $"{active} active download{(active == 1 ? "" : "s")}";
        TotalSpeedText.Text = FormatSpeed(downloads.Sum(item => item.SpeedBytesPerSecond));
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / 1024 / 1024:0.00} MiB/s"
        : bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024:0.0} KiB/s" : $"{bytesPerSecond:0} B/s";

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (allowClose) return;
        var active = downloads.Where(item => item.EngineProcess is { HasExited: false }).ToList();
        if (active.Count == 0) return;
        e.Cancel = true;
        StatusText.Text = "Stopping active downloads safely before exit…";
        foreach (var item in active)
        {
            try { item.EngineProcess!.StandardInput.WriteLine("cancel"); item.EngineProcess.StandardInput.Flush(); }
            catch { }
        }
        await Task.WhenAll(active.Select(async item =>
        {
            try { await item.EngineProcess!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { try { item.EngineProcess!.Kill(entireProcessTree: true); } catch { } }
            catch { }
        }));
        allowClose = true;
        Close();
    }

    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermIDM", "desktop-settings.json");

    private static long MiBToBytes(double value) => !double.IsFinite(value) || value <= 0 ? 0 : (long)Math.Min(long.MaxValue, Math.Round(value * 1024 * 1024));

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var root = settings.RootElement;
                if (root.TryGetProperty("defaultConnections", out var connections)) defaultConnectionLimit = Math.Clamp(connections.GetInt32(), 1, 128);
                if (root.TryGetProperty("defaultSpeedLimitMiB", out var speed)) defaultSpeedLimitMiB = Math.Clamp(speed.GetDouble(), 0, 1_000_000);
                if (root.TryGetProperty("networkTimeoutSeconds", out var timeout)) networkTimeoutSeconds = Math.Clamp(timeout.GetInt32(), 5, 600);
                if (root.TryGetProperty("defaultSaveFolder", out var folder) && !string.IsNullOrWhiteSpace(folder.GetString())) defaultSaveFolder = folder.GetString()!;
            }
        }
        catch { defaultConnectionLimit = 16; }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { defaultConnections = defaultConnectionLimit, defaultSpeedLimitMiB, networkTimeoutSeconds, defaultSaveFolder }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { StatusText.Text = $"Could not save settings: {ex.Message}"; }
    }
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string status = "Queued", details = "Waiting to start…", fileName;
    private double percent, speedBytesPerSecond;
    private long bytesDownloaded, totalBytes;
    private int activeConnections;
    private readonly string? categoryOverride;
    private readonly object telemetryGate = new();
    private string? pendingTelemetry;
    private bool telemetryDispatchQueued;

    public DownloadItem(string fileName, string url, string folder, string? category = null)
    {
        this.fileName = fileName;
        Url = url;
        Folder = folder;
        categoryOverride = category;
    }

    public string FileName { get => fileName; set { fileName = value; Changed(); Changed(nameof(ProgressText)); } }
    public string Url { get; }
    public string Folder { get; }
    public string Description { get; set; } = "";
    public string Category
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(categoryOverride) && categoryOverride != "Auto") return categoryOverride;
            var extension = Path.GetExtension(FileName).ToLowerInvariant();
            if (new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz" }.Contains(extension)) return "Compressed";
            if (new[] { ".pdf", ".doc", ".docx", ".txt", ".rtf", ".xls", ".xlsx", ".ppt", ".pptx", ".csv" }.Contains(extension)) return "Documents";
            if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg" }.Contains(extension)) return "Videos";
            return "General";
        }
    }
    public Process? EngineProcess { get; set; }
    public long LastProgressBytes { get; set; } = -1;
    public long LastProgressTimestamp { get; set; }
    public string SizeText => TotalBytes > 0 ? FormatBytes(TotalBytes) : "Checking…";
    public string ProgressText => TotalBytes > 0
        ? $"{Percent:0.0}%  ·  ETA {EtaText}"
        : "Connecting…";
    public string SpeedText => FormatRate(SpeedBytesPerSecond);
    public string EtaText => SpeedBytesPerSecond > 0 && TotalBytes >= BytesDownloaded
        ? TimeSpan.FromSeconds(Math.Clamp((TotalBytes - BytesDownloaded) / SpeedBytesPerSecond, 0, 365 * 86400)).ToString(@"h:mm:ss") : "—";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public string Status { get => status; set { status = value; Changed(); } }
    public string Details { get => details; set { details = value; Changed(); } }
    public double Percent { get => percent; set { percent = value; Changed(); Changed(nameof(ProgressText)); } }
    public double SpeedBytesPerSecond { get => speedBytesPerSecond; set { speedBytesPerSecond = value; Changed(); Changed(nameof(SpeedText)); Changed(nameof(EtaText)); Changed(nameof(ProgressText)); } }
    public long BytesDownloaded { get => bytesDownloaded; set { bytesDownloaded = value; Changed(); Changed(nameof(ProgressText)); Changed(nameof(EtaText)); } }
    public long TotalBytes { get => totalBytes; set { totalBytes = value; Changed(); Changed(nameof(SizeText)); Changed(nameof(ProgressText)); Changed(nameof(EtaText)); } }
    public int ActiveConnections { get => activeConnections; set { activeConnections = value; Changed(); } }
    public int ConnectionLimit { get; set; } = 16;
    public long SpeedLimitBytesPerSecond { get; set; }
    public int NetworkTimeoutSeconds { get; set; } = 90;

    public bool QueueTelemetry(string line)
    {
        lock (telemetryGate)
        {
            pendingTelemetry = line;
            if (telemetryDispatchQueued) return false;
            telemetryDispatchQueued = true;
            return true;
        }
    }

    public string? TakeTelemetry()
    {
        lock (telemetryGate)
        {
            var line = pendingTelemetry;
            pendingTelemetry = null;
            telemetryDispatchQueued = false;
            return line;
        }
    }

    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / 1024d / 1024 / 1024:0.00} GiB" : value >= 1024 * 1024 ? $"{value / 1024d / 1024:0.0} MiB" : value >= 1024 ? $"{value / 1024d:0.0} KiB" : $"{value} B";
    private static string FormatRate(double value) => value >= 1024 * 1024 ? $"{value / 1024 / 1024:0.00} MiB/s" : value >= 1024 ? $"{value / 1024:0.0} KiB/s" : $"{value:0} B/s";
}
