using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace emMDee.Views;

/// <summary>
/// Asks once before a document's link executes something.
///
/// Two outcomes only — run, or do not — plus a checkbox that records the answer
/// so the same program does not ask again. When the command is a script that
/// provably only launches another program, the dialog says which program that
/// is and the checkbox remembers THAT program instead of the script, so a
/// workflow that writes a fresh wrapper script per document asks once and then
/// never again. The default button is Run and Escape cancels, because the
/// owner clicked the link deliberately; the dialog exists so that a document
/// they did NOT write cannot execute code silently, not to argue with them
/// about a command they asked for.
/// </summary>
public partial class RunCommandDialog : Window
{
    /// <summary>True when the owner ticked "don't ask again".</summary>
    public bool Remember => RememberBox.IsChecked == true;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public RunCommandDialog(string commandPath, Services.ResolvedForwarder? forwarder)
    {
        InitializeComponent();
        PathText.Text = commandPath;

        var targets = forwarder?.InvokedExecutables
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        // When the script is a proven forwarder, name the program it launches:
        // that is the thing running when the owner clicks Run, and it is what
        // "don't ask again" remembers.
        if (targets.Count > 0)
        {
            LaunchText.Text = targets.Count == 1
                ? $"This command just launches: {targets[0]}"
                : "This command only launches:" + Environment.NewLine
                    + string.Join(Environment.NewLine, targets);
            LaunchText.Visibility = Visibility.Visible;
        }

        if (targets.Count == 1)
        {
            RememberText.Text = "Don't ask again for that program";
        }
        else
        {
            // Name the folder the tick would approve. A review request holds
            // several commands that all launch the same program, so approving
            // the folder is one decision instead of one per link - but it also
            // covers commands added to that folder later, and the reader should
            // be told which folder that is rather than left to infer it.
            var folder = Services.TrustedCommandService.FolderOf(commandPath);
            RememberText.Text = folder is null
                ? "Don't ask again for this file"
                : $"Don't ask again for commands in {DescribeFolder(folder)}";
        }

        // Stated in code as well as in the XAML: the remembered-trust box starts
        // CLEAR. A prompt that arrives pre-ticked turns "ask once" into "never ask
        // again" for anyone who hits Enter, which is the opposite of its purpose.
        RememberBox.IsChecked = false;

        // Centring on a maximized owner can push half the dialog past the edge of
        // the display. Nudge it back inside the working area once it has a size.
        Loaded += (_, _) => ClampToWorkingArea();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private const int MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// Keeps the whole dialog on the display it opened on.
    ///
    /// CenterOwner centres on the OWNER, and an owner maximized against the edge
    /// of a multi-monitor desktop can put half of this window past that edge -
    /// including the buttons. Uses the monitor the window actually landed on, not
    /// the primary one, because those are routinely different here.
    /// </summary>
    private void ClampToWorkingArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST), ref info))
            return;

        // The work area is device pixels; the window's Left/Top are DIPs.
        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double left = info.rcWork.Left / scaleX, top = info.rcWork.Top / scaleY;
        double right = info.rcWork.Right / scaleX, bottom = info.rcWork.Bottom / scaleY;

        if (Left + ActualWidth > right) Left = right - ActualWidth;
        if (Top + ActualHeight > bottom) Top = bottom - ActualHeight;
        if (Left < left) Left = left;
        if (Top < top) Top = top;
    }

    /// <summary>
    /// Names the folder in a way that identifies it.
    ///
    /// A command folder's own name is routinely generic - a review request keeps
    /// its scripts in one called `commands`, which produced the label "commands
    /// in commands". Qualify it with its parent so the reader can tell WHICH set
    /// of commands they are about to approve.
    /// </summary>
    private static string DescribeFolder(string folder)
    {
        var leaf = System.IO.Path.GetFileName(folder);
        var parent = System.IO.Path.GetFileName(
            System.IO.Path.GetDirectoryName(folder) ?? string.Empty);
        return string.IsNullOrEmpty(parent) ? folder : $"{parent}\\{leaf}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // The body follows the app's theme brushes, but the title bar is drawn by
        // the window manager and stays light unless it is told otherwise - a dark
        // dialog under a white caption looks like a different application's prompt,
        // which is the last thing a "do you want to run this" dialog should look like.
        try
        {
            int dark = App.IsSystemDarkMode() ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                                  DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Shows the prompt for <paramref name="commandPath"/> unless it is already
    /// trusted, and records the approval when asked to. Returns true when the
    /// caller may launch it.
    ///
    /// "Don't ask again" records the PROGRAM the command launches when the
    /// command is a proven wrapper, and falls back to the folder (or the one
    /// file) it always recorded otherwise.
    /// </summary>
    public static bool Confirm(Window? owner, Services.TrustedCommandService trust, string commandPath)
    {
        if (!Services.TrustedCommandService.IsExecutable(commandPath))
            return true;
        if (trust.IsTrusted(commandPath))
            return true;

        var forwarder = Services.CommandScriptResolver.TryResolve(commandPath);
        var targets = forwarder?.InvokedExecutables
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        var dialog = new RunCommandDialog(commandPath, forwarder);
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }
        else
        {
            // Without an owner, CenterOwner degrades to the top-left of the last
            // window position rather than centring, and the prompt can open half
            // off the screen it belongs to.
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var approved = dialog.ShowDialog() == true;
        if (approved && dialog.Remember)
        {
            if (targets.Count == 1)
            {
                // Approve the program the wrapper launches, not the wrapper: a
                // review workflow writes a new wrapper per document, but the
                // program behind it is the same one every time. The approval
                // also records the wrapper's environment variables, so a later
                // wrapper cannot smuggle new ones past this decision.
                trust.Trust(targets[0], forwarder!.EnvVarsSet);
            }
            else if (Services.TrustedCommandService.FolderOf(commandPath) is not null)
            {
                // Folder scope, because that is what the checkbox promised.
                // Falls back to the single file when the path has no readable
                // folder.
                trust.TrustFolder(commandPath);
            }
            else
            {
                trust.Trust(commandPath);
            }
        }
        return approved;
    }
}
