using System.IO;

namespace LiveShelf;

internal static class CrashLogger
{
    private static readonly object SyncRoot = new();

    public static void Log(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveShelf");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "crash.log");
            var message = $"""
                [{DateTimeOffset.Now:O}]
                {exception}

                """;

            lock (SyncRoot)
            {
                File.AppendAllText(path, message);
            }
        }
        catch
        {
            // Logging must never become the reason the app exits.
        }
    }
}
