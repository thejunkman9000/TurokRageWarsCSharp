using Trw.Game;
using Trw.Platform;

// usage: Trw.Game [--rom baserom.us.v11.z64] [--headless --seconds N]
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
TrwWindow? window = headless ? null : new TrwWindow();      // installs the device hooks before the game starts
GameBoot.Start();

if (headless)
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

