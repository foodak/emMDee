using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace emMDee;

/// <summary>
/// Application entry point for emMDee.
/// </summary>
public partial class App : Application
{
    // Single-instance plumbing. A named mutex elects the one running instance;
    // a named pipe lets later launches forward their file arguments to it.
    private const string MutexName = "emMDee.SingleInstance.Mutex";
    private const string PipeName = "emMDee.SingleInstance.Pipe";
    private static readonly char[] ArgSeparator = { '\n' };

    private Mutex? _instanceMutex;
    private MainWindow? _mainWindow;
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
        var fileArgs = GetFileArgs();

        // If another instance already holds the mutex, hand our files off to it and quit.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SendArgsToRunningInstance(fileArgs);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ApplyChromeColors(IsSystemDarkMode());
        Services.FileAssociationService.EnsureDocumentIcon();

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        foreach (var path in fileArgs)
            _mainWindow.OpenFileFromCommandLine(path);

        // Listen for file paths forwarded by subsequent launches.
        StartPipeServer();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // Command-line file paths that actually exist on disk (skips arg[0], the exe path).
    private static List<string> GetFileArgs()
    {
        var args = Environment.GetCommandLineArgs();
        var result = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (File.Exists(args[i]))
                result.Add(args[i]);
        }
        return result;
    }

    private static void SendArgsToRunningInstance(IReadOnlyList<string> fileArgs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var payload = string.Join("\n", fileArgs);
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SendArgsToRunningInstance failed: {ex.Message}");
        }
    }

    private void StartPipeServer()
    {
        var thread = new Thread(PipeServerLoop) { IsBackground = true, Name = "SingleInstancePipe" };
        thread.Start();
    }

    private void PipeServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = reader.ReadToEnd();

                var paths = payload.Split(ArgSeparator, StringSplitOptions.RemoveEmptyEntries);
                Dispatcher.Invoke(() => OnArgsForwarded(paths));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PipeServerLoop error: {ex.Message}");
            }
        }
    }

    // Runs on the UI thread: open forwarded files and surface the window.
    private void OnArgsForwarded(string[] paths)
    {
        if (_mainWindow == null) return;

        foreach (var path in paths)
        {
            if (File.Exists(path))
                _mainWindow.OpenFileFromCommandLine(path);
        }

        _mainWindow.ActivateFromAnotherInstance();
    }
}

