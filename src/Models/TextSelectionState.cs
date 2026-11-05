namespace emMDee.Models;

/// <summary>
/// A text selection in the preview, stored as character offsets into the rendered
/// text of the document so it can be put back after a re-render (tab switch) or a
/// restart. <see cref="Text"/> holds the start of the selected text and is checked
/// on restore, so a selection whose file has changed underneath it is dropped
/// rather than landing on the wrong words.
/// </summary>
public class TextSelectionState
{
    public int Start { get; set; }
    public int End { get; set; }
    public string Text { get; set; } = string.Empty;
}
