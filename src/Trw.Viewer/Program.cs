using Microsoft.Xna.Framework;
using Trw.Viewer;

// usage: Trw.Viewer [--data <extracted dir>] [--level N] [--pos x,y,z] [--yaw rad] [--pitch rad] [--shot file.png] [--size WxH]
string? data = null, shot = null; int level = 0, w = 1600, h = 900; Vector3? pos = null; float? yaw = null, pitch = null; int cull = 1; string flags = "", pick = "";
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data": data = args[++i]; break;
        case "--level": level = int.Parse(args[++i]); break;
        case "--shot": shot = args[++i]; break;
        case "--yaw": yaw = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--pitch": pitch = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--pos":
            var p = args[++i].Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            pos = new Vector3(p[0], p[1], p[2]); break;
        case "--pick": pick = args[++i]; break;       // x,y pixel to pick a face at, for headless checks
        case "--flags": flags = args[++i]; break;      // comma list: wire, showculled, orbit, topdown
        case "--cull": cull = int.Parse(args[++i]); break;
        case "--size":
            var s = args[++i].Split('x'); w = int.Parse(s[0]); h = int.Parse(s[1]); break;
    }
}
if (data == null)
{
    // walk up from the executable until an "extracted" folder is found
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "extracted"))) { data = Path.Combine(d.FullName, "extracted"); break; }
}
if (data == null) { Console.Error.WriteLine("extracted/ not found; run the trw converters first or pass --data"); return 1; }

using var game = new ViewerGame(new ViewerGame.Options(data, level, shot, pos, yaw, pitch, w, h, cull, flags, pick));
game.Run();
return 0;
