using Trw.Walk;

// usage: Trw.Game [--data <extracted dir>] [--level N] [--size WxH] [--shot file.png --frames N] [--autowalk]
string? data = null, shot = null; int level = 0, frames = 200, w = 1280, h = 720; bool autoWalk = false, noFog = false, noSound = false, noBotAi = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data": data = args[++i]; break;
        case "--level": level = int.Parse(args[++i]); break;
        case "--shot": shot = args[++i]; break;
        case "--frames": frames = int.Parse(args[++i]); break;
        case "--autowalk": autoWalk = true; break;
        case "--nofog": noFog = true; break;
        case "--nosound": noSound = true; break;
        case "--nobots": noBotAi = true; break;
        case "--size": { var s = args[++i].Split('x'); w = int.Parse(s[0]); h = int.Parse(s[1]); break; }
    }
}
if (data == null)
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "extracted"))) { data = Path.Combine(d.FullName, "extracted"); break; }
if (data == null) { Console.Error.WriteLine("extracted/ not found: run the trw tools first or pass --data"); return 1; }

using var game = new TrwGame(new TrwGame.Options(data, level, shot, frames, autoWalk, w, h, noFog, noSound, noBotAi));
game.Run();
return 0;
