using System.Text.Json;

namespace Trw.Game;

/// <summary>
/// The player's own settings, kept between sessions in <c>%LOCALAPPDATA%\TurokRageWars\settings.json</c> (per machine: nothing here reaches guest RAM, so netplay
/// peers can differ). Mouse: <see cref="MouseSensitivity"/> multiplies the 0.05 degrees per count of the native look (Os/MouseLook.cs) and of the stick
/// emulation; <c>-</c> / <c>=</c> change it in the window. <c>TRW_MOUSE_SENS</c> overrides the saved value for one run.
/// </summary>
public sealed class UserSettings
{
    public float MouseSensitivity { get; set; } = 2f;          // 2 x 0.05 = 0.1 degrees per count: 180 degrees in about 2.3 inches at 800 dpi
    public bool MouseInvertY { get; set; }

    public static readonly UserSettings Current = Load();

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurokRageWars", "settings.json");

    private static UserSettings Load()
    {
        var s = new UserSettings();
        try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? s; }
        catch (Exception e) { Console.Error.WriteLine($"[settings] {FilePath}: {e.Message} (defaults used)"); }
        if (float.TryParse(Environment.GetEnvironmentVariable("TRW_MOUSE_SENS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms > 0) s.MouseSensitivity = ms;
        s.MouseSensitivity = Math.Clamp(s.MouseSensitivity, 0.05f, 50f);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Console.Error.WriteLine($"[settings] cannot save {FilePath}: {e.Message}"); }
    }
}
