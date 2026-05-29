using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace emMDee;

/// <summary>
/// Application entry point for emMDee.
/// </summary>
public partial class App : Application
{
    internal static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return false; }
    }

    internal static void ApplyChromeColors(bool isDark)
    {
        var res = Current.Resources;
        if (isDark)
        {
            res["ChromeBackground"]      = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            res["ChromeBorderBrush"]     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            res["TabBackground"]         = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            res["SearchBarBackground"]   = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            res["ChromeForeground"]      = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            res["ContentBackground"]     = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            res["ScrollTrackBrush"]      = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            res["ScrollThumbBrush"]      = new SolidColorBrush(Color.FromRgb(0x68, 0x68, 0x68));
            res["ScrollThumbHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));

        }
        else
        {
            res["ChromeBackground"]      = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            res["ChromeBorderBrush"]     = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            res["TabBackground"]         = new SolidColorBrush(Colors.White);
            res["SearchBarBackground"]   = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            res["ChromeForeground"]      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            res["ContentBackground"]     = new SolidColorBrush(Colors.White);
            res["ScrollTrackBrush"]      = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            res["ScrollThumbBrush"]      = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
            res["ScrollThumbHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));

        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyChromeColors(IsSystemDarkMode());

        // Handle command-line arguments (open files passed via command line)
        var mainWindow = new MainWindow();
        mainWindow.Show();

        // Process any file paths passed as arguments
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            var path = args[i];
            if (System.IO.File.Exists(path))
            {
                mainWindow.OpenFileFromCommandLine(path);
            }
        }
    }
}

