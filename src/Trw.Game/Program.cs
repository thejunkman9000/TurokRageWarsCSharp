using Trw.Game;
using Trw.Platform;

// usage: Trw.Game [--rom baserom.us.v11.z64] [--headless --seconds N] [--netplay ...]
Trw.Game.Net.NetPlay.Configure(args);       // --netplay: before anything reads the environment (deterministic scheduler, no paks)
string romPath = "baserom.us.v11.z64";
double seconds = 10;
bool headless = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--rom") romPath = args[++i];
    else if (args[i] == "--seconds") seconds = double.Parse(args[++i]);
    else if (args[i] == "--headless") headless = true;
}
if (!File.Exists(romPath)) { Console.Error.WriteLine($"ROM not found: {romPath}"); return 2; }
Host.Rom = File.ReadAllBytes(romPath);

GameBoot.MapMemory();
GameBoot.LoadGuestData();
if (args.Contains("--selftest-math")) return GameBoot.SelfTestMath();
Trw.Game.Debug.DiffTest.Install();      // TRW_DIFF: differential tests against the ROM's machine code
if (Trw.Game.Net.NetPlay.Enabled) return Trw.Game.Net.NetPlay.Run(headless);      // lobby first: the session boots the game (docs/NETPLAY.md)
TrwWindow? window = headless ? null : new TrwWindow();      // installs the device hooks before the game starts
GameBoot.Start();

if (headless && (Oracle.Deterministic || Oracle.Enabled))
{
    // oracle run: display lists are interpreted (no GPU needed), the retrace is driven in lockstep with the game threads and as fast as the machine allows.
    // TRW_TICKS=<n> (default seconds*60) ends the run after n retraces, so the length of a run does not depend on the speed of the machine.
    int ticks = int.TryParse(Environment.GetEnvironmentVariable("TRW_TICKS"), out var tk) ? tk : (int)(seconds * 60);
    var interp = new Trw.Platform.Gfx.DisplayListInterpreter();
    Host.GraphicsTask = task => { var cmds = interp.Run(task); Oracle.Frame(cmds); TrwWindow.RecordSignature(cmds); };
    Host.ReadPad = TrwWindow.ReadPadHeadless;
    Host.ReadMouse = TrwWindow.ReadMouse;
    if (Oracle.Deterministic) Game.WaitQuiescent();       // the boot thread runs until it first waits for a retrace
    var swo = System.Diagnostics.Stopwatch.StartNew();
    for (int t = 0; t < ticks; t++)
    {
        if (Oracle.Deterministic) Game.LockstepTick(); else { Thread.Sleep(16); Game.ViTick(); Oracle.Tick(t + 1); }
        TrwWindow.scriptTick = t + 1;       // same order as TrwWindow.Update: the tick first, then the script clock
    }
    Console.Error.WriteLine($"[oracle] {ticks} ticks in {swo.Elapsed.TotalSeconds:F1} s ({ticks / swo.Elapsed.TotalSeconds:F1} ticks/s)");
    Oracle.Close();
    Environment.Exit(0);
}
else if (headless)
{
    // no window: tick the retrace like the display would
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(16);
        Game.ViTick();
    }
}
else
{
    using (window!) window!.Run();
}
return 0;

