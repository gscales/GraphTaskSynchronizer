using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphTaskSynchronizer;

/// <summary>
/// A task's notes: the typed todoTask <c>body</c> (an itemBody), kept as plain text.
///
/// To Do returns the body as text by default (contentType "text", line breaks as CRLF), from a
/// GET, a listing and a delta query alike, and that is what the synchronizer asks for: no
/// Prefer: outlook.body-content-type header, so all three read paths agree. Should a body
/// arrive as HTML anyway it is converted to the same plain text, so the same notes always
/// compare equal however they were read.
/// </summary>
public static class TaskNotes
{
    /// <summary>Separator between lines when notes are shown on one line.</summary>
    public const string LineSeparator = " · ";

    private static readonly Regex Dropped = new(
        @"<!--.*?-->|<(head|style|script|title)\b[^>]*>.*?</\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tag = new(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex SourceWhitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "ul", "ol", "tr", "table", "blockquote", "pre", "hr",
        "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer"
    };

    /// <summary>
    /// The notes in an itemBody: line endings as "\n", trailing spaces and surrounding blank
    /// lines removed; "" when the task has none.
    /// </summary>
    public static string FromBody(string? content, string? contentType)
    {
        if (string.IsNullOrEmpty(content))
            return "";

        return string.Equals(contentType?.Trim(), "html", StringComparison.OrdinalIgnoreCase)
            ? HtmlToText(content)
            : Normalize(content);
    }

    internal static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join('\n', lines.Select(l => l.TrimEnd())).Trim();
    }

    /// <summary>
    /// Plain text from an HTML body: blocks and &lt;br&gt; become line breaks (Outlook's
    /// &lt;div&gt;&lt;br&gt;&lt;/div&gt; and Word's &lt;p&gt;&amp;nbsp;&lt;/p&gt; an empty line),
    /// list items start with "• ", other tags are dropped and entities decoded.
    /// </summary>
    internal static string HtmlToText(string html)
    {
        var source = Dropped.Replace(html, "");
        var text = new StringBuilder(source.Length);
        var position = 0;

        foreach (Match tag in Tag.Matches(source))
        {
            AppendText(text, source[position..tag.Index]);
            position = tag.Index + tag.Length;

            var name = tag.Groups[2].Value;
            var closing = tag.Groups[1].Length > 0;

            if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                text.Append('\n');
            }
            else if (BlockTags.Contains(name))
            {
                EndLine(text);
                if (!closing && name.Equals("li", StringComparison.OrdinalIgnoreCase))
                    text.Append("• ");
            }
            else if (closing && (name.Equals("td", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("th", StringComparison.OrdinalIgnoreCase)))
            {
                text.Append(' ');
            }
        }

        AppendText(text, source[position..]);
        return Normalize(text.ToString());
    }

    private static void EndLine(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != '\n')
            text.Append('\n');
    }

    private static void AppendText(StringBuilder text, string raw)
    {
        if (raw.Length == 0)
            return;

        // Whitespace in HTML source, line breaks included, is one space — and none at the start
        // of a line. &nbsp; is decoded after that, so a paragraph of just &nbsp; still counts as
        // an (empty) line.
        var collapsed = SourceWhitespace.Replace(raw, " ");
        if (text.Length == 0 || text[^1] == '\n')
            collapsed = collapsed.TrimStart(' ');

        text.Append(WebUtility.HtmlDecode(collapsed).Replace(' ', ' '));
    }

    // ──────────────────────────────────────────────────────────
    //  Showing notes and what changed in them
    // ──────────────────────────────────────────────────────────

    public static string[] Lines(string? notes) => string.IsNullOrEmpty(notes) ? [] : notes.Split('\n');

    /// <summary>The notes on one line: lines joined with " · ", blank ones left out.</summary>
    public static string OneLine(string? notes) =>
        string.Join(LineSeparator, Lines(notes).Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>"added", "removed" or "edited".</summary>
    public static string Describe(string before, string after) =>
        before.Length == 0 ? "added" : after.Length == 0 ? "removed" : "edited";

    /// <summary>
    /// Two versions of a text shortened to <paramref name="width"/>, both starting a little
    /// before the first character that differs — so the change is visible even when it is
    /// near the end of a long note.
    /// </summary>
    public static (string Before, string After) ChangeWindow(string before, string after, int width)
    {
        if (before.Length <= width && after.Length <= width)
            return (before, after);

        var prefix = 0;
        var shorter = Math.Min(before.Length, after.Length);
        while (prefix < shorter && before[prefix] == after[prefix])
            prefix++;

        var start = Math.Max(0, prefix - width / 3);
        return (Window(before, start, width), Window(after, start, width));
    }

    private static string Window(string text, int start, int width)
    {
        if (text.Length <= width && start == 0)
            return text;

        var lead = start > 0 ? "…" : "";
        var rest = text[Math.Min(start, text.Length)..];
        var room = Math.Max(1, width - lead.Length);
        return rest.Length <= room ? lead + rest : lead + rest[..(room - 1)] + "…";
    }

    public enum LineChange
    {
        Removed,
        Added
    }

    /// <summary>
    /// The lines removed from and added to the notes, in order — a line diff (longest common
    /// subsequence) without the unchanged lines. Very long notes fall back to "all of the old
    /// lines removed, all of the new ones added".
    /// </summary>
    public static IReadOnlyList<(LineChange Change, string Line)> DiffLines(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);

        // Lines both versions start and end with don't need the table.
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
            head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)])
            tail++;

        var oldLines = a[head..^tail];
        var newLines = b[head..^tail];
        var result = new List<(LineChange, string)>();

        if ((long)oldLines.Length * newLines.Length > 250_000)
        {
            result.AddRange(oldLines.Select(l => (LineChange.Removed, l)));
            result.AddRange(newLines.Select(l => (LineChange.Added, l)));
            return result;
        }

        // lcs[i, j] = length of the longest common subsequence of oldLines[i..] and newLines[j..].
        var lcs = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var i = oldLines.Length - 1; i >= 0; i--)
        for (var j = newLines.Length - 1; j >= 0; j--)
        {
            lcs[i, j] = oldLines[i] == newLines[j]
                ? lcs[i + 1, j + 1] + 1
                : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        }

        int x = 0, y = 0;
        while (x < oldLines.Length || y < newLines.Length)
        {
            if (x < oldLines.Length && y < newLines.Length && oldLines[x] == newLines[y])
            {
                x++;
                y++;
            }
            else if (y == newLines.Length || (x < oldLines.Length && lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                result.Add((LineChange.Removed, oldLines[x++]));
            }
            else
            {
                result.Add((LineChange.Added, newLines[y++]));
            }
        }

        return result;
    }
}
