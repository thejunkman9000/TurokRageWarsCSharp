using System.Text.Json;

namespace Trw.Game.Net;

/// <summary>
/// Netplay settings: command line &gt; environment &gt; settings file (<c>%LOCALAPPDATA%\TurokRageWars\netplay.json</c>, or the file named by
/// <c>TRW_NET_SETTINGS</c>) &gt; defaults. The settings file remembers the master URL and the last username (never a password).
/// The automation switches (<c>--net-user</c>, <c>--net-host</c>, <c>--net-join</c> ...) drive the lobby without clicks, for tests and scripts.
/// </summary>
public sealed class NetConfig
{
    public const string DefaultMaster = "https://poly64.duckdns.org";

    public string MasterUrl = DefaultMaster;
    public int InputDelay = 3;
    public int HashEvery = 1;
    public int Port = 7810;
    public string LastUser = "";

    // automation (all optional)
    public string? User, Password;
    public bool Register;
    public string? HostName;              // --net-host "<lobby name>": host a game
    public int MaxPlayers = 4;
    public string? LobbyPassword;
    public int AutoStart;                 // --net-start <n>: the host starts once n players are in and ready
    public string? JoinName;              // --net-join "<lobby name>": join the first listed game with that name
    public bool AutoReady;                // --net-ready: a joiner says ready at once
    public string? Direct;                // --net-direct host:port: join without the master
    public bool Listen;                   // --net-listen: host without the master (needs --net-name)
    public string Name = "";              // display name for --net-direct / --net-listen
    public bool ForceRelay;               // --net-relay: a joiner skips the direct routes (to test the relay)
    public int ExitAfterTicks;            // --net-ticks <n>: leave and exit after n ticks
    public string? DigestLog;             // --net-digest-log <file>: every digest, one line per tick
    public bool Fast;                     // --net-fast (headless): do not pace at 60 Hz
    public double ExitAfterSeconds;

    public string SettingsPath = Environment.GetEnvironmentVariable("TRW_NET_SETTINGS") is { Length: > 0 } sp ? sp
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurokRageWars", "netplay.json");

    private sealed class FileForm { public string? MasterUrl { get; set; } public string? LastUser { get; set; } public int? InputDelay { get; set; } public int? Port { get; set; } }

    public static NetConfig Load(string[] args)
    {
        var c = new NetConfig();
        try
        {
            if (File.Exists(c.SettingsPath) && JsonSerializer.Deserialize<FileForm>(File.ReadAllText(c.SettingsPath)) is { } f)
            {
                if (!string.IsNullOrWhiteSpace(f.MasterUrl)) c.MasterUrl = f.MasterUrl!;
                c.LastUser = f.LastUser ?? "";
                if (f.InputDelay is int d) c.InputDelay = d;
                if (f.Port is int p) c.Port = p;
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"[net] ignoring {c.SettingsPath}: {e.Message}"); }

        string? Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;
        if (Env("TRW_MASTER") is { } m) c.MasterUrl = m;
        if (int.TryParse(Env("TRW_NET_DELAY"), out var dl)) c.InputDelay = dl;
        if (int.TryParse(Env("TRW_NET_HASH_EVERY"), out var he)) c.HashEvery = he;
        if (int.TryParse(Env("TRW_NET_PORT"), out var pt)) c.Port = pt;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--master": c.MasterUrl = Next(); break;
                case "--net-delay": c.InputDelay = int.Parse(Next()); break;
                case "--net-hash-every": c.HashEvery = int.Parse(Next()); break;
                case "--net-port": c.Port = int.Parse(Next()); break;
                case "--net-user": c.User = Next(); break;
                case "--net-pass": c.Password = Next(); break;
                case "--net-register": c.Register = true; break;
                case "--net-host": c.HostName = Next(); break;
                case "--net-max": c.MaxPlayers = int.Parse(Next()); break;
                case "--net-password": c.LobbyPassword = Next(); break;
                case "--net-start": c.AutoStart = int.Parse(Next()); break;
                case "--net-join": c.JoinName = Next(); break;
                case "--net-ready": c.AutoReady = true; break;
                case "--net-direct": c.Direct = Next(); break;
                case "--net-listen": c.Listen = true; break;
                case "--net-name": c.Name = Next(); break;
                case "--net-relay": c.ForceRelay = true; break;
                case "--net-ticks": c.ExitAfterTicks = int.Parse(Next()); break;
                case "--net-digest-log": c.DigestLog = Next(); break;
                case "--net-fast": c.Fast = true; break;
                case "--seconds": c.ExitAfterSeconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
        if (Env("TRW_NET_PASS") is { } pw && c.Password == null) c.Password = pw;      // keeps a test password off the command line
        c.InputDelay = Math.Clamp(c.InputDelay, 1, 30);
        c.HashEvery = Math.Clamp(c.HashEvery, 1, 3600);
        c.MaxPlayers = Math.Clamp(c.MaxPlayers, 2, 4);
        return c;
    }

    /// <summary>Remembers the master URL and the username (not the password).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new FileForm { MasterUrl = MasterUrl, LastUser = LastUser, InputDelay = InputDelay, Port = Port },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Console.Error.WriteLine($"[net] cannot save {SettingsPath}: {e.Message}"); }
    }
}
