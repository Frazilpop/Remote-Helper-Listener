namespace RemoteHelper.Listener;

/// <summary>
/// Writes to the console (visible in dev/console builds) and to a small
/// log file (the only record when running as a windowless tray app).
/// </summary>
public static class Log
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteHelper", "listener.log");

    private static readonly object Gate = new();

    public static void Line(string message)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss} {message}";
        Console.WriteLine(stamped);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // Fresh start when the log gets fat; it's a diagnostic, not an archive.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, stamped + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
