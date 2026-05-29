namespace emMDee.Models;

/// <summary>
/// Serializable session data persisted between application restarts.
/// </summary>
public class SessionData
{
    public List<string> OpenFilePaths { get; set; } = new();
    public List<string> RecentFilePaths { get; set; } = new();
    public int ActiveTabIndex { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 900;
    public double WindowHeight { get; set; } = 600;
    public bool WindowMaximized { get; set; }
    public double ZoomFactor { get; set; } = 1.0;
    public List<double> TabScrollPositions { get; set; } = new();
}
