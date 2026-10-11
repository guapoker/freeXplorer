using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FreeXplorer.App;
using FreeXplorer.App.Services;
using FreeXplorer.App.ViewModels;
using FreeXplorer.App.Views;
using FreeXplorer.Fatx;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Tests;

// WPF wants one thread to own the Application and every window, so all UI tests go through this
static class UiThread
{
    static readonly Lazy<Dispatcher> dispatcher = new(Start);

    static Dispatcher Start()
    {
        Dispatcher? found = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            FreeXplorer.App.App.Quiet = true;
            var app = new FreeXplorer.App.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // after InitializeComponent, app.xaml sets its own
            found = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        return found!;
    }

    public static T Run<T>(Func<T> work) => dispatcher.Value.Invoke(work);
    public static void Run(Action work) => dispatcher.Value.Invoke(work);
}

public class UiTests : IDisposable
{
    const long Gib = 1L << 30;

    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-ui-" + Guid.NewGuid().ToString("N"));
    readonly FakePlatform platform = new();
    readonly FakeDialogs dialogs = new();

    public UiTests() => Directory.CreateDirectory(dir);

    readonly List<MainViewModel> models = new();

    MainViewModel Track(MainViewModel model)
    {
        lock (models)
            models.Add(model);
        return model;
    }

    public void Dispose()
    {
        foreach (var model in models)
            model.Dispose();
        Directory.Delete(dir, true);
    }

