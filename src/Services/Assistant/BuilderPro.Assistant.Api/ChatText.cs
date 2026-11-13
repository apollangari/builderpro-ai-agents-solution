using System.Text.RegularExpressions;

namespace BuilderPro.Assistant.Api;

/// <summary>
/// Makes a model reply fit a plain-text chat bubble. Models add Markdown even when told not to
/// (seen live with free models), so this is enforced in code: bold/italic markers and heading
/// hashes are removed, Windows line breaks normalised, and runs of blank lines collapsed.
/// "- " bullet lists are kept. Money text is untouched, so the price guard sees the same amounts.
/// </summary>
public static partial class ChatText
{
    public static string Clean(string? text)
    {
        var s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        s = Emphasis().Replace(s, "");
        s = Headings().Replace(s, "");
        s = TrailingSpaces().Replace(s, "\n");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"\*\*|__")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?m)^#{1,6}\s+")]
    private static partial Regex Headings();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
