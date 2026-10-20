using System.Text;

namespace Trw.Android;

/// <summary>Console.Out / Console.Error to logcat (tag TRW), line by line: the game logs through the console on the desktop.</summary>
internal sealed class Logcat : TextWriter
{
    private readonly StringBuilder line = new();
    private readonly bool error;
    private static bool installed;

    private Logcat(bool error) { this.error = error; }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Console.SetOut(new Logcat(false));
        Console.SetError(new Logcat(true));
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (line)
        {
            if (value == '\n') Flush();
            else if (value != '\r') line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        lock (line) foreach (var c in value) Write(c);
    }

    public override void WriteLine(string? value)
    {
        lock (line) { Write(value); Flush(); }
    }

    public override void Flush()
    {
        lock (line)
        {
            if (line.Length == 0) return;
            var s = line.ToString();
            line.Clear();
            if (error && (s.Contains("exception", StringComparison.OrdinalIgnoreCase) || s.Contains("failed", StringComparison.OrdinalIgnoreCase)))
                global::Android.Util.Log.Warn("TRW", s);
            else global::Android.Util.Log.Info("TRW", s);
        }
    }
}
