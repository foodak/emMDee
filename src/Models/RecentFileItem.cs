namespace emMDee.Models;

/// <summary>
/// Represents a recently opened file shown in the File → Open Recent menu.
/// </summary>
public class RecentFileItem
{
    public string FilePath { get; init; } = string.Empty;
    public string DisplayName => System.IO.Path.GetFileName(FilePath);

    /// <summary>Full path shown as a tooltip or secondary text.</summary>
    public string DisplayPath => FilePath;

    public RecentFileItem(string filePath)
    {
        FilePath = filePath;
    }
}
