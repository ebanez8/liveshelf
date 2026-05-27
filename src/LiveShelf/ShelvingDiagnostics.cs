using System.Diagnostics;
using System.IO;
using System.Text;

namespace LiveShelf;

internal sealed class ShelvingAttemptLog
{
    private readonly StringBuilder _builder = new();
    private readonly string _id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

    internal void Add(string message)
    {
        _builder.Append(DateTime.UtcNow.ToString("O"));
        _builder.Append(" [");
        _builder.Append(_id);
        _builder.Append("] ");
        _builder.AppendLine(message);
    }

    internal void Flush()
    {
        var text = _builder.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Trace.Write(text);

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveShelf",
                "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "shelving.log"), text);
        }
        catch
        {
            // Tracing still carries the diagnostic if file logging is unavailable.
        }
    }

    internal static string FormatHwnd(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";

    internal static string FormatRect(NativeMethods.RECT rect) =>
        $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom} ({rect.Width}x{rect.Height})";
}
