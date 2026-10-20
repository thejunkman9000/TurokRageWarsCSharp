using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Microsoft.Xna.Framework;
using TrwWindow = Trw.Game.TrwWindow;
using TouchControls = Trw.Game.TouchControls;
using GameBoot = Trw.Game.GameBoot;
using Build = Android.OS.Build;
using View = Android.Views.View;
using Environment = System.Environment;
using Trw.Platform;

namespace Trw.Android;

/// <summary>
/// The Android head: hosts the same game as the desktop (<see cref="TrwWindow"/>) in MonoGame's Android activity. First run: the player picks the ROM with the
/// system file picker (Storage Access Framework); it is copied to the app's private storage and used from there. A ROM in the app's external files
/// folder (<c>/sdcard/Android/data/&lt;package&gt;/files/</c>, where <c>adb push</c> can put it) is taken as well. Never bundled with the APK.
/// Test switches (the desktop's TRW_* environment variables): an intent extra <c>env</c> (<c>"TRW_X=1;TRW_Y=2"</c>) or the file <c>trw_env.txt</c>
/// (one <c>NAME=value</c> per line) in the external files folder. Console output goes to logcat (tag <c>TRW</c>).
/// </summary>
[Activity(
    Name = "io.github.thejunkman9000.turokragewars.MainActivity",
    Label = "Turok: Rage Wars",
    MainLauncher = true,
    Icon = "@drawable/icon",
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    AlwaysRetainTaskState = true,
    LaunchMode = LaunchMode.SingleInstance,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Navigation | ConfigChanges.Density)]
public class MainActivity : AndroidGameActivity
{
    private const int PickRom = 4711;
    private const string RomFile = "baserom.us.v11.z64";
    private TrwWindow? game;
    private bool booting;
    private TextView? status;

    protected override void OnCreate(Bundle? bundle)
    {
        base.OnCreate(bundle);
        Logcat.Install();
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (game != null || booting) return;
        ApplyTestEnvironment();
        Console.Error.WriteLine($"[android] {Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
                                $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} cores");
        // the game's files (settings, Controller Paks, screenshots, the HUD layout) live in the app's private files folder
        var files = FilesDir!.AbsolutePath;
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", files + "/");
        ExtractAsset("Content/Hud/hud.json", Path.Combine(files, "Content", "Hud", "hud.json"));

        var rom = FindRom();
        if (rom != null) Boot(rom);
        else ShowRomPrompt(null);
    }

    // ---- ROM ----

    private string PrivateRom => Path.Combine(FilesDir!.AbsolutePath, RomFile);

    private byte[]? FindRom()
    {
        var candidates = new List<string> { PrivateRom };
        var ext = GetExternalFilesDir(null)?.AbsolutePath;
        if (ext != null && Directory.Exists(ext)) { candidates.Add(Path.Combine(ext, RomFile)); candidates.AddRange(Directory.GetFiles(ext, "*.z64").Concat(Directory.GetFiles(ext, "*.v64")).Concat(Directory.GetFiles(ext, "*.n64"))); }
        foreach (var c in candidates.Distinct())
        {
            try
            {
                if (!File.Exists(c)) continue;
                var bytes = File.ReadAllBytes(c);
                var err = Validate(ref bytes);
                if (err == null) { Console.Error.WriteLine($"[android] ROM {c}"); return bytes; }
                Console.Error.WriteLine($"[android] {c}: {err}");
            }
            catch (Exception e) { Console.Error.WriteLine($"[android] {c}: {e.Message}"); }
        }
        return null;
    }

    /// <summary>Checks the image (Turok: Rage Wars USA, 8 MB) and brings a byte-swapped dump (.v64 / .n64) into the .z64 order. Null = usable.</summary>
    private static string? Validate(ref byte[] rom)
    {
        if (rom.Length < 0x1000) return "not an N64 ROM";
        uint magic = (uint)(rom[0] << 24 | rom[1] << 16 | rom[2] << 8 | rom[3]);
        if (magic == 0x37804012) { for (int i = 0; i + 1 < rom.Length; i += 2) (rom[i], rom[i + 1]) = (rom[i + 1], rom[i]); }
        else if (magic == 0x40123780) { for (int i = 0; i + 3 < rom.Length; i += 4) { (rom[i], rom[i + 3]) = (rom[i + 3], rom[i]); (rom[i + 1], rom[i + 2]) = (rom[i + 2], rom[i + 1]); } }
        else if (magic != 0x80371240) return "not an N64 ROM";
        var title = System.Text.Encoding.ASCII.GetString(rom, 0x20, 20).Trim('\0', ' ');
        if (!title.Contains("RAGE WARS", StringComparison.OrdinalIgnoreCase)) return $"this is \"{title}\", not Turok: Rage Wars";
        if (rom[0x3E] != (byte)'E') return "this port needs the US (NTSC) ROM";
        if (rom.Length < 8 << 20) return $"too small ({rom.Length} bytes; the cartridge is 8 MB)";
        return null;
    }

