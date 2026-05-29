namespace emMDee.Models;

/// <summary>
/// Represents an open tab with its associated markdown file.
/// </summary>
public class TabItem
{
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = "Untitled";
    public string MarkdownContent { get; set; } = string.Empty;
    public bool IsModified { get; set; }
    public double ScrollPosition { get; set; }

    public static TabItem FromFile(string filePath, string? content = null)
    {
        return new TabItem
        {
            FilePath = filePath,
            Title = System.IO.Path.GetFileName(filePath),
            MarkdownContent = content ?? string.Empty
        };
    }
}
