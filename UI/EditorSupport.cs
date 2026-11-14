using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

/// <summary>Colors the text of an editor from the segments of its document.</summary>
internal sealed class SegmentColorizer(List<Segment> segments) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        int lineStart = line.Offset;
        int lineEnd = line.EndOffset;

        // First segment that ends after the start of the line (segments are sorted and do not overlap).
        int low = 0;
        int high = segments.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (segments[middle].End <= lineStart)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (int i = low; i < segments.Count && segments[i].Start < lineEnd; i++)
        {
            int start = Math.Max(segments[i].Start, lineStart);
            int end = Math.Min(segments[i].End, lineEnd);
            if (start >= end)
            {
                continue;
            }

            // Looked up on every draw: the palette changes with the theme.
            var brush = AppTheme.For(segments[i].Kind);
            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}

/// <summary>Tooltips of the find bar, in the language of the application.</summary>
internal sealed class FindBarTexts : Localization
{
    public override string MatchCaseText => Lang.Tr("Match case");
    public override string MatchWholeWordsText => Lang.Tr("Match whole word");
    public override string UseRegexText => Lang.Tr("Use regular expression");
    public override string FindNextText => Lang.Tr("Find next (F3)");
    public override string FindPreviousText => Lang.Tr("Find previous (Shift+F3)");
    public override string NoMatchesFoundText => Lang.Tr("No matches found");
    public override string ErrorText => Lang.Tr("Invalid regular expression: ");
}