    string ShotPath(string name)
    {
        string folder = Environment.GetEnvironmentVariable("FREEXPLORER_SCREENSHOT_DIR") ?? dir;
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name + ".png");
    }

    string MakeImage()
    {
        string path = Path.Combine(dir, "Xbox 360 HDD.img");
        using var dev = FileBlockDevice.CreateImage(path, 8 * Gib);
        HddFormatter.Format(dev, new[] { "sysext", "sysext2", "compat", "content" }, new HddFormatOptions { ContentLabel = "Games" });
        return path;
    }

    // puts the window on screen where nobody sees it, takes a picture, saves it and closes the window again
    static RenderTargetBitmap Shot(Window window, string path, int width, int height)
    {
        window.Left = -20000;
        window.Top = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.Width = width;
        window.Height = height;
        window.Show();
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));

        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        window.Close();
        return bitmap;
    }

    static (int Width, int Height, long NonWhite) Render(Window window, string path, int width, int height)
    {
        var bitmap = Shot(window, path, width, height);
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        long nonWhite = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
                nonWhite++;
        return (w, h, nonWhite);
    }

    static Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    [Fact]
    public async Task Main_window_shows_drives_and_partitions()
    {
        platform.DriverVersion = null;
        platform.Disks.Add(new PhysicalDiskInfo(0, 953L * Gib, 512, "Samsung SSD 990 PRO 1TB", "S1", "NVMe", false, true, true));
        platform.Disks.Add(new PhysicalDiskInfo(2, 465L * Gib, 512, "Seagate ST9500325AS", "S2", "USB", true, false, false));

        var model = await Task.Run(async () =>
        {
            var vm = Track(new MainViewModel(platform, dialogs));
            await vm.InitializeAsync();
            await vm.LoadBackupAsync(MakeImage());
            return vm;
        });

        var result = UiThread.Run(() => Render(new MainWindow(model), ShotPath("main-window"), 1080, 700));

        Assert.True(result.NonWhite > 20_000, "the window rendered as a blank page");
    }

    [Fact]
    public async Task Main_window_bindings_line_up_with_the_view_model()
    {
        platform.Disks.Add(new PhysicalDiskInfo(2, 465L * Gib, 512, "Seagate ST9500325AS", "S2", "USB", true, false, false));
        var model = await Task.Run(async () =>
        {
            var vm = Track(new MainViewModel(platform, dialogs));
            await vm.InitializeAsync();
            await vm.LoadBackupAsync(MakeImage());
            return vm;
        });

        UiThread.Run(() =>
        {
            var window = ShowOffscreen(model);
            var list = (ListBox)window.FindName("SourceList");
            var grid = (DataGrid)window.FindName("PartitionGrid");
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(6, grid.Items.Count);
            Assert.Same(model.SelectedPartition, grid.SelectedItem);
            Assert.Equal(model.SelectedSource!.Title, ((SourceItem)list.SelectedItem).Title);
            window.Close();
        });
    }

    [Fact]
    public async Task Mounted_state_and_log_render()
    {
        var model = await Task.Run(async () =>
        {
            var vm = Track(new MainViewModel(platform, dialogs));
            await vm.InitializeAsync();
            await vm.LoadBackupAsync(MakeImage());
            await vm.MountCommand.ExecuteAsync();
            vm.SelectedPartition = vm.Partitions.Single(p => p.Id == "compat");
            await vm.MountCommand.ExecuteAsync();
            return vm;
        });
        UiThread.Run(() =>
        {
            var info = Render(new MainWindow(model), ShotPath("main-window-mounted"), 1180, 700);
            Assert.True(info.NonWhite > 20_000);
        });
        Assert.Equal(2, model.MountedCount);
    }

    // what the grid really paints for a row, taken from a picture of the window and not from the row's own Background
    static Color RowColour(Window window, DataGrid grid, object item)
    {
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);
        var bounds = row.TransformToAncestor(window).TransformBounds(new Rect(row.RenderSize));
        int x = (int)bounds.Right - 14, y = (int)(bounds.Top + bounds.Height / 2);

        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return PixelAt(bitmap, x, y);
    }

    static void Settle() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));

    [Fact]
    public async Task Mounted_partitions_get_a_green_row()
    {
        var model = await Task.Run(async () =>
        {
            var vm = Track(new MainViewModel(platform, dialogs));
            await vm.InitializeAsync();
            await vm.LoadBackupAsync(MakeImage());
            vm.SelectedPartition = vm.Partitions.Single(p => p.Id == "compat");
            await vm.MountCommand.ExecuteAsync();
            vm.SelectedPartition = vm.Partitions.Single(p => p.Id == "content");
            return vm;
        });

        UiThread.Run(() =>
        {
            var window = ShowOffscreen(model);
            Settle();
            var grid = (DataGrid)window.FindName("PartitionGrid");
            var compat = model.Partitions.Single(p => p.Id == "compat");
            var content = model.Partitions.Single(p => p.Id == "content");
            var sysext = model.Partitions.Single(p => p.Id == "sysext");

            // mounted and not selected: green. Selected but not mounted: the plain selection colour. The rest: white.
            Assert.Equal(Rgb(0xD3, 0xED, 0xD3), RowColour(window, grid, compat));
            Assert.Equal(Rgb(0xDE, 0xE6, 0xF2), RowColour(window, grid, content));
            Assert.Equal(Rgb(0xFF, 0xFF, 0xFF), RowColour(window, grid, sysext));

            // pick the mounted one: still green, a shade deeper so it shows which one is selected
            model.SelectedPartition = compat;
            Settle();
            Assert.Equal(Rgb(0xB4, 0xDE, 0xB4), RowColour(window, grid, compat));
            Assert.Equal(Rgb(0xFF, 0xFF, 0xFF), RowColour(window, grid, content));

            window.Close();
        });
    }

    static MainWindow ShowOffscreen(MainViewModel model)
    {
        var window = new MainWindow(model)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = 0,
            ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();

        // the window looks for drives as soon as it opens, and buttons stay off until that's done
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (model.Busy && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(10);
        }
        return window;
    }

    static string LabelOf(Button button) => ((Panel)button.Content).Children.OfType<TextBlock>().Single().Text;

    static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    [Fact]
    public async Task Driver_button_offers_install_when_missing()
    {
        platform.DriverVersion = null;
        var model = await Task.Run(() => Track(new MainViewModel(platform, dialogs)));

        UiThread.Run(() =>
        {
            var window = ShowOffscreen(model);
            var button = (Button)window.FindName("DriverButton");
            var light = (System.Windows.Shapes.Ellipse)window.FindName("DriverLight");

            Assert.True(button.IsEnabled);
            Assert.Equal("Install driver", LabelOf(button));
            Assert.Equal("Install driver", System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.Equal(Rgb(0xC4, 0x2B, 0x1C), ((SolidColorBrush)light.Fill).Color);
            Assert.Contains("isn't installed", (string)button.ToolTip);
            var shot = Render(window, ShotPath("main-window-driver-missing"), 1180, 700);
            Assert.True(shot.NonWhite > 20_000);
        });
    }

    [Fact]
    public async Task Driver_button_goes_green_once_installed()
    {
        platform.DriverVersion = "2.1.25156";
        var model = await Task.Run(() => Track(new MainViewModel(platform, dialogs)));

        UiThread.Run(() =>
        {
            var window = ShowOffscreen(model);
            var button = (Button)window.FindName("DriverButton");
            var light = (System.Windows.Shapes.Ellipse)window.FindName("DriverLight");

            Assert.False(button.IsEnabled);
            Assert.Equal("Driver installed", LabelOf(button));
            Assert.Equal("Driver installed", System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.Equal(Rgb(0x10, 0x7C, 0x10), ((SolidColorBrush)light.Fill).Color);
            Assert.Contains("2.1.25156", (string)button.ToolTip);
            var shot = Render(window, ShotPath("main-window-driver-installed"), 1180, 700);
            Assert.True(shot.NonWhite > 20_000);
        });
    }

    [Fact]
    public void Format_window_renders()
    {
        var disk = new PhysicalDiskInfo(3, 465L * Gib, 512, "Seagate ST9500325AS", "S2", "USB", true, false, false);
        var scan = DriveScanner.Scan(new MemoryBlockDevice(465L * Gib));
        var info = new FormatDialogInfo("Disk 3", disk, 465L * Gib, true, scan);

        UiThread.Run(() =>
        {
            var model = new FormatDialogViewModel(info);
            var window = new FormatWindow(model);
            var shot = Render(window, ShotPath("format-window"), 560, 860);
            Assert.True(shot.NonWhite > 5_000);
        });
    }

    [Fact]
    public void Format_window_compat_only_mode()
    {
        var disk = new PhysicalDiskInfo(3, 465L * Gib, 512, "Seagate ST9500325AS", "S2", "USB", true, false, false);
        var scan = DriveScanner.Scan(new MemoryBlockDevice(465L * Gib));
        var info = new FormatDialogInfo("Disk 3", disk, 465L * Gib, true, scan);

        UiThread.Run(() =>
        {
            var model = new FormatDialogViewModel(info) { CompatOnly = true };
            var window = new FormatWindow(model);
            window.Show();
            window.UpdateLayout();
            var list = (ItemsControl)window.FindName("PartitionList");
            var compat = (RadioButton)window.FindName("CompatModeButton");
            var full = (RadioButton)window.FindName("FullModeButton");

            Assert.Equal(Visibility.Collapsed, list.Visibility);
            Assert.True(compat.IsChecked);
            Assert.False(full.IsChecked);

            model.FullFormat = true;
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, list.Visibility);
            Assert.True(full.IsChecked);

            model.CompatOnly = true;
            var shot = Render(window, ShotPath("format-window-compat"), 560, 640);
            Assert.True(shot.NonWhite > 5_000);
        });
    }

    [Fact]
    public void Create_backup_window_renders_and_enables_with_a_good_path()
    {
        var disk = new PhysicalDiskInfo(4, 120034123776, 512, "SABRENT", "S", "USB", false, false, false);
        var info = new BackupDialogInfo(disk, "Xbox 360 drive 111.8GB 2026-10-10.img");

        UiThread.Run(() =>
        {
            var model = new BackupDialogViewModel(info, dir);
            var window = new CreateBackupWindow(model);
            var shot = Render(window, ShotPath("create-backup-window"), 560, 420);
            Assert.True(shot.NonWhite > 5_000);
            Assert.True(model.CanCreate);
        });
    }

    [Fact]
    public void Restore_window_renders()
    {
        string backup = Path.Combine(dir, "drive.img");
        File.WriteAllBytes(backup, new byte[1 << 20]);
        var disks = new[]
        {
            new PhysicalDiskInfo(0, 953L * Gib, 512, "Samsung SSD 990 PRO 1TB", "S1", "NVMe", false, true, true),
            new PhysicalDiskInfo(2, 465L * Gib, 512, "Seagate ST9500325AS", "S2", "USB", true, true, false),
            new PhysicalDiskInfo(4, 120034123776, 512, "SABRENT", "S3", "USB", false, false, false),
            new PhysicalDiskInfo(5, 512L << 10, 512, "Tiny stick", "S4", "USB", true, false, false),
        };
        var info = new RestoreDialogInfo(disks, backup, new HashSet<string>());

        UiThread.Run(() =>
        {
            var model = new RestoreDialogViewModel(info);
            var window = new RestoreWindow(model);
            window.Show();
            window.UpdateLayout();
            var list = (ListBox)window.FindName("TargetList");
            Assert.Equal(4, list.Items.Count);

            model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
            var shot = Render(window, ShotPath("restore-window"), 620, 760);
            Assert.True(shot.NonWhite > 5_000);
        });
    }

    [Fact]
    public void Restore_window_keep_option_follows_the_rules()
    {
        // a backup of a drive the console saw as 120 GB, as a sparse file so the test doesn't write 120 GB
        string backup = Path.Combine(dir, "same-size.img");
        using (var file = new FileStream(backup, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            SparseFile.TryMark(file.SafeFileHandle);
            file.SetLength(120034123776);
        }
        var disks = new[]
        {
            new PhysicalDiskInfo(4, 120034123776, 512, "SABRENT", "S3", "USB", false, false, false),
            new PhysicalDiskInfo(6, 250059350016, 512, "Bigger drive", "S5", "USB", false, false, false),
        };
        var sectors = new Dictionary<int, SecuritySector>
        {
            [4] = new("WD-WXQ1A12B3456", 120034123776),
            [6] = new("WD-WXQ9Z98Y7654", 250059350016),
        };
        var info = new RestoreDialogInfo(disks, backup, new HashSet<string>(), sectors);

        UiThread.Run(() =>
        {
            var model = new RestoreDialogViewModel(info);
            var window = new RestoreWindow(model);
            window.Show();
            window.UpdateLayout();
            var keep = (CheckBox)window.FindName("KeepBox");

            Assert.False(keep.IsEnabled);

            model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 6);
            window.UpdateLayout();
            Assert.False(keep.IsEnabled);   // the console sees that one at another size than the backup came from
            Assert.Contains("same size", model.KeepHeaderNote);

            model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
            window.UpdateLayout();
            Assert.True(keep.IsEnabled);
            keep.IsChecked = true;
            Assert.True(model.KeepTargetHeader);
            Assert.Contains("except its own security sector", model.Warning);

            var shot = Render(window, ShotPath("restore-window-keep"), 620, 820);
            Assert.True(shot.NonWhite > 5_000);
        });
    }

    [Fact]
    public async Task Main_window_has_the_backup_buttons_and_no_leftovers()
    {
        var model = await Task.Run(() => Track(new MainViewModel(platform, dialogs)));
        UiThread.Run(() =>
        {
            var window = ShowOffscreen(model);
            string[] buttons = FindButtons(window).ToArray();
            Assert.Contains("Load backup...", buttons);
            Assert.Contains("Create backup...", buttons);
            Assert.Contains("Restore backup...", buttons);
            Assert.DoesNotContain("Open image...", buttons);
            Assert.DoesNotContain("New image...", buttons);
            Assert.DoesNotContain("Mount all", buttons);
            window.Close();
        });
    }

    static IEnumerable<string> FindButtons(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Content: string text })
                yield return text;
            foreach (var found in FindButtons(child))
                yield return found;
        }
    }

    [Fact]
    public void Text_window_renders()
    {
        UiThread.Run(() =>
        {
            var shot = Render(new TextWindow("Check of Content", "Content: 12 folders, 340 files.\n\nNo problems found."),
                ShotPath("text-window"), 640, 420);
            Assert.True(shot.NonWhite > 1_000);
        });
    }

    // ---- dark mode ----

    static bool IsDark(Color c) => c.R < 0x40 && c.G < 0x40 && c.B < 0x40;
    static bool IsLight(Color c) => c.R > 0xE0 && c.G > 0xE0 && c.B > 0xE0;

    // a window with partitions mounted, so the green rows show
    async Task<MainViewModel> ModelWithMountedPartitions()
    {
        return await Task.Run(async () =>
        {
            var vm = Track(new MainViewModel(platform, dialogs));
            await vm.InitializeAsync();
            await vm.LoadBackupAsync(MakeImage());
            await vm.MountCommand.ExecuteAsync();
            vm.SelectedPartition = vm.Partitions.Single(p => p.Id == "compat");
            await vm.MountCommand.ExecuteAsync();
            return vm;
        });
    }

    [Fact]
    public async Task Dark_mode_restyles_a_window_that_is_already_open()
    {
        var model = await ModelWithMountedPartitions();

        UiThread.Run(() =>
        {
            var window = ShowOffscreen(model);
            try
            {
                Assert.Equal(Rgb(0xF2, 0xF3, 0xF5), ((SolidColorBrush)window.Background).Color);

                Theme.Apply(true);
                window.UpdateLayout();
                Assert.True(Theme.IsDark);
                Assert.Equal(Rgb(0x1B, 0x1D, 0x21), ((SolidColorBrush)window.Background).Color);
                Assert.Equal(Rgb(0xE7, 0xE9, 0xED), ((SolidColorBrush)window.Foreground).Color);

                var grid = (DataGrid)window.FindName("PartitionGrid");
                var compat = model.Partitions.Single(p => p.Id == "compat");
                var content = model.Partitions.Single(p => p.Id == "content");
                var sysext = model.Partitions.Single(p => p.Id == "sysext");
                model.SelectedPartition = sysext;
                window.UpdateLayout();
                Assert.Equal(Rgb(0x1F, 0x4B, 0x2B), RowColour(window, grid, content));
                Assert.Equal(Rgb(0x32, 0x3C, 0x4D), RowColour(window, grid, sysext));
                model.SelectedPartition = compat;
                window.UpdateLayout();
                Assert.Equal(Rgb(0x2B, 0x6A, 0x3C), RowColour(window, grid, compat));

                Theme.Apply(false);
                window.UpdateLayout();
                Assert.Equal(Rgb(0xF2, 0xF3, 0xF5), ((SolidColorBrush)window.Background).Color);
                Assert.Equal(Rgb(0xB4, 0xDE, 0xB4), RowColour(window, grid, compat));
            }
            finally
            {
                Theme.Apply(false);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Dark_switch_changes_theme_and_is_remembered()
    {
        var model = await Task.Run(() => Track(new MainViewModel(platform, dialogs)));
        string file = Path.Combine(dir, "settings.json");
        string before = Theme.SettingsFile;

        UiThread.Run(() =>
        {
            Theme.SettingsFile = file;
            try
            {
                var window = ShowOffscreen(model);
                var dark = (CheckBox)window.FindName("DarkSwitch");
                Assert.False(dark.IsChecked);
                Assert.Null(Theme.SavedChoice());

                dark.IsChecked = true;
                Assert.True(Theme.IsDark);
                Assert.True(Theme.SavedChoice());
                Assert.Contains("true", File.ReadAllText(file));

                dark.IsChecked = false;
                Assert.False(Theme.IsDark);
                Assert.False(Theme.SavedChoice());

                // a window opened while dark mode is on starts with the switch on
                Theme.Choose(true);
                var next = ShowOffscreen(model);
                Assert.True(((CheckBox)next.FindName("DarkSwitch")).IsChecked);
                next.Close();
                window.Close();
            }
            finally
            {
                Theme.Apply(false);
                Theme.SettingsFile = before;
            }
        });
    }

    [Fact]
    public void A_missing_or_damaged_settings_file_means_no_choice_yet()
    {
        string before = Theme.SettingsFile;
        try
        {
            Theme.SettingsFile = Path.Combine(dir, "nothing-here.json");
            Assert.Null(Theme.SavedChoice());

            Theme.SettingsFile = Path.Combine(dir, "broken.json");
            File.WriteAllText(Theme.SettingsFile, "{ not json");
            Assert.Null(Theme.SavedChoice());

            File.WriteAllText(Theme.SettingsFile, "{\"dark\": \"yes\"}");
            Assert.Null(Theme.SavedChoice());

            File.WriteAllText(Theme.SettingsFile, "{\"dark\": true}");
            Assert.True(Theme.SavedChoice());
        }
        finally
        {
            Theme.SettingsFile = before;
        }
    }

    [Fact]
    public async Task Every_window_is_dark_in_dark_mode()
    {
        var model = await ModelWithMountedPartitions();
        string backup = Path.Combine(dir, "drive.img");
        File.WriteAllBytes(backup, new byte[1 << 20]);
        var disks = new[]
        {
            new PhysicalDiskInfo(0, 953L * Gib, 512, "Samsung SSD 990 PRO 1TB", "S1", "NVMe", false, true, true),
            new PhysicalDiskInfo(4, 120034123776, 512, "SABRENT", "S3", "USB", false, false, false),
            new PhysicalDiskInfo(5, 512L << 10, 512, "Tiny stick", "S4", "USB", true, false, false),
        };
        var formatInfo = new FormatDialogInfo("Disk 3", disks[1], 120034123776, true, DriveScanner.Scan(new MemoryBlockDevice(8L * Gib)));

        UiThread.Run(() =>
        {
            try
            {
                Theme.Apply(true);
                var corners = new List<(string Name, Color Pixel)>
                {
                    ("main", PixelAt(Shot(new MainWindow(model), ShotPath("dark-main"), 1180, 700), 4, 4)),
                    ("text", PixelAt(Shot(new TextWindow("Check of Content", "Content: 12 folders, 340 files.\n\nNo problems found."),
                        ShotPath("dark-text"), 640, 420), 4, 4)),
                    ("backup", PixelAt(Shot(new CreateBackupWindow(new BackupDialogViewModel(
                        new BackupDialogInfo(disks[1], "Xbox 360 drive.img"), dir)), ShotPath("dark-backup"), 560, 420), 4, 4)),
                };

                var restore = new RestoreDialogViewModel(new RestoreDialogInfo(disks, backup, new HashSet<string>(),
                    new Dictionary<int, SecuritySector> { [4] = new("WD-WXQ1A12B3456", 120034123776) }));
                restore.SelectedTarget = restore.Targets.Single(t => t.Disk.Number == 4);
                restore.ConfirmText = "dis";
                corners.Add(("restore", PixelAt(Shot(new RestoreWindow(restore), ShotPath("dark-restore"), 620, 760), 4, 4)));

                var format = new FormatWindow(new FormatDialogViewModel(formatInfo) { Label = "Games" });
                ((Expander)format.FindName("AdvancedBox")).IsExpanded = true;
                corners.Add(("format", PixelAt(Shot(format, ShotPath("dark-format"), 560, 860), 4, 4)));

                var compatOnly = new FormatWindow(new FormatDialogViewModel(formatInfo) { CompatOnly = true });
                corners.Add(("format, compatibility only", PixelAt(Shot(compatOnly, ShotPath("dark-format-compat"), 560, 640), 4, 4)));

                foreach (var (name, pixel) in corners)
                    Assert.True(IsDark(pixel), $"the {name} window starts with a light corner: {pixel}");

                Theme.Apply(false);
                Assert.True(IsLight(PixelAt(Shot(new TextWindow("Light again", "Back to normal."), ShotPath("light-text"), 640, 420), 4, 4)));
            }
            finally
            {
                Theme.Apply(false);
            }
        });
    }
}
