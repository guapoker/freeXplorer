using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FreeXplorer.App.Services;

// Light and dark mode. The colours are brushes in Themes\Light.xaml and Themes\Dark.xaml and the styles
// look them up with DynamicResource, so swapping the dictionary restyles every window that's open.
public static class Theme
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static bool IsDark { get; private set; }

    // the choice is kept here, the tests point it somewhere else
    public static string SettingsFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "freeXplorer", "settings.json");

    // Called once when the program starts. Uses what was picked last time, or what Windows is set to.
    public static void Start()
    {
        Apply(SavedChoice() ?? WindowsPrefersDark());
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((window, _) => PaintTitleBar((Window)window)));
    }

    // What the switch in the toolbar calls
    public static void Choose(bool dark)
    {
        Apply(dark);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { dark }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // it just won't be remembered
        }
    }

    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var colours = new ResourceDictionary
        {
            Source = new Uri($"/freeXplorer;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = colours;
        else
            merged.Insert(0, colours);

        IsDark = dark;
        foreach (Window window in app.Windows)
            PaintTitleBar(window);
    }

    public static bool? SavedChoice()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsFile));
            if (json.RootElement.TryGetProperty("dark", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return null;
    }

    static bool WindowsPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    // The title bar belongs to Windows, so it has to be asked to follow along. Older versions of Windows
    // ignore the colours and the ones that don't know about dark title bars ignore all of it.
    static void PaintTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        int dark = IsDark ? 1 : 0;
        int background = IsDark ? 0x282320 : unchecked((int)0xFFFFFFFF);   // BGR, the same as BarBackground
        int text = IsDark ? 0xEDE9E7 : unchecked((int)0xFFFFFFFF);         // 0xFFFFFFFF means the default
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref background, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }
}