    private void ShowRomPrompt(string? error)
    {
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetGravity(GravityFlags.Center);
        layout.SetBackgroundColor(global::Android.Graphics.Color.Rgb(12, 8, 4));
        layout.SetPadding(64, 64, 64, 64);
        var title = new TextView(this) { Text = "TUROK: RAGE WARS", TextSize = 34 };
        title.SetTextColor(global::Android.Graphics.Color.Rgb(255, 200, 70));
        title.Gravity = GravityFlags.Center;
        status = new TextView(this)
        {
            Text = (error != null ? error + "\n\n" : "") +
                   "This is the game's own code translated to C#; it needs your copy of the cartridge.\n" +
                   "Choose your Turok: Rage Wars ROM (USA, .z64 / .v64 / .n64, 8 MB). It is copied into the app's private storage once.",
            TextSize = 18,
        };
        status.SetTextColor(global::Android.Graphics.Color.Rgb(236, 226, 206));
        status.Gravity = GravityFlags.Center;
        status.SetPadding(0, 32, 0, 48);
        var pick = new global::Android.Widget.Button(this) { Text = "Choose ROM file", TextSize = 22 };
        pick.Click += (_, _) => OpenPicker();
        layout.AddView(title); layout.AddView(status); layout.AddView(pick);
        SetContentView(layout);
        Immersive();
    }

    private void OpenPicker()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");          // .z64 has no MIME type
        StartActivityForResult(intent, PickRom);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickRom) return;
        if (resultCode != Result.Ok || data?.Data == null) { ShowRomPrompt("No file chosen."); return; }
        if (status != null) status.Text = "Reading the ROM...";
        var uri = data.Data;
        Task.Run(() =>
        {
            try
            {
                using var input = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("cannot open the file");
                using var ms = new MemoryStream();
                input.CopyTo(ms);
                var bytes = ms.ToArray();
                var err = Validate(ref bytes);
                if (err != null) { RunOnUiThread(() => ShowRomPrompt("That file cannot be used: " + err + ".")); return; }
                File.WriteAllBytes(PrivateRom, bytes);
                Console.Error.WriteLine($"[android] ROM copied to {PrivateRom}");
                RunOnUiThread(() => Boot(bytes));
            }
            catch (Exception e) { RunOnUiThread(() => ShowRomPrompt("Reading the file failed: " + e.Message)); }
        });
    }

    // ---- the game ----

    private void Boot(byte[] rom)
    {
        booting = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Host.Rom = rom;
        GameBoot.MapMemory();
        GameBoot.LoadGuestData();
        Trw.Game.Debug.DiffTest.Install();
        TouchControls.Haptic = Vibrate;
        game = new TrwWindow();              // installs the device hooks before the game starts (as Program.cs does on the desktop)
        GameBoot.Start();
        Console.Error.WriteLine($"[android] boot {sw.ElapsedMilliseconds} ms");
        var view = (View)game.Services.GetService(typeof(View))!;
        SetContentView(view);
        view.RequestFocus();
        Immersive();
        game.Run();
    }

    private Vibrator? vibrator;
    private void Vibrate()
    {
        try
        {
            vibrator ??= (Vibrator?)GetSystemService(VibratorService);
            if (vibrator == null || !vibrator.HasVibrator) return;
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) vibrator.Vibrate(VibrationEffect.CreatePredefined(VibrationEffect.EffectTick));
            else if (OperatingSystem.IsAndroidVersionAtLeast(26)) vibrator.Vibrate(VibrationEffect.CreateOneShot(12, 80));
        }
        catch (Exception) { }
    }

    /// <summary>The system back key / gesture: START in free play (pause), B in menus. Gamepad buttons pass through to MonoGame.</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (game != null && e != null && e.KeyCode == Keycode.Back && (e.Source & InputSourceType.Gamepad) != InputSourceType.Gamepad)
        {
            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0) TouchControls.PressBack();
            return true;
        }
        return base.DispatchKeyEvent(e);
    }

    protected override void OnPause()
    {
        game?.Suspend(true);
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Immersive();
        game?.Suspend(false);
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) Immersive();
    }

    private void Immersive()
    {
        var w = Window;
        if (w?.PeekDecorView() == null) return;      // (before the first SetContentView there is no decor view to hide the bars of)
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            w.SetDecorFitsSystemWindows(false);
            var c = w.InsetsController;
            if (c != null)
            {
                c.Hide(WindowInsets.Type.SystemBars());
                c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
        else
        {
#pragma warning disable CA1422
            w.DecorView.SystemUiFlags = SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.LayoutStable |
                                        SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen;
#pragma warning restore CA1422
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && w.Attributes != null)
        {
            var a = w.Attributes; a.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges; w.Attributes = a;
        }
    }

    // ---- helpers ----

    private void ExtractAsset(string asset, string dest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var s = Assets!.Open(asset);
            using var f = File.Create(dest);
            s.CopyTo(f);
        }
        catch (Exception e) { Console.Error.WriteLine($"[android] asset {asset}: {e.Message}"); }
    }

    /// <summary>TRW_* switches for test runs: the intent extra "env" ("A=1;B=2") and trw_env.txt in the external files folder.</summary>
    private void ApplyTestEnvironment()
    {
        var pairs = new List<string>();
        try
        {
            var ext = GetExternalFilesDir(null)?.AbsolutePath;
            var file = ext == null ? null : Path.Combine(ext, "trw_env.txt");
            if (file != null && File.Exists(file)) pairs.AddRange(File.ReadAllLines(file));
        }
        catch (Exception e) { Console.Error.WriteLine($"[android] trw_env.txt: {e.Message}"); }
        if (Intent?.GetStringExtra("env") is { Length: > 0 } extra) pairs.AddRange(extra.Split(';'));
        foreach (var raw in pairs)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            Environment.SetEnvironmentVariable(line[..eq].Trim(), line[(eq + 1)..].Trim());
            Console.Error.WriteLine($"[android] env {line}");
        }
    }
}
