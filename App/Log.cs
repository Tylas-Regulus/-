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
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _file = new StreamWriter(path, append: false) { AutoFlush = true };
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
