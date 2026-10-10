using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace RecWorksLauncher
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient httpClient = new HttpClient();

        private string installPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RecWorks", "Game");

        private const string CurrentLauncherVersion = "1.0.0";

        private const string LatestReleaseApi =
            "https://api.github.com/repos/recrewind/RecWorksLauncher/releases/latest";

        private const string DriveFileId = "167KgKEiFbOhtq9UPd69QFBfJG5e4J0tB";
        private readonly string settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RecWorks", "launcher-settings.json");
        private LauncherSettings launcherSettings = new LauncherSettings();
        private bool isBusy;
        private bool wasCancelled;
        private CancellationTokenSource? installCancellation;

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            InstallPathText.Text = installPath;
            SettingsInstallPathText.Text = installPath;
            LaunchArgumentsTextBox.Text = launcherSettings.LaunchArguments;
            CloseLauncherCheckBox.IsChecked = launcherSettings.CloseLauncherAfterLaunch;
            BackgroundThemeComboBox.SelectedIndex = launcherSettings.BackgroundThemeIndex;
            TextScaleComboBox.SelectedIndex = launcherSettings.TextScaleIndex;
            HighContrastCheckBox.IsChecked = launcherSettings.HighContrast;
            Loaded += (_, _) =>
            {
                CaptureOriginalFontSizes(this);
                ApplyAppearanceSettings();
            };
            RefreshGameState();
            Loaded += async (_, _) => await CheckForLauncherUpdateAsync();
        }

        private bool IsGameInstalled => File.Exists(Path.Combine(installPath, "RecRewind.exe"));

        private void RefreshGameState()
        {
            InstallPathText.Text = installPath;

            if (isBusy)
                return;

            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            CancelButton.Content = "Cancel";

            if (IsGameInstalled)
            {
                PageHeaderText.Text = "Ready to play";
                StatusText.Text = "RecWorks is ready to play";
                ActivityText.Text = "Installed and ready.";
                EtaText.Text = "Installed";
                StageTextSafe("Installed");
                InstallButton.Visibility = Visibility.Collapsed;
                PlayButton.Visibility = Visibility.Visible;
                InstallProgressBar.Value = 100;
                ProgressPercentText.Text = "100%";
                DownloadSizeText.Text = "";
            }
            else
            {
                PageHeaderText.Text = "RecWorks";
                StatusText.Text = "Ready to install RecWorks";
                ActivityText.Text = "Install the game to get started.";
                EtaText.Text = "Ready when you are";
                StageTextSafe("Not installed");
                InstallButton.Visibility = Visibility.Visible;
                PlayButton.Visibility = Visibility.Collapsed;
                InstallProgressBar.Value = 0;
                ProgressPercentText.Text = "0%";
                DownloadSizeText.Text = "";
            }
        }

        // Kept as a helper so the XAML can stay focused on the visible progress UI.
        private void StageTextSafe(string value) { /* stage is represented by the status and activity text */ }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
                return;

            isBusy = true;
            wasCancelled = false;
            installCancellation = new CancellationTokenSource();
            CancellationToken token = installCancellation.Token;

            string zipPath = Path.Combine(Path.GetTempPath(), "RecRewind_download.zip");
            string stagingPath = Path.Combine(Path.GetTempPath(), "RecRewind_staging_" + Guid.NewGuid().ToString("N"));

            InstallButton.Visibility = Visibility.Collapsed;
            PlayButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible;
            InstallProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
            PageHeaderText.Text = "Installing RecWorks";
            StatusText.Text = "Installing RecWorks...";
            ActivityText.Text = "Preparing download...";
            EtaText.Text = "Getting things ready";

            try
            {
                Directory.CreateDirectory(installPath);
                string url = "https://drive.usercontent.google.com/download?id=" +
                             Uri.EscapeDataString(DriveFileId) +
                             "&export=download&confirm=t";

                StatusText.Text = "Downloading RecWorks";
                ActivityText.Text = "Connecting to download server...";
                EtaText.Text = "Downloading";

                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                long downloaded = 0;
                var timer = Stopwatch.StartNew();

                using (var input = await response.Content.ReadAsStreamAsync(token))
                using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;

                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        downloaded += read;

                        if (totalBytes is > 0)
                        {
                            double percent = Math.Min(99, downloaded * 100.0 / totalBytes.Value);
                            InstallProgressBar.Value = percent;
                            ProgressPercentText.Text = $"{percent:0}%";
                            double speed = downloaded / Math.Max(timer.Elapsed.TotalSeconds, 0.1);
                            double remaining = (totalBytes.Value - downloaded) / Math.Max(speed, 1);
                            EtaText.Text = FormatTime(remaining) + " left";
                            DownloadSizeText.Text = $"{downloaded / 1048576.0:0.0} MB / {totalBytes.Value / 1048576.0:0.0} MB";
                            ActivityText.Text = $"Downloading... {percent:0}%";
                        }
                        else
                        {
                            EtaText.Text = "Downloading";
                            DownloadSizeText.Text = $"{downloaded / 1048576.0:0.0} MB downloaded";
                            ActivityText.Text = "Downloading game files...";
                        }

                        await Dispatcher.InvokeAsync(() => { });
                    }
                }

                token.ThrowIfCancellationRequested();
                byte[] signature = new byte[4];
                using (var file = File.OpenRead(zipPath))
                {
                    if (file.Read(signature, 0, 4) < 4 || signature[0] != 0x50 || signature[1] != 0x4B)
                        throw new InvalidDataException("The download did not look like a ZIP file. Check the Google Drive sharing settings.");
                }

                StatusText.Text = "Verifying download";
                ActivityText.Text = "Checking downloaded archive...";
                EtaText.Text = "Verifying";
                token.ThrowIfCancellationRequested();
                StatusText.Text = "Extracting RecWorks";
                ActivityText.Text = "Unpacking game assets...";
                EtaText.Text = "Extracting";
                InstallProgressBar.Value = 0;
                ProgressPercentText.Text = "0%";
                DownloadSizeText.Text = "";
                Directory.CreateDirectory(stagingPath);

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
                    if (files.Count == 0)
                        throw new InvalidDataException("The ZIP archive contains no files.");

                    string stagingRoot = Path.GetFullPath(stagingPath) + Path.DirectorySeparatorChar;
                    int completed = 0;
                    long totalExtractBytes = files.Sum(entry => Math.Max(0, entry.Length));
                    long extractedBytes = 0;

                    foreach (var entry in files)
                    {
                        token.ThrowIfCancellationRequested();
                        string destination = Path.GetFullPath(Path.Combine(stagingPath, entry.FullName));
                        if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("An unsafe file path was found in the ZIP archive.");

                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        using (var source = entry.Open())
                        using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                            await source.CopyToAsync(target, token);

                        completed++;
                        extractedBytes += entry.Length;
                        double percent = totalExtractBytes > 0
                            ? extractedBytes * 100.0 / totalExtractBytes
                            : completed * 100.0 / files.Count;
                        InstallProgressBar.Value = percent;
                        ProgressPercentText.Text = $"{percent:0}%";
                        DownloadSizeText.Text = totalExtractBytes > 0
                            ? $"{extractedBytes / 1073741824.0:0.00} GB / {totalExtractBytes / 1073741824.0:0.00} GB"
                            : "";
                        ActivityText.Text = percent >= 99.5 ? "Finalizing installation..." : "Unpacking game assets...";
                        EtaText.Text = "Extracting";
                        await Dispatcher.InvokeAsync(() => { });
                    }
                }

                token.ThrowIfCancellationRequested();
                string? gameExe = Directory.GetFiles(stagingPath, "RecRewind.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (gameExe == null)
                    throw new FileNotFoundException("RecRewind.exe was not found in the downloaded archive.");

                string gameRoot = Path.GetDirectoryName(gameExe)!;
                StatusText.Text = "Finalizing installation";
                ActivityText.Text = "Copying game files into place...";
                EtaText.Text = "Finalizing";
                InstallProgressBar.Value = 99;
                ProgressPercentText.Text = "99%";

                string[] directories = Directory.GetDirectories(gameRoot);
                string[] rootFiles = Directory.GetFiles(gameRoot);
                int totalItems = directories.Length + rootFiles.Length;
                int installedItems = 0;

                foreach (string directory in directories)
                {
                    token.ThrowIfCancellationRequested();
                    CopyDirectory(directory, Path.Combine(installPath, Path.GetFileName(directory)));
                    installedItems++;
                    UpdateProgress(installedItems, totalItems, "Finalizing installation...");
                    await Dispatcher.InvokeAsync(() => { });
                }

                foreach (string file in rootFiles)
                {
                    token.ThrowIfCancellationRequested();
                    File.Copy(file, Path.Combine(installPath, Path.GetFileName(file)), true);
                    installedItems++;
                    UpdateProgress(installedItems, totalItems, "Finalizing installation...");
                    await Dispatcher.InvokeAsync(() => { });
                }

                if (!IsGameInstalled)
                    throw new IOException("Installation finished, but RecRewind.exe was not found in the install folder.");

                InstallProgressBar.Value = 100;
                ProgressPercentText.Text = "100%";
                PageHeaderText.Text = "Ready to play RecWorks";
                StatusText.Text = "RecWorks is ready to play";
                ActivityText.Text = "Installation complete.";
                EtaText.Text = "Complete";
                DownloadSizeText.Text = "";
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                PageHeaderText.Text = "RecWorks";
                StatusText.Text = "Installation cancelled";
                ActivityText.Text = "You can try again whenever you're ready.";
                EtaText.Text = "Cancelled";
                DownloadSizeText.Text = "";
            }
            catch (Exception ex)
            {
                PageHeaderText.Text = "RecWorks";
                StatusText.Text = "Installation failed";
                ActivityText.Text = ex.Message;
                EtaText.Text = "Stopped";
                MessageBox.Show(ex.Message, "RecWorks Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true); } catch { }

                installCancellation?.Dispose();
                installCancellation = null;
                isBusy = false;
                CancelButton.Visibility = Visibility.Collapsed;
                CancelButton.IsEnabled = true;
                CancelButton.Content = "Cancel";

                if (IsGameInstalled)
                {
                    InstallButton.Visibility = Visibility.Collapsed;
                    PlayButton.Visibility = Visibility.Visible;
                    InstallProgressBar.Value = 100;
                    ProgressPercentText.Text = "100%";
                    PageHeaderText.Text = "Ready to play RecWorks";
                    StatusText.Text = "RecWorks is ready to play";
                    EtaText.Text = "Installed";
                }
                else
                {
                    InstallButton.Visibility = Visibility.Visible;
                    PlayButton.Visibility = Visibility.Collapsed;
                    if (wasCancelled)
                    {
                        InstallProgressBar.Value = 0;
                        ProgressPercentText.Text = "0%";
                        EtaText.Text = "Cancelled";
                    }
                }
            }
        }

        private void UpdateProgress(int completed, int total, string message)
        {
            double percent = total == 0 ? 100 : completed * 100.0 / total;
            InstallProgressBar.Value = Math.Min(99, percent);
            ProgressPercentText.Text = $"{InstallProgressBar.Value:0}%";
            ActivityText.Text = message;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            installCancellation?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
            ActivityText.Text = "Cancelling safely...";
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return "Calculating...";
            var time = TimeSpan.FromSeconds(seconds);
            if (time.TotalHours >= 1) return $"{(int)time.TotalHours}h {time.Minutes}m";
            if (time.TotalMinutes >= 1) return $"{time.Minutes}m {time.Seconds}s";
            return $"{Math.Max(0, time.Seconds)}s";
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        private void Home_Click(object sender, RoutedEventArgs e)
        {
            HomePage.Visibility = Visibility.Visible;
            SettingsPage.Visibility = Visibility.Collapsed;
            PageHeaderText.Text = IsGameInstalled ? "Ready to play" : "RecWorks";
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                MessageBox.Show("Please wait for the current install operation to finish before opening settings.", "RecWorks Launcher");
                return;
            }

            SettingsInstallPathText.Text = installPath;
            LaunchArgumentsTextBox.Text = launcherSettings.LaunchArguments;
            CloseLauncherCheckBox.IsChecked = launcherSettings.CloseLauncherAfterLaunch;
            BackgroundThemeComboBox.SelectedIndex = launcherSettings.BackgroundThemeIndex;
            TextScaleComboBox.SelectedIndex = launcherSettings.TextScaleIndex;
            HighContrastCheckBox.IsChecked = launcherSettings.HighContrast;
            SettingsFeedbackText.Text = "";
            HomePage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
            PageHeaderText.Text = "Settings";
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            launcherSettings.LaunchArguments = LaunchArgumentsTextBox.Text.Trim();
            launcherSettings.CloseLauncherAfterLaunch = CloseLauncherCheckBox.IsChecked == true;
            launcherSettings.BackgroundThemeIndex = Math.Clamp(BackgroundThemeComboBox.SelectedIndex, 0, 2);
            launcherSettings.TextScaleIndex = Math.Clamp(TextScaleComboBox.SelectedIndex, 0, 2);
            launcherSettings.HighContrast = HighContrastCheckBox.IsChecked == true;
            ApplyAppearanceSettings();
            SaveSettingsToDisk();
            SettingsFeedbackText.Text = "Settings saved.";
            RefreshGameState();
            SettingsPage.Visibility = Visibility.Visible;
            HomePage.Visibility = Visibility.Collapsed;
            PageHeaderText.Text = "Settings";
        }

        private void RestoreSettings_Click(object sender, RoutedEventArgs e)
        {
            launcherSettings = new LauncherSettings();
            installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecWorks", "Game");
            LaunchArgumentsTextBox.Text = launcherSettings.LaunchArguments;
            CloseLauncherCheckBox.IsChecked = launcherSettings.CloseLauncherAfterLaunch;
            BackgroundThemeComboBox.SelectedIndex = launcherSettings.BackgroundThemeIndex;
            TextScaleComboBox.SelectedIndex = launcherSettings.TextScaleIndex;
            HighContrastCheckBox.IsChecked = launcherSettings.HighContrast;
            SettingsInstallPathText.Text = installPath;
            InstallPathText.Text = installPath;
            SaveSettingsToDisk();
            RefreshGameState();
            SettingsPage.Visibility = Visibility.Visible;
            HomePage.Visibility = Visibility.Collapsed;
            PageHeaderText.Text = "Settings";
            BackgroundThemeComboBox.SelectedIndex = launcherSettings.BackgroundThemeIndex;
            TextScaleComboBox.SelectedIndex = launcherSettings.TextScaleIndex;
            HighContrastCheckBox.IsChecked = launcherSettings.HighContrast;
            ApplyAppearanceSettings();
            SettingsFeedbackText.Text = "Default settings restored.";
        }

        private readonly Dictionary<FrameworkElement, double> originalFontSizes = new Dictionary<FrameworkElement, double>();
        private readonly Dictionary<TextBlock, Brush> originalTextColors = new Dictionary<TextBlock, Brush>();

        private void CaptureOriginalFontSizes(DependencyObject parent)
        {
            if (parent is not Visual && parent is not System.Windows.Media.Media3D.Visual3D)
                return;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                // FontSize is defined on Control and TextBlock, not FrameworkElement.
                if (child is Control control && !originalFontSizes.ContainsKey(control))
                    originalFontSizes[control] = control.FontSize;
                else if (child is TextBlock textBlock && !originalFontSizes.ContainsKey(textBlock))
                    originalFontSizes[textBlock] = textBlock.FontSize;

                if (child is TextBlock textColorBlock && !originalTextColors.ContainsKey(textColorBlock))
                    originalTextColors[textColorBlock] = textColorBlock.Foreground;
                CaptureOriginalFontSizes(child);
            }
        }

        private void ApplyAppearanceSettings()
        {
            int themeIndex = Math.Clamp(launcherSettings.BackgroundThemeIndex, 0, 2);
            Color background;
            Color sidebar;
            Color panel;
            Color line;

            switch (themeIndex)
            {
                case 0: // Rec Room-inspired blue
                    background = (Color)ColorConverter.ConvertFromString("#101A2A");
                    sidebar = (Color)ColorConverter.ConvertFromString("#111C2D");
                    panel = (Color)ColorConverter.ConvertFromString("#1B2A40");
                    line = (Color)ColorConverter.ConvertFromString("#34445B");
                    break;
                case 2: // Charcoal
                    background = (Color)ColorConverter.ConvertFromString("#101010");
                    sidebar = (Color)ColorConverter.ConvertFromString("#171717");
                    panel = (Color)ColorConverter.ConvertFromString("#202020");
                    line = (Color)ColorConverter.ConvertFromString("#3A3A3A");
                    break;
                default: // Midnight
                    background = (Color)ColorConverter.ConvertFromString("#111318");
                    sidebar = (Color)ColorConverter.ConvertFromString("#17191F");
                    panel = (Color)ColorConverter.ConvertFromString("#20232A");
                    line = (Color)ColorConverter.ConvertFromString("#343841");
                    break;
            }

            bool highContrast = launcherSettings.HighContrast;
            Background = new SolidColorBrush(background);
            SetResourceBrushColor("Sidebar", highContrast ? Colors.Black : sidebar);
            SetResourceBrushColor("Panel", highContrast ? Colors.Black : panel);
            SetResourceBrushColor("Line", highContrast ? Colors.White : line);
            SetResourceBrushColor("Muted", highContrast ? Colors.White : (Color)ColorConverter.ConvertFromString("#C0C6D0"));

            double scale = launcherSettings.TextScaleIndex switch { 1 => 1.15, 2 => 1.30, _ => 1.0 };
            foreach (KeyValuePair<FrameworkElement, double> entry in originalFontSizes)
            {
                double scaledSize = entry.Value * scale;
                if (entry.Key is Control control)
                    control.FontSize = scaledSize;
                else if (entry.Key is TextBlock textBlock)
                    textBlock.FontSize = scaledSize;
            }

            // Preserve each text element's normal colour and restore it when high contrast is disabled.
            foreach (KeyValuePair<TextBlock, Brush> entry in originalTextColors)
                entry.Key.Foreground = highContrast ? Brushes.White : entry.Value;

            Foreground = highContrast ? Brushes.White : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F3F6"));
        }

        private void SetResourceBrushColor(string key, Color color)
        {
            if (Resources[key] is SolidColorBrush brush)
                brush.Color = color;
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    launcherSettings = JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
                    if (!string.IsNullOrWhiteSpace(launcherSettings.InstallPath))
                        installPath = launcherSettings.InstallPath;
                }
            }
            catch
            {
                launcherSettings = new LauncherSettings();
            }
        }

        private void SaveSettingsToDisk()
        {
            try
            {
                launcherSettings.InstallPath = installPath;
                string? folder = Path.GetDirectoryName(settingsFilePath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(settingsFilePath, JsonSerializer.Serialize(launcherSettings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't save launcher settings:\n" + ex.Message, "RecWorks Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                MessageBox.Show("Cancel the current operation before changing the install folder.", "RecWorks Launcher");
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "Choose the game installation folder",
                InitialDirectory = Directory.Exists(installPath)
                    ? installPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };

            if (dialog.ShowDialog() == true)
            {
                installPath = dialog.FolderName;
                SettingsInstallPathText.Text = installPath;
                launcherSettings.InstallPath = installPath;
                SaveSettingsToDisk();
                RefreshGameState();
                ActivityText.Text = "Install location updated.";
            }
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
                return;

            string exe = Path.Combine(installPath, "RecRewind.exe");
            if (!File.Exists(exe))
            {
                MessageBox.Show("Install RecWorks first.", "RecWorks Launcher");
                RefreshGameState();
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = installPath,
                    Arguments = launcherSettings.LaunchArguments,
                    UseShellExecute = true
                });
                StatusText.Text = "RecWorks is running";
                ActivityText.Text = "Launched RecWorks.";
                if (launcherSettings.CloseLauncherAfterLaunch)
                    Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't launch RecWorks:\n" + ex.Message, "RecWorks Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
                return;

            // Downloads and installs the latest package at the configured source.
            // A true automatic version check requires a hosted version manifest.
            Install_Click(sender, e);
        }


        private async Task CheckForLauncherUpdateAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, LatestReleaseApi);

                request.Headers.UserAgent.ParseAdd("RecWorksLauncher/1.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var response = await httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                    return;

                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());

                if (!json.RootElement.TryGetProperty("tag_name", out var tagElement))
                    return;

                string? tag = tagElement.GetString();

                if (string.IsNullOrWhiteSpace(tag))
                    return;

                string versionText = tag.TrimStart('v', 'V');

                if (!Version.TryParse(versionText, out var latestVersion) ||
                    !Version.TryParse(CurrentLauncherVersion, out var currentVersion))
                    return;

                if (latestVersion <= currentVersion)
                    return;

                var result = MessageBox.Show(
                    $"RecWorks Launcher {latestVersion} is available.\n\n" +
                    "Would you like to open the release page?",
                    "Launcher Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result != MessageBoxResult.Yes)
                    return;

                if (!json.RootElement.TryGetProperty("html_url", out var urlElement))
                    return;

                string? releaseUrl = urlElement.GetString();

                if (Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps &&
                    uri.Host == "github.com")
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = uri.AbsoluteUri,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // An update-check failure should not stop the launcher opening.
            }
        }


        private sealed class LauncherSettings
        {
            public string InstallPath { get; set; } = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecWorks", "Game");
            public string LaunchArguments { get; set; } = "";
            public bool CloseLauncherAfterLaunch { get; set; } = false;
            public int BackgroundThemeIndex { get; set; } = 0;
            public int TextScaleIndex { get; set; } = 0;
            public bool HighContrast { get; set; } = false;
        }

    }
}
