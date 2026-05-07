using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace LiveShelf;

internal sealed record WindowContentSnapshot(string Text, int Hash);

internal static class WindowContentProbe
{
    private const int MaxNodes = 80;
    private const int MaxTextLength = 20000;
    private const int SnapshotTailLength = 6000;
    private static readonly Regex VolatileGlyphs = new("[\\u2588\\u258C\\u2590\\u2800-\\u28FF]", RegexOptions.Compiled);
    private static readonly Regex RepeatedSpaces = new("[ \\t]+", RegexOptions.Compiled);

    public static WindowContentSnapshot? TryCapture(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
            {
                return null;
            }

            var builder = new StringBuilder(MaxTextLength);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);

            var visited = 0;
            while (queue.Count > 0 && visited++ < MaxNodes && builder.Length < MaxTextLength)
            {
                var element = queue.Dequeue();
                AppendElementContent(element, builder, seen);

                AutomationElementCollection children;
                try
                {
                    children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                foreach (AutomationElement child in children)
                {
                    if (queue.Count + visited >= MaxNodes)
                    {
                        break;
                    }

                    queue.Enqueue(child);
                }
            }

            var text = Normalize(builder.ToString());
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (text.Length > SnapshotTailLength)
            {
                text = text[^SnapshotTailLength..];
            }

            var stableText = NormalizeForHash(text);
            return new WindowContentSnapshot(text, StringComparer.Ordinal.GetHashCode(stableText));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static void AppendElementContent(AutomationElement element, StringBuilder builder, HashSet<string> seen)
    {
        try
        {
            if (TryGetTextPatternText(element, builder, seen))
            {
                return;
            }

            if (TryGetValuePatternText(element, builder, seen))
            {
                return;
            }

            TryAppend(element.Current.Name, builder, seen);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return;
        }
    }

    private static bool TryGetTextPatternText(AutomationElement element, StringBuilder builder, HashSet<string> seen)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern textPattern)
        {
            return false;
        }

        var remaining = Math.Max(256, MaxTextLength - builder.Length);
        var text = textPattern.DocumentRange.GetText(-1);
        if (text.Length > remaining)
        {
            text = text[^remaining..];
        }

        return TryAppend(text, builder, seen);
    }

    private static bool TryGetValuePatternText(AutomationElement element, StringBuilder builder, HashSet<string> seen)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern valuePattern)
        {
            return false;
        }

        return TryAppend(valuePattern.Current.Value, builder, seen);
    }

    private static bool TryAppend(string? value, StringBuilder builder, HashSet<string> seen)
    {
        var normalized = Normalize(value ?? string.Empty);
        if (normalized.Length < 2 || !seen.Add(normalized))
        {
            return false;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(normalized);
        return true;
    }

    private static string Normalize(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string NormalizeForHash(string value)
    {
        var text = VolatileGlyphs.Replace(value, string.Empty);
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => RepeatedSpaces.Replace(line, " ").TrimEnd());

        return string.Join('\n', lines).Trim();
    }
}
