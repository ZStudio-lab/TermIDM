using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinRT.Interop;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace TermIDM.Desktop;

public sealed partial class MainWindow : Window
{
    private const string EngineDllName = "EngineBridge.dll";

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Engine_Initialize();

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Engine_Shutdown();

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Engine_SetCallbacks(
        ProgressCallback progressCb,
        StatusCallback statusCb,
        LogCallback logCb,
        IntPtr userData);

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern bool Engine_StartDownload(
        string url,
        string destinationFolder,
        int maxConnections,
        long maxSpeedBytesPerSec,
        int timeoutSeconds);

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Engine_PauseDownload();

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Engine_ResumeDownload();

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Engine_CancelDownload();

    [DllImport(EngineDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Engine_IsRunning();

    private delegate void ProgressCallback(IntPtr userData, string fileName, long bytesDownloaded, long totalBytes, int activeConnections, double speedBps);
    private delegate void StatusCallback(IntPtr userData, string status, string message);
    private delegate void LogCallback(IntPtr userData, string message);

    private static ProgressCallback _progressCallback;
    private static StatusCallback _statusCallback;
    private static LogCallback _logCallback;
    private GCHandle _gcHandle;
    private bool _isDisposed = false;

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
    private DownloadItem? _currentDownload;
    private DispatcherTimer? _progressTimer;

    public MainWindow()
    {
        InitializeComponent();
        var logoPath = Path.Combine(AppContext.BaseDirectory, "assets", "termidm.ico");
        if (File.Exists(logoPath)) BrandLogoImage.Source = new BitmapImage(new Uri(logoPath));
        uiQueue = DispatcherQueue.GetForCurrentThread();
        DownloadList.ItemsSource = visibleDownloads;
        Root.RequestedTheme = ElementTheme.Dark;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            SystemBackdrop = new MicaBackdrop();
        else
            Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 12, 16));
        LoadSettings();
        LoadHistory();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = $"{version?.Major}.{version?.Minor}.{version?.Build}";
        Title = $"TermIDM v{versionString} \u00B7 Downloads";
        VersionText.Text = $"Desktop download manager  \u00B7  {versionString}";

        var hwnd = WindowNative.GetWindowHandle(this);
        appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1180, Height = 760 });
        appWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => Application.Current.Exit();
        SetFilter("All");
        UpdateSummary();

        InitializeEngine();
    }

    private void InitializeEngine()
    {
        try
        {
            _progressCallback = OnEngineProgress;
            _statusCallback = OnEngineStatus;
            _logCallback = OnEngineLog;
            _gcHandle = GCHandle.Alloc(this);

            Engine_SetCallbacks(_progressCallback, _statusCallback, _logCallback, (IntPtr)_gcHandle);
            Engine_Initialize();
            Log("Native engine initialized successfully.");
        }
        catch (Exception ex)
        {
            Log($"Failed to initialize native engine: {ex}");
        }
    }

    private void OnEngineProgress(IntPtr userData, string fileName, long bytesDownloaded, long totalBytes, int activeConnections, double speedBps)
    {
        if (_isDisposed) return;
        
        uiQueue.TryEnqueue(() =>
        {
            try
            {
                if (_isDisposed) return;

                if (_currentDownload != null)
                {
                    _currentDownload.FileName = fileName;
                    _currentDownload.BytesDownloaded = bytesDownloaded;
                    _currentDownload.TotalBytes = totalBytes;
                    _currentDownload.Percent = totalBytes > 0 ? Math.Clamp(bytesDownloaded * 100.0 / totalBytes, 0, 100) : 0;
                    _currentDownload.SpeedBytesPerSecond = (long)speedBps;
                    _currentDownload.ActiveConnections = activeConnections;
                    if (_currentDownload.Status != "Paused")
                        _currentDownload.Status = "Downloading";
                    _currentDownload.Details = FormatSpeed(speedBps);
                    
                    StartProgressTimer();
                }
                UpdateSummary();
            }
            catch (Exception ex)
            {
                Log($"Error in OnEngineProgress: {ex}");
            }
        });
    }

    private void OnEngineStatus(IntPtr userData, string status, string message)
    {
        if (_isDisposed) return;
        
        uiQueue.TryEnqueue(() =>
        {
            try
            {
                if (_isDisposed) return;

                StatusText.Text = message;
                Log($"Engine status: {status} - {message}");

                if (_currentDownload != null)
                {
                    switch (status)
                    {
                        case "Completed":
                            _currentDownload.Status = "Completed";
                            _currentDownload.Percent = 100;
                            _currentDownload.SpeedBytesPerSecond = 0;
                            _currentDownload.Details = "Download completed.";
                            SaveToHistory(_currentDownload);
                            _currentDownload = null;
                            StopProgressTimer();
                            break;
                        case "Cancelled":
                            _currentDownload.Status = "Cancelled";
                            _currentDownload.Details = "Download cancelled.";
                            SaveToHistory(_currentDownload);
                            _currentDownload = null;
                            StopProgressTimer();
                            break;
                        case "Error":
                            _currentDownload.Status = "Error";
                            _currentDownload.Details = message;
                            SaveToHistory(_currentDownload);
                            _currentDownload = null;
                            StopProgressTimer();
                            break;
                        case "Paused":
                            if (_currentDownload.Status == "Downloading")
                                _currentDownload.Status = "Paused";
                            break;
                        case "Resumed":
                            if (_currentDownload.Status == "Paused")
                                _currentDownload.Status = "Downloading";
                            break;
                    }
                }

                RefreshVisibleDownloads();
                UpdateSummary();
            }
            catch (Exception ex)
            {
                Log($"Error in OnEngineStatus: {ex}");
            }
        });
    }

    private void OnEngineLog(IntPtr userData, string message)
    {
        Log(message);
    }

    private void StartProgressTimer()
    {
        if (_progressTimer != null) return;
        
        _progressTimer = new DispatcherTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _progressTimer.Tick += (s, e) =>
        {
            if (_currentDownload == null || _currentDownload.Status is "Completed" or "Cancelled" or "Error")
            {
                StopProgressTimer();
                return;
            }
            var temp = _currentDownload.SpeedBytesPerSecond;
            _currentDownload.SpeedBytesPerSecond = 0;
            _currentDownload.SpeedBytesPerSecond = temp;
            
            var tempPercent = _currentDownload.Percent;
            _currentDownload.Percent = 0;
            _currentDownload.Percent = tempPercent;
            
            UpdateSummary();
        };
        _progressTimer.Start();
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
    }

    private DownloadItem? SelectedDownload => DownloadList.SelectedItem as DownloadItem;

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
        var browse = new Button { Content = "Browse\u2026", MinWidth = 92 };
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
        await Task.Yield();

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

        var fileName = "download";
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
        StatusText.Text = "Starting download...";
        _currentDownload = item;
        RunDownload(item, connectionLimit);
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
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 20, 28)),
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
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 130, 246)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 99, 235))
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

    private void RunDownload(DownloadItem item, int connectionLimit)
    {
        try
        {
            item.Status = "Connecting";
            bool started = Engine_StartDownload(
                item.Url,
                item.Folder,
                connectionLimit,
                item.SpeedLimitBytesPerSecond,
                item.NetworkTimeoutSeconds);

            if (!started)
            {
                item.Status = "Error";
                item.Details = "Failed to start download. Another download may already be in progress.";
                StatusText.Text = item.Details;
                _currentDownload = null;
            }
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.Details = ex.Message;
            StatusText.Text = ex.Message;
            _currentDownload = null;
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedDownload;
        if (item is null) { StatusText.Text = "Select a download first."; return; }
        if (Engine_PauseDownload())
        {
            item.Status = "Pausing";
            RefreshVisibleDownloads();
        }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedDownload;
        if (item is null) { StatusText.Text = "Select a download first."; return; }
        if (item.Status is "Paused" or "Pausing")
        {
            if (Engine_ResumeDownload())
            {
                item.Status = "Downloading";
                _currentDownload = item;
                RefreshVisibleDownloads();
            }
        }
        else if (item.Status is "Cancelled" or "Error")
        {
            _currentDownload = item;
            RunDownload(item, item.ConnectionLimit);
        }
        else StatusText.Text = "Select a paused or interrupted download.";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedDownload;
        if (item is null) { StatusText.Text = "Select a download first."; return; }
        if (Engine_CancelDownload())
        {
            item.Status = "Cancelling";
            RefreshVisibleDownloads();
        }
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        int count = 0;
        for (int i = downloads.Count - 1; i >= 0; i--)
        {
            var item = downloads[i];
            if (item.Status is "Completed" or "Cancelled" or "Error")
            {
                downloads.RemoveAt(i);
                count++;
            }
        }
        RefreshVisibleDownloads();
        UpdateSummary();
        StatusText.Text = count > 0 ? $"Cleared {count} completed download(s)." : "No completed downloads to clear.";
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

        // General Tab
        var generalPanel = new StackPanel { Spacing = 16, Margin = new Thickness(16) };
        
        var themePanel = new StackPanel { Spacing = 8 };
        themePanel.Children.Add(new TextBlock { Text = "Theme", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
        var themeCombo = new ComboBox { Width = 200, SelectedIndex = 0 };
        themeCombo.Items.Add("Dark");
        themeCombo.Items.Add("Light");
        themeCombo.Items.Add("System");
        themeCombo.SelectionChanged += (_, _) =>
        {
            StatusText.Text = $"Theme set to: {themeCombo.SelectedItem}";
        };
        themePanel.Children.Add(themeCombo);
        generalPanel.Children.Add(themePanel);

        var startupPanel = new StackPanel { Spacing = 8 };
        startupPanel.Children.Add(new TextBlock { Text = "Startup", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
        var startWithWindows = new ToggleSwitch { Header = "Start with Windows", OnContent = "Enabled", OffContent = "Disabled" };
        startWithWindows.Toggled += (_, _) =>
        {
            try
            {
                if (startWithWindows.IsOn)
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                    if (key != null)
                    {
                        key.SetValue("TermIDM", $"\"{System.Reflection.Assembly.GetExecutingAssembly().Location}\"");
                    }
                    StatusText.Text = "TermIDM will start with Windows.";
                }
                else
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                    if (key != null)
                    {
                        key.DeleteValue("TermIDM", false);
                    }
                    StatusText.Text = "TermIDM will not start with Windows.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Startup setting failed: {ex.Message}";
            }
        };
        startupPanel.Children.Add(startWithWindows);
        generalPanel.Children.Add(startupPanel);
        tabs.TabItems.Add(new TabViewItem { Header = "General", Content = generalPanel });

        // Connection Tab
        var connectionLimitBox = new NumberBox
        {
            Header = "Maximum connections per download (1\u2013128)",
            Value = defaultConnectionLimit,
            Minimum = 1,
            Maximum = 128,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Margin = new Thickness(16)
        };
        var speedLimitBox = new NumberBox { Header = "Default speed limit (MiB/s, 0 = unlimited)", Value = defaultSpeedLimitMiB, Minimum = 0, Maximum = 1_000_000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Margin = new Thickness(16) };
        var timeoutBox = new NumberBox { Header = "Connect timeout (seconds)", Value = networkTimeoutSeconds, Minimum = 5, Maximum = 600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Margin = new Thickness(16) };
        var retryBox = new NumberBox { Header = "Max retry attempts", Value = 3, Minimum = 1, Maximum = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Margin = new Thickness(16) };
        var connectionSettings = new StackPanel { Spacing = 12 };
        connectionSettings.Children.Add(connectionLimitBox);
        connectionSettings.Children.Add(speedLimitBox);
        connectionSettings.Children.Add(timeoutBox);
        connectionSettings.Children.Add(retryBox);
        tabs.TabItems.Add(new TabViewItem { Header = "Connection", Content = connectionSettings });

        // Downloads Tab
        var saveFolderBox = new TextBox { Text = defaultSaveFolder, PlaceholderText = "Default download folder", MinWidth = 400 };
        var folderSettings = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        folderSettings.Children.Add(new TextBlock { Text = "Default save folder", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
        folderSettings.Children.Add(saveFolderBox);
        var browseDefault = new Button { Content = "Browse\u2026", HorizontalAlignment = HorizontalAlignment.Left };
        browseDefault.Click += async (_, _) =>
        {
            try { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, activeSubWindowHandle ?? WindowNative.GetWindowHandle(this)); var selected = await picker.PickSingleFolderAsync(); if (selected is not null) saveFolderBox.Text = selected.Path; }
            catch (Exception ex) { StatusText.Text = $"Folder picker failed: {ex.Message}"; }
        };
        folderSettings.Children.Add(browseDefault);

        folderSettings.Children.Add(new TextBlock { Text = "Download History", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(0,16,0,0) });
        var historyPanel = new StackPanel { Spacing = 8 };
        var historyInfo = new TextBlock { Text = $"History file: {HistoryPath}", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), TextWrapping = TextWrapping.Wrap };
        historyPanel.Children.Add(historyInfo);
        var clearHistoryBtn = new Button { Content = "Clear Download History", HorizontalAlignment = HorizontalAlignment.Left };
        clearHistoryBtn.Click += (_, _) =>
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    File.Delete(HistoryPath);
                    StatusText.Text = "Download history cleared.";
                }
                else
                {
                    StatusText.Text = "No download history found.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to clear history: {ex.Message}";
            }
        };
        historyPanel.Children.Add(clearHistoryBtn);
        folderSettings.Children.Add(historyPanel);
        tabs.TabItems.Add(new TabViewItem { Header = "Downloads", Content = folderSettings });

        // About Tab
        var aboutPanel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        aboutPanel.Children.Add(new TextBlock { Text = "TermIDM", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
        aboutPanel.Children.Add(new TextBlock { Text = $"Version: {Assembly.GetExecutingAssembly().GetName().Version}", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 12 });
        aboutPanel.Children.Add(new TextBlock { Text = "Integrated native download engine", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 12 });
        aboutPanel.Children.Add(new TextBlock { Text = "Built with WinUI 3 and libcurl", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 12 });
        aboutPanel.Children.Add(new TextBlock { Text = "", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 8 });
        aboutPanel.Children.Add(new TextBlock { Text = "Engine Features:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12 });
        aboutPanel.Children.Add(new TextBlock { Text = "- HTTP/HTTPS with range request support", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Multi-threaded segmented downloading", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Browser-like stealth headers", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Automatic retry with exponential backoff", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Content-Disposition filename detection", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Pause / Resume / Cancel support", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Speed limiting", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 8 });
        aboutPanel.Children.Add(new TextBlock { Text = "UI Features:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Real-time progress and speed display", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Download history persistence (CSV)", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Category and status filtering", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Dark Fluent UI with Mica backdrop", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- Settings with theme and startup options", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 8 });
        aboutPanel.Children.Add(new TextBlock { Text = "Tech Stack:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12 });
        aboutPanel.Children.Add(new TextBlock { Text = "- C# / .NET 8 / WinUI 3 (Windows App SDK 2.5)", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- C++17 native engine via P/Invoke bridge", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- libcurl for HTTP transfers", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        aboutPanel.Children.Add(new TextBlock { Text = "- ASP.NET Core license API (separate)", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 11 });
        tabs.TabItems.Add(new TabViewItem { Header = "About", Content = aboutPanel });

        if (await ShowSubWindowAsync("Settings", tabs, "Save", "Cancel", 700, 680))
        {
            defaultConnectionLimit = (int)Math.Clamp(connectionLimitBox.Value, 1, 128);
            defaultSpeedLimitMiB = Math.Max(0, speedLimitBox.Value);
            networkTimeoutSeconds = (int)Math.Clamp(timeoutBox.Value, 5, 600);
            defaultSaveFolder = string.IsNullOrWhiteSpace(saveFolderBox.Text) ? defaultSaveFolder : saveFolderBox.Text.Trim();
            SaveSettings();
            StatusText.Text = $"Settings saved \u00B7 {defaultConnectionLimit} connections \u00B7 {(defaultSpeedLimitMiB == 0 ? "unlimited" : $"{defaultSpeedLimitMiB:0.##} MiB/s")}.";
        }
    }

    private async void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView { MinWidth = 480, MinHeight = 230 };
        foreach (var item in downloads)
        {
            list.Items.Add(new TextBlock
            {
                Text = $"{item.FileName}    \u00B7    {item.Status}    \u00B7    {item.SizeText}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(5)
            });
        }
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "TermIDM \u00B7 Download Queue Manager", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
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
        DownloadingCount.Text = downloads.Count(item => item.Status is "Connecting" or "Downloading" or "Queued").ToString(CultureInfo.InvariantCulture);
        CompletedCount.Text = downloads.Count(item => item.Status == "Completed").ToString(CultureInfo.InvariantCulture);
        PausedCount.Text = downloads.Count(item => item.Status == "Paused" || item.Status == "Pausing").ToString(CultureInfo.InvariantCulture);
        ActiveSummary.Text = $"{active} active download{(active == 1 ? "" : "s")}";
        TotalSpeedText.Text = FormatSpeed(downloads.Sum(item => item.SpeedBytesPerSecond));
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / 1024 / 1024:0.00} MiB/s"
        : bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024:0.0} KiB/s" : $"{bytesPerSecond:0} B/s";

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        Log("AppWindow_Closing invoked");
        if (allowClose) return;
        var active = downloads.Where(item => item.Status is "Downloading" or "Connecting" or "Paused").ToList();
        if (active.Count == 0) return;
        e.Cancel = true;
        StatusText.Text = "Stopping active downloads safely before exit\u2026";
        Log($"{active.Count} active downloads, attempting graceful shutdown");
        Engine_CancelDownload();
        await Task.Delay(2000);
        allowClose = true;
        Log("AllowClose set true; calling Close()");
        Close();
    }

    // Download history persistence
    private string HistoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermIDM", "download-history.csv");

    private void SaveToHistory(DownloadItem item)
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            
            bool fileExists = File.Exists(HistoryPath);
            using var writer = new StreamWriter(HistoryPath, true, Encoding.UTF8);
            if (!fileExists)
            {
                writer.WriteLine("Timestamp,FileName,Url,Folder,Category,Status,SizeBytes");
            }
            writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss},{EscapeCsv(item.FileName)},{EscapeCsv(item.Url)},{EscapeCsv(item.Folder)},{item.Category},{item.Status},{item.TotalBytes}");
        }
        catch (Exception ex)
        {
            Log($"Failed to save history: {ex.Message}");
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var lines = File.ReadAllLines(HistoryPath, Encoding.UTF8);
                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = ParseCsvLine(lines[i]);
                    if (parts.Length >= 7)
                    {
                        var item = new DownloadItem(parts[1], parts[2], parts[3], parts[4])
                        {
                            Status = parts[5],
                            TotalBytes = long.TryParse(parts[6], out var size) ? size : 0
                        };
                        downloads.Add(item);
                    }
                }
                RefreshVisibleDownloads();
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to load history: {ex.Message}");
        }
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new System.Collections.Generic.List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
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

    private static void Log(string message)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermIDM", "logs");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, "trace.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string status = "Queued", details = "Waiting to start\u2026", fileName;
    private double percent, speedBytesPerSecond;
    private long bytesDownloaded, totalBytes;
    private int activeConnections;
    private readonly string? categoryOverride;

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
    public long LastProgressBytes { get; set; } = -1;
    public long LastProgressTimestamp { get; set; }
    public string SizeText => TotalBytes > 0 ? FormatBytes(TotalBytes) : "Checking\u2026";
    public string ProgressText => TotalBytes > 0
        ? $"{Percent:0.0}%  \u00B7  ETA {EtaText}"
        : "Connecting\u2026";
    public string SpeedText => FormatRate(SpeedBytesPerSecond);
    public string EtaText
    {
        get
        {
            if (SpeedBytesPerSecond <= 0 || TotalBytes <= 0 || TotalBytes < BytesDownloaded)
                return "\u2014";
            var remaining = TotalBytes - BytesDownloaded;
            var seconds = remaining / SpeedBytesPerSecond;
            if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds))
                return "\u2014";
            var ts = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 365 * 86400));
            if (ts.TotalHours >= 24)
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            return ts.ToString(@"h\:mm\:ss");
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public string Status
    {
        get => status;
        set
        {
            if (string.Equals(status, value, StringComparison.Ordinal)) return;
            status = value;
            Changed();
        }
    }
    public string Details
    {
        get => details;
        set
        {
            if (string.Equals(details, value, StringComparison.Ordinal)) return;
            details = value;
            Changed();
        }
    }
    public double Percent { get => percent; set { percent = value; Changed(); Changed(nameof(ProgressText)); } }
    public double SpeedBytesPerSecond { get => speedBytesPerSecond; set { speedBytesPerSecond = value; Changed(); Changed(nameof(SpeedText)); Changed(nameof(EtaText)); Changed(nameof(ProgressText)); } }
    public long BytesDownloaded { get => bytesDownloaded; set { bytesDownloaded = value; Changed(); Changed(nameof(ProgressText)); Changed(nameof(EtaText)); } }
    public long TotalBytes { get => totalBytes; set { totalBytes = value; Changed(); Changed(nameof(SizeText)); Changed(nameof(ProgressText)); Changed(nameof(EtaText)); } }
    public int ActiveConnections { get => activeConnections; set { activeConnections = value; Changed(); } }
    public int ConnectionLimit { get; set; } = 16;
    public long SpeedLimitBytesPerSecond { get; set; }
    public int NetworkTimeoutSeconds { get; set; } = 90;

    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / 1024d / 1024 / 1024:0.00} GiB" : value >= 1024 * 1024 ? $"{value / 1024d / 1024:0.0} MiB" : value >= 1024 ? $"{value / 1024d:0.0} KiB" : $"{value} B";
    private static string FormatRate(double value) => value >= 1024 * 1024 ? $"{value / 1024 / 1024:0.00} MiB/s" : value >= 1024 ? $"{value / 1024:0.0} KiB/s" : $"{value:0} B/s";
}
