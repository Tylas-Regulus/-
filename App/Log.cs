using System.Diagnostics;

namespace Live2DPet.App;

/// <summary>
/// Tiny logger. Writes to the console and, optionally, to a file.
/// A desktop pet ends up with no console once it ships, so everything is also
/// mirrored into a log file that survives the process.
/// </summary>
internal static class Log
{
    private static StreamWriter? _file;
    private static readonly Lock Gate = new();

    public static void AttachFile(string path)
    {
        // A log file is a convenience, never a reason to fail. The pet holds
        // this one open for its whole life, so a second start - the editor, or a
        // second pet - cannot open it at all, and the earlier version of this
        // method threw out of `Main` and put a crash dialog on somebody's screen
        // for no better reason than that they had a pet on screen already. Say so
        // and carry on with the console.
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _file = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            _file = null;
            Warn($"could not open a log file at {path} ({ex.Message}); logging to the console only");
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{stamp}] {level} {message}";

        lock (Gate)
        {
            Debug.WriteLine(line);
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }
}
