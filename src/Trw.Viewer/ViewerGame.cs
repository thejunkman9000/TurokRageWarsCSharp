using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Trw.Viewer;

/// <summary>Level viewer. Controls (also summarised in the window title):
///   Camera   right mouse = look, left mouse = orbit (orbit mode), wheel = zoom, arrows = rotate, WASD move, Q/E down/up, Shift fast,
///            O fly/orbit, R reset, T top-down view
///   Display  B cycle back-face culling (off / cull clockwise / cull counter-clockwise), K show the faces culled by the current mode in red,
///            X wireframe, C vertex colours, F point/linear filtering, [ ] cycle through materials one at a time (solo), \ show all
///   Rotation Tab chooses yaw-only or quaternion records (they can need different settings); 1/2/3 turn each model about X/Y/Z before placement, 4/5/6 turn each placed piece, Shift = reverse, 7 invert placement rotation, J X<->Z-swapped placement rotation (the quaternion default),
///            8 Z-up conversion on/off, 9 mirror X, M / Y / Z final-frame mirror X / Y / Z (for the record type being edited), 0 reset, G winding mode (none / reverse flag-1 parts / opposite), I per-object assembly / baked world.obj, F9 save the settings to rotation_settings.txt
///   Faces    P or middle click = pick a face (info -> selection.txt, piece outlined yellow, face magenta), H hide the piece, V flip its winding,
///            L mirror it in X, N turn it 90 degrees (Shift = -90), U undo all manual fixes on the level; fixes persist in placement_overrides.txt
///   World    F1 / F2 mirror the whole assembled level (positions too) on world X / Z
///   Light    C game vertex lighting on/off, F11 distance fog + zone clear colour on/off
///   Actors   F10 on/off, F4 pickups, F8 bots at the deathmatch spawns, F7 next weapon, F5 turn all actors 90 degrees, F6 flip the yaw sign
///   Creature P / middle click picks a face (its texture is outlined), V flips that texture's T axis (saved to creature_texture_flips.txt)
///   Creature F3 toggles creature mode (extracted/creatures): PgUp/PgDn creature, , . clip, Space pause, Home restart
///   Other    PgUp/PgDn level, F12 screenshot, Esc quit</summary>
public sealed class ViewerGame : Game
{
    public sealed record Options(string DataRoot, int Level, string? Shot, Vector3? Pos, float? Yaw, float? Pitch, int Width, int Height, int Cull, string Flags = "", string Pick = "");

    static readonly string[] CullNames = ["culling off", "cull clockwise", "cull counter-clockwise"];

    readonly GraphicsDeviceManager _gfx;
    readonly string[] _levelDirs;
    readonly Dictionary<string, Texture2D?> _textures = new();
    readonly Options _opt;
    readonly RasterizerState[,] _raster = new RasterizerState[3, 2];     // [cull mode, wireframe]
    readonly SamplerState[,] _samplers = new SamplerState[2, 3];         // [point/linear, wrap/mirror/clamp]
    BasicEffect _effect = null!;
    AlphaTestEffect _cutout = null!;           // opaque parts: texels with alpha below 0.5 are not written (the game's alpha test)
    double _time;                              // seconds, drives texture animation
    static readonly int[] TexRates = [10, 15, 20, 30, 60];
    int _texRate = 2;                          // index into TexRates: the game's texture tick is the rendered-frame counter, whose real rate (20? 30?) is not yet known; - and = change it
    int _texHz => TexRates[_texRate];
    Texture2D? _white;
    ObjScene? _scene;
    int _level;
    Vector3 _cam, _pivot; float _yaw, _pitch, _dist = 1500f;
    bool _orbit, _wire, _showCulled;
    bool _point = true;                       // point filtering by default: the N64 look (F toggles linear)
    int _cull, _solo = -1;
    MouseState _prevMouse; KeyboardState _prevKeys;
    int _frames;
    // Defaults tuned by eye in the viewer (2026-10-07), expressed for a level that is mirrored on X as a whole (InstanceScene.WorldMirrorX):
    // yaw-only records = Z-up, 180 degrees about Y, placement inverted; quaternion records = Z-up, pre (270,270,90), post (0,270,0), X<->Z-swapped placement rotation.
    readonly RotationSettings _rotCompact = new() { PreY = 2, FlipPlacement = true };      // with the world mirror on X: plain rotation, yaw direction inverted
    readonly RotationSettings _rotMatrix = new() { PreX = 3, PreY = 3, PreZ = 1, PostY = 3, SwapXZ = true };      // the former final-frame X mirror was the world mirror   // + final-frame X mirror: reverses exactly the 16 pieces that had to be flipped by hand
    bool _editMatrix;
    RotationSettings _rot => _editMatrix ? _rotMatrix : _rotCompact;
    // face selector
    readonly Dictionary<string, Dictionary<int, PlacementOverride>> _overrides = new();
    int _selBatch = -1, _selTri = -1;
    VertexPositionColor[] _selPieceWire = [], _selFace = [];
    string _selText = "";
    Matrix _view, _proj;
    // creature mode (F3): one animated creature at a time, from extracted/creatures
    bool _creatureMode;
    CreatureScene? _creature; string[] _creatureFiles = []; int _creatureIndex, _clip; double _clipTime; bool _paused;
    ObjScene? _levelScene;
    // actors layer (player start with a weapon, bots at the deathmatch spawns, pickups and movers as props)
    ActorLayer? _actors; ObjScene? _actorScene;
    bool _showActors = true, _showPickups, _showBots = true, _yawSign = true;
    float _actorYaw = MathHelper.Pi;           // extra facing in radians (F5 adds 90 degrees); default +180 found by eye, F6 flips the yaw sign
    int _weaponIndex = 5;                      // MAG 60 (weapon chunk = 16 + index)
    // lighting (docs/formats/lighting.md): static pieces carry the game's CPU-computed vertex colours; fog fades to the zone colour (also the clear colour)
    bool _lighting = true, _fog;
    sealed record Zone(Color Colour, float Far, float Fov);
    Dictionary<int, Zone>? _zones;
    bool _instances = true;                       // assemble the level from per-object meshes so orientation can be changed live

    public ViewerGame(Options opt)
    {
        _opt = opt;
        _gfx = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = opt.Width, PreferredBackBufferHeight = opt.Height, GraphicsProfile = GraphicsProfile.HiDef };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        string root = Path.Combine(opt.DataRoot, Environment.GetEnvironmentVariable("TRW_LEVELDIR") ?? "levelgeo");   // TRW_LEVELDIR selects an alternative export
        _levelDirs = Directory.Exists(root) ? Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith("_")).OrderBy(d => d).ToArray() : [];
        _level = Math.Clamp(opt.Level, 0, Math.Max(0, _levelDirs.Length - 1));
        _cull = Math.Clamp(opt.Cull, 0, 2);
        LoadOverrides();
        ApplyRotationFlags(opt.Flags); _wire = opt.Flags.Contains("wire"); _showCulled = opt.Flags.Contains("showculled"); _orbit = opt.Flags.Contains("orbit");
    }

    protected override void LoadContent()
    {
        _effect = new BasicEffect(GraphicsDevice) { TextureEnabled = true, VertexColorEnabled = false, LightingEnabled = false };
        LoadTextureFlips();
        LoadActorFacing();
        _cutout = new AlphaTestEffect(GraphicsDevice) { VertexColorEnabled = false, AlphaFunction = CompareFunction.Greater, ReferenceAlpha = 127 };
        var cullModes = new[] { CullMode.None, CullMode.CullClockwiseFace, CullMode.CullCounterClockwiseFace };
        for (int c = 0; c < 3; c++)
            for (int w = 0; w < 2; w++)
                _raster[c, w] = new RasterizerState { CullMode = cullModes[c], FillMode = w == 1 ? FillMode.WireFrame : FillMode.Solid };
        var modes = new[] { TextureAddressMode.Wrap, TextureAddressMode.Mirror, TextureAddressMode.Clamp };
        for (int f = 0; f < 2; f++) for (int m = 0; m < 3; m++)
            _samplers[f, m] = new SamplerState { Filter = f == 0 ? TextureFilter.Point : TextureFilter.Linear, AddressU = modes[m], AddressV = modes[m] };
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData([Color.White]);
        LoadLevel(_level);
        if (_startCreature == null && _startTime >= 0) _time = _startTime;
        if (_startCreature != null)
        {
            ToggleCreatureMode();
            int idx = Array.FindIndex(_creatureFiles, p => Path.GetFileName(p) == $"obj_{_startCreature}.json");
            if (_creatureMode && idx >= 0)
            {
                _creatureIndex = idx; _creature = CreatureScene.Load(_creatureFiles[idx]);
                _clip = Math.Clamp(_startClip, 0, Math.Max(0, _creature.Clips.Count - 1)); _clipTime = Math.Max(0, _startTime); _paused = _startTime >= 0;
                PoseCreature(true);
            }
        }
    }

    void LoadLevel(int index)
    {
        if (_levelDirs.Length == 0) { Window.Title = "no levels found; run: trw levelgeo"; return; }
        _level = (index + _levelDirs.Length) % _levelDirs.Length;
        BuildScene();
        BuildActors();
        _solo = -1;
        ResetCamera();
        if (_opt.Flags.Contains("topdown")) TopDown();
    }

    // --flags pre=0,2,0  post=0,0,0  (90 degree steps), noZup, mirror, invert, baked
    void ApplyRotationFlags(string flags)
    {
        foreach (string part in flags.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = part.Split('=');
            if (kv[0] is "pre" or "post" or "mpre" or "mpost" && kv.Length == 2)      // m-prefix = quaternion records
            {
                var n = kv[1].Split('/').Select(int.Parse).ToArray();
                var r = kv[0][0] == 'm' ? _rotMatrix : _rotCompact;
                if (kv[0].EndsWith("pre")) { r.PreX = n[0] & 3; r.PreY = n[1] & 3; r.PreZ = n[2] & 3; }
                else { r.PostX = n[0] & 3; r.PostY = n[1] & 3; r.PostZ = n[2] & 3; }
            }
            else if (kv[0] == "noZup") _rotCompact.ZUpConversion = _rotMatrix.ZUpConversion = false;
            else if (kv[0] == "mirror") _rotCompact.MirrorX = true;
            else if (kv[0] == "invert") _rotCompact.FlipPlacement = true;
            else if (kv[0] == "baked") _instances = false;
            else if (kv[0] == "wmx") InstanceScene.WorldMirrorX = true;       // mirror the whole level on X
            else if (kv[0] == "wmz") InstanceScene.WorldMirrorZ = true;       // ... or on Z
            else if (kv[0] == "linear") _point = false;
            else if (kv[0] == "nowm") InstanceScene.WorldMirrorX = false;
            else if (kv[0] == "creature" && kv.Length == 2) _startCreature = kv[1];       // start in creature mode on this object id
            else if (kv[0] == "fog") _fog = true;
            else if (kv[0] == "nolight") { _lighting = false; InstanceScene.Lighting = false; }
            else if (kv[0] == "pickups") _showPickups = true;
            else if (kv[0] == "nobots") _showBots = false;
            else if (kv[0] == "noactors") _showActors = false;
            else if (kv[0] == "actoryaw" && kv.Length == 2) _actorYaw = float.Parse(kv[1], CultureInfo.InvariantCulture) * MathHelper.Pi / 180f;
            else if (kv[0] == "yawsign") _yawSign = false;
            else if (kv[0] == "texhz" && kv.Length == 2) _texRate = Math.Max(0, Array.IndexOf(TexRates, int.Parse(kv[1])));
            else if (kv[0] == "clip" && kv.Length == 2) _startClip = int.Parse(kv[1]);
            else if (kv[0] == "skin" && kv.Length == 2) _skin = int.Parse(kv[1]);
            else if (kv[0] == "showhidden") CreatureScene.ShowHiddenParts = true;
            else if (kv[0] == "yaw" && kv.Length == 2) _startYaw = float.Parse(kv[1], CultureInfo.InvariantCulture);
            else if (kv[0] == "uv" && kv.Length == 2)       // uv=vall|vmirror|uall|umirror|swap (comma separated)
                foreach (string u in kv[1].Split(',')) { if (u == "vall") CreatureScene.FlipVAll = true; else if (u == "vmirror") CreatureScene.FlipVMirror = true; else if (u == "novmirror") CreatureScene.FlipVMirror = false; else if (u == "novall") CreatureScene.FlipVAll = false; else if (u.StartsWith("vtex")) CreatureScene.FlipVTextures.Add(int.Parse(u[4..])); else if (u == "uall") CreatureScene.FlipUAll = true; else if (u == "umirror") CreatureScene.FlipUMirror = true; else if (u == "swap") CreatureScene.SwapUV = true; }
            else if (kv[0] == "time" && kv.Length == 2) { _startTime = double.Parse(kv[1], CultureInfo.InvariantCulture); }      // raw, unmirrored coordinates
            else if (kv[0] == "pmirror") _rotMatrix.PostMirrorX = true;        // quaternion records: final-frame X mirror
            else if (kv[0] is "cmirrorx" or "cmirrory" or "cmirrorz") { if (kv[0][^1] == 'x') _rotCompact.PostMirrorX = true; else if (kv[0][^1] == 'y') _rotCompact.PostMirrorY = true; else _rotCompact.PostMirrorZ = true; }   // yaw-only records
            else if (kv[0] == "nooverrides") _overrides.Clear();
            else if (kv[0] == "noflagfix") InstanceScene.ReverseMode = 0;
            else if (kv[0] == "opposite") InstanceScene.ReverseMode = 2;
        }
    }

    void BuildScene()
    {
        string dir = _levelDirs[_level];
        _selBatch = _selTri = -1; _selPieceWire = _selFace = []; _selText = "";
        _levelScene = _instances && InstanceScene.Available(dir)
            ? InstanceScene.Build(GraphicsDevice, dir, _opt.DataRoot, _rotCompact, _rotMatrix, _textures, LevelOverrides(false))
            : ObjScene.Load(GraphicsDevice, Path.Combine(dir, "world.obj"), _textures);
        if (!_creatureMode) _scene = _levelScene;
    }

    // number row: 1/2/3 turn the model about X/Y/Z before it is placed, 4/5/6 turn the placed piece, Shift = the other direction,
    // 7 invert the placement rotation, 8 Z-up conversion on/off, 9 mirror X, 0 reset, G winding mode (none / reverse flag-1 parts / opposite), I per-object assembly vs baked world.obj, F9 save settings
    void HandleRotationKeys(KeyboardState k)
    {
        if (_levelDirs.Length == 0 || _creatureMode) return;
        int d = (k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift)) ? 3 : 1;     // +90 or -90 (= +270) degrees
        bool changed = false;
        void Step(Keys key, ref int v) { if (Pressed(k, key)) { v = (v + d) & 3; changed = true; } }
        Step(Keys.D1, ref _rot.PreX); Step(Keys.D2, ref _rot.PreY); Step(Keys.D3, ref _rot.PreZ);
        Step(Keys.D4, ref _rot.PostX); Step(Keys.D5, ref _rot.PostY); Step(Keys.D6, ref _rot.PostZ);
        if (Pressed(k, Keys.D7)) { _rot.FlipPlacement = !_rot.FlipPlacement; changed = true; }
        if (Pressed(k, Keys.D8)) { _rot.ZUpConversion = !_rot.ZUpConversion; changed = true; }
        if (Pressed(k, Keys.D9)) { _rot.MirrorX = !_rot.MirrorX; changed = true; }
        if (Pressed(k, Keys.J)) { _rot.SwapXZ = !_rot.SwapXZ; changed = true; }               // J: placement rotation reflected through an X<->Z swap
        if (Pressed(k, Keys.M)) { _rot.PostMirrorX = !_rot.PostMirrorX; changed = true; }      // M / Y / Z: mirror in the final world frame, X / Y / Z
        if (Pressed(k, Keys.Y)) { _rot.PostMirrorY = !_rot.PostMirrorY; changed = true; }
        if (Pressed(k, Keys.Z)) { _rot.PostMirrorZ = !_rot.PostMirrorZ; changed = true; }
        if (Pressed(k, Keys.D0)) { _rot.Reset(); changed = true; }
        if (Pressed(k, Keys.G)) { InstanceScene.ReverseMode = (InstanceScene.ReverseMode + 1) % 3; changed = true; }
        if (Pressed(k, Keys.Tab)) _editMatrix = !_editMatrix;      // choose which record type the rotation keys edit
        if (Pressed(k, Keys.I)) { _instances = !_instances; changed = true; }
        if (Pressed(k, Keys.F1)) { InstanceScene.WorldMirrorX = !InstanceScene.WorldMirrorX; changed = true; }      // F1 / F2: mirror the whole level on world X / Z
        if (Pressed(k, Keys.F2)) { InstanceScene.WorldMirrorZ = !InstanceScene.WorldMirrorZ; changed = true; }
        if (Pressed(k, Keys.F9))
        {
            string path = Path.Combine(_opt.DataRoot, "rotation_settings.txt");
            File.AppendAllText(path, $"{DateTime.Now:s} {Path.GetFileName(_levelDirs[_level])}: yaw-records [{_rotCompact}] quaternion-records [{_rotMatrix}]\n");
        }
        if (changed) { int solo = _solo; BuildScene(); _solo = Math.Min(solo, (_scene?.Batches.Count ?? 0) - 1); }
    }

    // ---------------------------------------------------------------- face selector
    // P or middle click = pick the face under the cursor; H hide the picked piece, V flip its winding, L mirror it in X, N turn it 90 degrees (Shift = -90),
    // U undo all manual fixes on this level. Fixes are saved to placement_overrides.txt, pick details to selection.txt.
    string OverridePath => Path.Combine(_opt.DataRoot, "placement_overrides.txt");

    Dictionary<int, PlacementOverride>? LevelOverrides(bool create)
    {
        if (_levelDirs.Length == 0) return null;
        string name = Path.GetFileName(_levelDirs[_level]);
        if (!_overrides.TryGetValue(name, out var d)) { if (!create) return null; _overrides[name] = d = new(); }
        return d;
    }

    void LoadOverrides()
    {
        if (!File.Exists(OverridePath)) return;
        foreach (string line in File.ReadLines(OverridePath))
        {
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);          // level index hidden flip mirror yawSteps
            if (t.Length != 6 || t[0].StartsWith('#')) continue;
            if (!_overrides.TryGetValue(t[0], out var d)) _overrides[t[0]] = d = new();
            d[int.Parse(t[1])] = new PlacementOverride { Hidden = t[2] == "1", FlipWinding = t[3] == "1", MirrorX = t[4] == "1", YawSteps = int.Parse(t[5]) };
        }
    }

    void SaveOverrides()
    {
        var lines = new List<string> { "# level placementIndex hidden flipWinding mirrorX yawSteps90   (written by the viewer's face selector)" };
        foreach (var (level, d) in _overrides.OrderBy(k => k.Key))
            foreach (var (i, o) in d.OrderBy(k => k.Key))
                if (!o.IsDefault) lines.Add($"{level} {i} {(o.Hidden ? 1 : 0)} {(o.FlipWinding ? 1 : 0)} {(o.MirrorX ? 1 : 0)} {o.YawSteps & 3}");
        File.WriteAllLines(OverridePath, lines);
    }

    void HandleSelectionKeys(KeyboardState k, MouseState m)
    {
        if (_scene == null || !_instances || _creatureMode) return;
        bool middle = m.MiddleButton == ButtonState.Pressed && _prevMouse.MiddleButton == ButtonState.Released;
        if (Pressed(k, Keys.P) || middle) Pick(m.X, m.Y);
        if (_selBatch < 0)
        {
            if (Pressed(k, Keys.U)) { LevelOverrides(false)?.Clear(); SaveOverrides(); BuildScene(); }
            return;
        }

        int placement = _scene.Batches[_selBatch].Tris[_selTri].Placement;
        var ov = LevelOverrides(true)!;
        if (!ov.TryGetValue(placement, out var o)) ov[placement] = o = new PlacementOverride();
        bool changed = false;
        int d = (k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift)) ? 3 : 1;
        if (Pressed(k, Keys.H)) { o.Hidden = true; changed = true; }
        if (Pressed(k, Keys.V)) { o.FlipWinding = !o.FlipWinding; changed = true; }
        if (Pressed(k, Keys.L)) { o.MirrorX = !o.MirrorX; changed = true; }
        if (Pressed(k, Keys.N)) { o.YawSteps = (o.YawSteps + d) & 3; changed = true; }
        if (Pressed(k, Keys.U)) { ov.Clear(); changed = true; }
        if (!changed) return;
        SaveOverrides();
        int keepSolo = _solo; BuildScene(); _solo = Math.Min(keepSolo, (_scene?.Batches.Count ?? 0) - 1);
    }

    void Pick(int mx, int my)
    {
        if (_scene == null) return;
        var vp = GraphicsDevice.Viewport;
        var near = vp.Unproject(new Vector3(mx, my, 0), _proj, _view, Matrix.Identity);
        var far = vp.Unproject(new Vector3(mx, my, 1), _proj, _view, Matrix.Identity);
        var dir = Vector3.Normalize(far - near);
        float best = float.MaxValue; int bb = -1, bt = -1;
        for (int bi = 0; bi < _scene.Batches.Count; bi++)
        {
            if (_solo >= 0 && bi != _solo) continue;
            var v = _scene.Batches[bi].Vertices;
            for (int t = 0; t < v.Length / 3; t++)
            {
                Vector3 a = v[t * 3].Position, b = v[t * 3 + 1].Position, c = v[t * 3 + 2].Position;
                float facing = Vector3.Dot(Vector3.Cross(b - a, c - a), dir);
                if (_cull == 1 && facing >= 0) continue;          // counter-clockwise faces are the visible ones
                if (_cull == 2 && facing <= 0) continue;
                if (RayTriangle(near, dir, a, b, c, out float dist) && dist < best) { best = dist; bb = bi; bt = t; }
            }
        }
        if (bb < 0) { _selBatch = _selTri = -1; _selPieceWire = _selFace = []; _selText = "nothing under the cursor"; return; }
        _selBatch = bb; _selTri = bt;
        Describe(near + dir * best);
    }

    static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0; var e1 = b - a; var e2 = c - a; var p = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det; var s = o - a; float u = Vector3.Dot(s, p) * inv; if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1); float v = Vector3.Dot(d, q) * inv; if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv; return t > 0;
    }

    void Describe(Vector3 hit)
    {
        var batch = _scene!.Batches[_selBatch]; var ti = batch.Tris[_selTri]; var p = _scene.Placements[ti.Placement];
        Vector3 a = batch.Vertices[_selTri * 3].Position, b = batch.Vertices[_selTri * 3 + 1].Position, c = batch.Vertices[_selTri * 3 + 2].Position;
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        var rs = p.Matrix ? _rotMatrix : _rotCompact;
        var ov = LevelOverrides(false)?.GetValueOrDefault(ti.Placement);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        string F(Vector3 v) => string.Format(ic, "({0:F1}, {1:F1}, {2:F1})", v.X, v.Y, v.Z);
        _selText = string.Join(Environment.NewLine, new[] {
            $"level {Path.GetFileName(_levelDirs[_level])}   picked at world {F(hit)}",
            $"piece: placement #{ti.Placement}  object {p.ObjectId} model {p.ModelIndex} def {p.Def}  sector {p.Sector} record {p.Record}  form {p.Form}",
            $"       position {F(p.Position)}  scale {F(p.Scale)}  yaw {(p.YawDegrees?.ToString(ic) ?? "n/a (quaternion)")}  flags {p.Flags}  quaternion ({p.Rotation.X:F3}, {p.Rotation.Y:F3}, {p.Rotation.Z:F3}, {p.Rotation.W:F3})",
            $"face:  texture {ti.Texture}  look {ti.Frame} (piece's texture key {p.TextureKey})  material flags 0x{ti.MaterialFlags:X}  local triangle {ti.LocalTriangle}  (0x800 mirror, 0x20 clamp, 0x10 env-map, 0x4 double-sided)",
            $"       world normal {F(n)}  ({(MathF.Abs(n.Y) > 0.9f ? (n.Y > 0 ? "faces UP" : "faces DOWN") : "wall-like")})",
            InstanceScene.DescribeLocalTriangle(p, ti).TrimEnd(),
            $"settings in effect for this form: {rs}   winding mode {InstanceScene.ReverseMode}",
            $"manual fixes: {(ov == null || ov.IsDefault ? "none" : $"hidden={ov.Hidden} flipWinding={ov.FlipWinding} mirrorX={ov.MirrorX} yawSteps={ov.YawSteps & 3}")}",
        });
        File.WriteAllText(Path.Combine(_opt.DataRoot, "selection.txt"), _selText + Environment.NewLine);
        File.AppendAllText(Path.Combine(_opt.DataRoot, "selected_faces.txt"), DateTime.Now.ToString("s") + Environment.NewLine + _selText + Environment.NewLine + Environment.NewLine);

        // highlight geometry: the whole piece as yellow wire, the face as magenta fill
        var wire = new List<VertexPositionColor>();
        foreach (var bt in _scene.Batches)
            for (int t = 0; t < bt.Tris.Length; t++)
                if (bt.Tris[t].Placement == ti.Placement)
                    for (int i = 0; i < 3; i++) wire.Add(new VertexPositionColor(bt.Vertices[t * 3 + i].Position, Color.Yellow));
        _selPieceWire = wire.ToArray();
        _selFace = [new VertexPositionColor(a, Color.Magenta), new VertexPositionColor(b, Color.Magenta), new VertexPositionColor(c, Color.Magenta)];
    }

    void DrawSelection()
    {
        if (_selFace.Length == 0) return;
        bool tex = _effect.TextureEnabled, vc = _effect.VertexColorEnabled;
        _effect.TextureEnabled = false; _effect.VertexColorEnabled = true;
        GraphicsDevice.DepthStencilState = DepthStencilState.None;
        GraphicsDevice.RasterizerState = _raster[0, 1];
        foreach (var pass in _effect.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, _selPieceWire, 0, _selPieceWire.Length / 3); }
        GraphicsDevice.RasterizerState = _raster[0, 0];
        foreach (var pass in _effect.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, _selFace, 0, 1); }
        _effect.TextureEnabled = tex; _effect.VertexColorEnabled = vc;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
    }

    // ---------------------------------------------------------------- creature mode
    // F3 toggles between the level and the creature viewer. In creature mode: PgUp/PgDn change creature, , and . change clip, Space pauses/plays,
    // Home restarts the clip. Clips run at the clip's own rate (15 ticks/s in the game) and loop back to the clip's loop frame.
    void HandleCreatureKeys(KeyboardState k, float dt)
    {
        if (Pressed(k, Keys.F3)) ToggleCreatureMode();
        if (!_creatureMode || _creature == null) return;
        bool reload = false;
        if (Pressed(k, Keys.PageUp)) { _creatureIndex = (_creatureIndex + 1) % _creatureFiles.Length; reload = true; }
        if (Pressed(k, Keys.PageDown)) { _creatureIndex = (_creatureIndex + _creatureFiles.Length - 1) % _creatureFiles.Length; reload = true; }
        if (reload)
        {
            int step = Pressed(k, Keys.PageDown) ? -1 : 1;
            for (int tries = 0; tries < _creatureFiles.Length; tries++)      // skip files that cannot be loaded instead of crashing
            {
                try { _creature = CreatureScene.Load(_creatureFiles[_creatureIndex]); break; }
                catch (Exception e)
                {
                    File.AppendAllText(Path.Combine(_opt.DataRoot, "viewer_errors.txt"), $"{DateTime.Now:s} {_creatureFiles[_creatureIndex]}: {e.GetType().Name}: {e.Message}\n");
                    _creatureIndex = (_creatureIndex + step + _creatureFiles.Length) % _creatureFiles.Length;
                }
            }
            _clip = 0; _clipTime = 0; PoseCreature(true);
        }
        if (Pressed(k, Keys.OemPeriod) && _creature.Clips.Count > 0) { _clip = (_clip + 1) % _creature.Clips.Count; _clipTime = 0; }
        if (Pressed(k, Keys.OemComma) && _creature.Clips.Count > 0) { _clip = (_clip + _creature.Clips.Count - 1) % _creature.Clips.Count; _clipTime = 0; }
        if (Pressed(k, Keys.Space)) _paused = !_paused;
        if (Pressed(k, Keys.P) || (Mouse.GetState().MiddleButton == ButtonState.Pressed && _prevMouse.MiddleButton == ButtonState.Released)) PickCreature(Mouse.GetState().X, Mouse.GetState().Y);
        if (Pressed(k, Keys.V) && _creatureTexture >= 0)
        {
            if (!CreatureScene.FlipVTextures.Remove(_creatureTexture)) CreatureScene.FlipVTextures.Add(_creatureTexture);
            SaveTextureFlips();
        }
        if (Pressed(k, Keys.Home)) _clipTime = 0;
        if (Pressed(k, Keys.K)) _skin = (_skin + 1) % 3;                                      // K: next skin
        if (Pressed(k, Keys.H)) CreatureScene.ShowHiddenParts = !CreatureScene.ShowHiddenParts;   // H: show the parts the game hides by default (shield shell, banner, overlay)
        if (Pressed(k, Keys.D1)) CreatureScene.FlipVAll = !CreatureScene.FlipVAll;           // UV experiments: 1 flip V all, 2 flip V on mirror-flag materials, 3 flip U all,
        if (Pressed(k, Keys.D2)) CreatureScene.FlipVMirror = !CreatureScene.FlipVMirror;     //  4 flip U on mirror-flag materials, 5 swap U/V, 0 reset
        if (Pressed(k, Keys.D3)) CreatureScene.FlipUAll = !CreatureScene.FlipUAll;
        if (Pressed(k, Keys.D4)) CreatureScene.FlipUMirror = !CreatureScene.FlipUMirror;
        if (Pressed(k, Keys.D5)) CreatureScene.SwapUV = !CreatureScene.SwapUV;
        if (Pressed(k, Keys.D0)) { CreatureScene.FlipUAll = CreatureScene.FlipUMirror = CreatureScene.SwapUV = false; CreatureScene.FlipVMirror = false; CreatureScene.FlipVAll = false; CreatureScene.FlipVTextures.Clear(); SaveTextureFlips(); }
        if (!_paused) _clipTime += dt;
        PoseCreature(false);
    }

    void ToggleCreatureMode()
    {
        if (_creatureMode) { _creatureMode = false; _scene = _levelScene; ResetCamera(); return; }
        _creatureFiles = CreatureScene.Files(_opt.DataRoot).ToArray();
        if (_creatureFiles.Length == 0) { Window.Title = "no creatures found in extracted/creatures; run: trw creatures"; return; }
        _creatureMode = true; _selBatch = _selTri = -1; _selPieceWire = _selFace = [];
        _creature = CreatureScene.Load(_creatureFiles[_creatureIndex]); _clip = 0; _clipTime = 0;
        PoseCreature(true);
    }

    void PoseCreature(bool frameCamera)
    {
        if (_creature == null) return;
        var toWorld = _rotCompact.Base();          // Z-up conversion only: creatures and portraits face the viewer without the extra 180 degree turn the static pieces need
        int clip = _creature.Clips.Count > 0 ? _clip : -1;
        int frame = clip >= 0 ? CreatureScene.FrameAt(_creature.Clips[clip], _clipTime) : 0;
        _scene = _creature.Pose(GraphicsDevice, clip, frame, toWorld, _textures, Path.Combine(_opt.DataRoot, "models", "textures"), 0, _skin);
        _creatureFrame = frame;
        if (frameCamera)
        {
            var centre = (_scene.Min + _scene.Max) / 2f; float size = Math.Max(50f, (_scene.Max - _scene.Min).Length());
            _dist = size * 1.15f; _pivot = centre; _yaw = _startYaw ?? 0.6f; _pitch = -0.25f; _orbit = true; _cam = _pivot - Forward() * _dist;
        }
    }
    int _creatureFrame;
    int _skin;                                 // character skin 0..2 (K cycles; texture frame/palette variants of the skin textures)
    int _creatureTexture = -1;                 // texture picked in creature mode (P / middle click); V flips its T axis, saved to creature_texture_flips.txt
    string FlipFile => Path.Combine(_opt.DataRoot, "creature_texture_flips.txt");
    string? _startCreature; int _startClip; double _startTime = -1; float? _startYaw;

    void LoadTextureFlips()
    {
        if (!File.Exists(FlipFile)) return;
        foreach (string line in File.ReadLines(FlipFile))
            if (int.TryParse(line.Trim(), out int t)) CreatureScene.FlipVTextures.Add(t);
    }

    void SaveTextureFlips() => File.WriteAllLines(FlipFile, CreatureScene.FlipVTextures.OrderBy(t => t).Select(t => t.ToString()));

    /// <summary>Creature mode: pick the nearest face under the cursor, highlight it and remember its texture (V then flips that texture).</summary>
    void PickCreature(int mx, int my)
    {
        if (_scene == null) return;
        var vp = GraphicsDevice.Viewport;
        var near = vp.Unproject(new Vector3(mx, my, 0), _proj, _view, Matrix.Identity);
        var far = vp.Unproject(new Vector3(mx, my, 1), _proj, _view, Matrix.Identity);
        var dir = Vector3.Normalize(far - near);
        float best = float.MaxValue; int bb = -1, bt = -1;
        for (int bi = 0; bi < _scene.Batches.Count; bi++)
        {
            var v = _scene.Batches[bi].Vertices;
            for (int t = 0; t < v.Length / 3; t++)
            {
                Vector3 a = v[t * 3].Position, b = v[t * 3 + 1].Position, c = v[t * 3 + 2].Position;
                float facing = Vector3.Dot(Vector3.Cross(b - a, c - a), dir);
                if (_cull == 1 && facing >= 0) continue;
                if (_cull == 2 && facing <= 0) continue;
                if (RayTriangle(near, dir, a, b, c, out float dist) && dist < best) { best = dist; bb = bi; bt = t; }
            }
        }
        if (bb < 0) { _creatureTexture = -1; _selPieceWire = _selFace = []; return; }
        var batch = _scene.Batches[bb];
        _creatureTexture = int.Parse(batch.Name.Split(' ')[0].Replace("tex_", ""));
        var wire = new List<VertexPositionColor>();
        foreach (var vtx in batch.Vertices) wire.Add(new VertexPositionColor(vtx.Position, Color.Yellow));
        _selPieceWire = wire.ToArray();
        _selFace = [new VertexPositionColor(batch.Vertices[bt * 3].Position, Color.Magenta), new VertexPositionColor(batch.Vertices[bt * 3 + 1].Position, Color.Magenta), new VertexPositionColor(batch.Vertices[bt * 3 + 2].Position, Color.Magenta)];
    }

    // ---------------------------------------------------------------- actors layer
    // F4 pickups on/off, F5 turn all actors 90 degrees, F6 flip the yaw sign, F7 next weapon, F8 bots at spawns on/off, F10 actors on/off
    /// <summary>Zone 0 of the current level from levelgeo/lighting.json: clear/fog colour (rgb &amp; 0xF8), far plane, field of view.</summary>
    Zone? CurrentZone()
    {
        if (_levelDirs.Length == 0) return null;
        if (_zones == null)
        {
            _zones = new();
            string path = Path.Combine(_opt.DataRoot, Environment.GetEnvironmentVariable("TRW_LEVELDIR") ?? "levelgeo", "lighting.json");
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var lv in doc.RootElement.GetProperty("levels").EnumerateArray())
                {
                    var zs = lv.GetProperty("zones").EnumerateArray().ToList();
                    if (zs.Count == 0) continue;
                    var c = zs[0].GetProperty("colorRgba").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    _zones[lv.GetProperty("level").GetInt32()] = new Zone(new Color(c[0] & 0xF8, c[1] & 0xF8, c[2] & 0xF8), zs[0].GetProperty("farPlane").GetSingle(), zs[0].GetProperty("fovDegrees").GetSingle());
                }
            }
        }
        int id = int.TryParse(Path.GetFileName(_levelDirs[_level]).Split('_')[0], out int n) ? n : _level;
        return _zones.GetValueOrDefault(id);
    }

    void BuildActors()
    {
        _actors = _levelDirs.Length == 0 ? null : ActorLayer.Build(_opt.DataRoot, Path.GetFileName(_levelDirs[_level]), _showPickups, _showBots);
        if (_actors != null)
        {
            _actors.WeaponChunk = 16 + _weaponIndex;
            foreach (var act in _actors.Actors.Where(x => x.Kind is "spawn" or "playerStart"))
            {
                var p = act.Position; if (InstanceScene.WorldMirrorX) p.X = -p.X;
                if (FloorBelow(new Vector3(p.X, p.Y + 120f, p.Z)) is float fy) act.FloorY = fy;
            }
        }
        _actorScene = null;
    }

    /// <summary>Height of the highest upward-facing, opaque level surface at or below <paramref name="from"/> (a ray cast straight down), or null if there is none.</summary>
    float? FloorBelow(Vector3 from)
    {
        if (_levelScene == null) return null;
        float best = float.NegativeInfinity;
        foreach (var b in _levelScene.Batches)
        {
            if (b.Translucent) continue;
            var v = b.Vertices;
            for (int t = 0; t < v.Length / 3; t++)
            {
                Vector3 p0 = v[t * 3].Position, p1 = v[t * 3 + 1].Position, p2 = v[t * 3 + 2].Position;
                var n = Vector3.Cross(p1 - p0, p2 - p0);
                if (n.Y <= 0.2f * n.Length()) continue;                           // keep only floor-like triangles (front face up)
                // barycentric test in the XZ plane
                float d = (p1.Z - p2.Z) * (p0.X - p2.X) + (p2.X - p1.X) * (p0.Z - p2.Z);
                if (MathF.Abs(d) < 1e-6f) continue;
                float l0 = ((p1.Z - p2.Z) * (from.X - p2.X) + (p2.X - p1.X) * (from.Z - p2.Z)) / d;
                float l1 = ((p2.Z - p0.Z) * (from.X - p2.X) + (p0.X - p2.X) * (from.Z - p2.Z)) / d;
                float l2 = 1f - l0 - l1;
                if (l0 < -0.001f || l1 < -0.001f || l2 < -0.001f) continue;
                float y = l0 * p0.Y + l1 * p1.Y + l2 * p2.Y;
                if (y <= from.Y && y > best) best = y;
            }
        }
        return float.IsNegativeInfinity(best) ? null : best;
    }

    string FacingFile => Path.Combine(_opt.DataRoot, "actor_facing.txt");

    /// <summary>Remembers the facing convention found with F5/F6 (degrees added to the stored yaw, and whether the yaw sign is flipped for the mirrored world).</summary>
    void SaveActorFacing() => File.WriteAllText(FacingFile, $"yawDegrees {MathHelper.ToDegrees(_actorYaw):F0}\nyawSignFlipped {(_yawSign ? 1 : 0)}\n");

    void LoadActorFacing()
    {
        if (!File.Exists(FacingFile)) return;
        foreach (string line in File.ReadLines(FacingFile))
        {
            var p = line.Split(' ');
            if (p.Length != 2) continue;
            if (p[0] == "yawDegrees" && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float d)) _actorYaw = MathHelper.ToRadians(d);
            else if (p[0] == "yawSignFlipped") _yawSign = p[1] == "1";
        }
    }

    void HandleActorKeys(KeyboardState k)
    {
        bool rebuild = false;
        if (Pressed(k, Keys.F10)) { _showActors = !_showActors; if (!_showActors) _actorScene = null; }
        if (Pressed(k, Keys.F4)) { _showPickups = !_showPickups; rebuild = true; }
        if (Pressed(k, Keys.F8)) { _showBots = !_showBots; rebuild = true; }
        if (Pressed(k, Keys.F5)) { _actorYaw = (_actorYaw + MathHelper.PiOver2) % MathHelper.TwoPi; SaveActorFacing(); }
        if (Pressed(k, Keys.F6)) { _yawSign = !_yawSign; SaveActorFacing(); }
        if (Pressed(k, Keys.F7)) { _weaponIndex = (_weaponIndex + 1) % ActorLayer.WeaponNames.Length; rebuild = true; }
        if (rebuild && !_creatureMode) BuildActors();
        if (_creatureMode || !_showActors || _actors == null || _levelDirs.Length == 0) return;
        _actorScene = _actors.Pose(GraphicsDevice, _time, _rotCompact.Base(), _actorYaw, _yawSign, InstanceScene.WorldMirrorX, _textures,
            Path.Combine(_opt.DataRoot, "models", "textures"), Path.Combine(_opt.DataRoot, "creatures"));
    }

    void ResetCamera()
    {
        if (_scene == null) return;
        var centre = (_scene.Min + _scene.Max) / 2f;
        _dist = Math.Max(800f, (_scene.Max - _scene.Min).Length() * 0.7f);
        _yaw = _opt.Yaw ?? 0f; _pitch = _opt.Pitch ?? -0.45f;
        if (_opt.Pos is Vector3 p) { _cam = p; _pivot = p + Forward() * _dist; }
        else { _pivot = centre; _cam = _pivot - Forward() * _dist; }
    }

    void TopDown()
    {
        if (_scene == null) return;
        _pivot = (_scene.Min + _scene.Max) / 2f; _pitch = -1.55f; _yaw = 0f;
        _dist = Math.Max(800f, (_scene.Max - _scene.Min).Length() * 0.8f);
        _cam = _pivot - Forward() * _dist;
    }

    protected override void Update(GameTime gt)
    {
        var k = Keyboard.GetState(); var m = Mouse.GetState();
        float dt = (float)gt.ElapsedGameTime.TotalSeconds;
        _time += dt;
        if (k.IsKeyDown(Keys.Escape)) Exit();
        if (Pressed(k, Keys.PageUp) && !_creatureMode) LoadLevel(_level + 1);
        if (Pressed(k, Keys.PageDown) && !_creatureMode) LoadLevel(_level - 1);
        if (Pressed(k, Keys.B)) _cull = (_cull + 1) % 3;
        if (Pressed(k, Keys.K)) _showCulled = !_showCulled;
        if (Pressed(k, Keys.X)) _wire = !_wire;
        if (Pressed(k, Keys.C)) { _lighting = !_lighting; InstanceScene.Lighting = _lighting; if (!_creatureMode && _levelDirs.Length > 0) { int keep = _solo; BuildScene(); _solo = keep; } }   // C: game vertex lighting on/off
        if (Pressed(k, Keys.F11)) _fog = !_fog;                                           // F11: the game's distance fog (fades to the zone colour)
        if (Pressed(k, Keys.F)) _point = !_point;
        if (Pressed(k, Keys.OemMinus)) _texRate = Math.Max(0, _texRate - 1);
        if (Pressed(k, Keys.OemPlus)) _texRate = Math.Min(TexRates.Length - 1, _texRate + 1);
        HandleRotationKeys(k);
        HandleActorKeys(k);
        HandleCreatureKeys(k, dt);
        HandleSelectionKeys(k, m);
        if (Pressed(k, Keys.R)) ResetCamera();
        if (Pressed(k, Keys.T)) TopDown();
        if (Pressed(k, Keys.F12)) Shot(Path.Combine(_opt.DataRoot, $"viewer_{_level:D2}.png"));
        if (Pressed(k, Keys.O)) { _orbit = !_orbit; if (_orbit) _pivot = _cam + Forward() * _dist; }
        if (_scene != null)
        {
            int n = _scene.Batches.Count;
            if (Pressed(k, Keys.OemCloseBrackets)) _solo = _solo + 1 >= n ? -1 : _solo + 1;
            if (Pressed(k, Keys.OemOpenBrackets)) _solo = _solo < 0 ? n - 1 : _solo - 1;
            if (Pressed(k, Keys.OemBackslash) || Pressed(k, Keys.OemPipe)) _solo = -1;
        }

        // rotation: arrows, right mouse (look), left mouse (orbit)
        float turn = 1.6f * dt;
        if (k.IsKeyDown(Keys.Left)) _yaw += turn;
        if (k.IsKeyDown(Keys.Right)) _yaw -= turn;
        if (k.IsKeyDown(Keys.Up)) _pitch += turn;
        if (k.IsKeyDown(Keys.Down)) _pitch -= turn;
        int dx = m.X - _prevMouse.X, dy = m.Y - _prevMouse.Y;
        bool look = m.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Pressed;
        bool orbitDrag = _orbit && m.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Pressed;
        if (look || orbitDrag)
        {
            float sign = orbitDrag ? -1f : 1f;
            _yaw -= dx * 0.004f * sign;
            _pitch -= dy * 0.004f * sign;
        }
        _pitch = MathHelper.Clamp(_pitch, -1.55f, 1.55f);

        int wheel = m.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (wheel != 0) _dist = MathHelper.Clamp(_dist * MathF.Pow(0.9f, wheel / 120f), 50f, 60000f);

        float speed = (k.IsKeyDown(Keys.LeftShift) ? 2500f : 600f) * dt;
        var fwd = Forward(); var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.Up));
        Vector3 move = Vector3.Zero;
        if (k.IsKeyDown(Keys.W)) move += fwd;
        if (k.IsKeyDown(Keys.S)) move -= fwd;
        if (k.IsKeyDown(Keys.D)) move += right;
        if (k.IsKeyDown(Keys.A)) move -= right;
        if (k.IsKeyDown(Keys.E)) move += Vector3.Up;
        if (k.IsKeyDown(Keys.Q)) move -= Vector3.Up;
        if (_orbit) { _pivot += move * speed; _cam = _pivot - fwd * _dist; }
        else { _cam += move * speed; _pivot = _cam + fwd * _dist; }

        _prevMouse = m; _prevKeys = k;
        UpdateTitle();
        base.Update(gt);
    }

    bool Pressed(KeyboardState k, Keys key) => k.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);

    Vector3 Forward() => new(MathF.Cos(_pitch) * -MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * -MathF.Cos(_yaw));

    void UpdateTitle()
    {
        if (_creatureMode && _creature != null)
        {
            var c = _creature.Clips.Count > 0 ? _creature.Clips[_clip] : null;
            Window.Title = $"creature object {_creature.ObjectId} ({_creatureIndex + 1}/{_creatureFiles.Length}) | clip {_clip + 1}/{_creature.Clips.Count}" +
                           (c != null ? $" role {c.Role} frame {_creatureFrame}/{c.Frames} @ {c.Rate}/s" : "") + $" | skin {_skin + 1}/3 (K)" + (CreatureScene.ShowHiddenParts ? " | hidden parts SHOWN (H)" : " | H: show hidden parts") + (_paused ? " | PAUSED" : "") +
                           (_creatureTexture >= 0 ? $" | PICKED texture {_creatureTexture}{(CreatureScene.FlipVTextures.Contains(_creatureTexture) ? " (V flipped)" : "")}: V flips it" : " | P: pick a face") + " | uv: " + (CreatureScene.UvDescription() is { Length: > 0 } uvd ? uvd : "as exported") + " | 1-5 UV tests, 0 reset | PgUp/PgDn creature  , . clip  Space pause  F3 back";
            return;
        }
        if (_scene == null || _levelDirs.Length == 0) return;
        string solo = _solo < 0 ? "all materials" : $"material {_solo + 1}/{_scene.Batches.Count}: {_scene.Batches[_solo].Name}";
        Window.Title = $"{Path.GetFileName(_levelDirs[_level])} | {CullNames[_cull]}{(_showCulled ? " (culled faces red)" : "")}{(_wire ? " | wireframe" : "")}" +
                       $" | {(_orbit ? "orbit" : "fly")} | {(_instances ? $"editing {(_editMatrix ? "QUATERNION" : "yaw")} records: {_rot} | other: {(_editMatrix ? _rotCompact : _rotMatrix)} | world mirror: X {(InstanceScene.WorldMirrorX ? "ON" : "off")} Z {(InstanceScene.WorldMirrorZ ? "ON" : "off")} (F1/F2) | winding mode G: {new[] { "none", "reverse flag-1 parts", "OPPOSITE: reverse all but flag-1" }[InstanceScene.ReverseMode]}" : "baked world.obj")} | light {(_lighting ? "game vertex lighting (C)" : "flat (C)")} fog {(_fog ? "ON" : "off")} (F11) | actors {(_showActors ? $"on ({_actors?.Actors.Count ?? 0}), weapon {ActorLayer.WeaponNames[_weaponIndex]} (F7)" : "off")} F4 pickups F5 turn (now +{MathHelper.ToDegrees(_actorYaw):F0} deg) F6 yaw sign ({(_yawSign ? "flipped" : "raw")}) F8 bots F10 | texture animation {_texHz} Hz (- / =) | {solo} | P pick  H hide  V flip  L mirror  N turn  U undo | O orbit  B cull  K culled-red  X wire  [ ] material";
    }

    protected override void Draw(GameTime gt)
    {
        var zone = CurrentZone();
        GraphicsDevice.Clear(_fog && zone != null && !_creatureMode ? zone.Colour : new Color(24, 26, 32));
        if (_scene != null)
        {
            GraphicsDevice.BlendState = BlendState.NonPremultiplied;
            GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            _effect.World = Matrix.Identity;
            _effect.View = Matrix.CreateLookAt(_cam, _cam + Forward(), Vector3.Up);
            _effect.Projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(70), GraphicsDevice.Viewport.AspectRatio, 5f, 60000f);
            _view = _effect.View; _proj = _effect.Projection;
            _cutout.World = Matrix.Identity; _cutout.View = _view; _cutout.Projection = _proj;
            bool fogOn = _fog && zone != null && !_creatureMode;
            // fog starts where the game's depth value passes 995/1000 (about 16 / (1 - 0.995 * (far - 16) / far)) and is full at the far plane
            float far = zone?.Far ?? 2816f, fogStart = 16f / (1f - 0.995f * (far - 16f) / far);
            foreach (var fx in new IEffectFog[] { _effect, _cutout })
            { fx.FogEnabled = fogOn; if (zone != null) fx.FogColor = zone.Colour.ToVector3(); fx.FogStart = fogStart; fx.FogEnd = far; }
            _effect.VertexColorEnabled = true; _cutout.VertexColorEnabled = true;
            int w = _wire ? 1 : 0;

            if (_showCulled && _cull != 0)                      // the faces the current mode throws away, flat red
            {
                GraphicsDevice.RasterizerState = _raster[_cull == 1 ? 2 : 1, w];
                bool tex = _effect.TextureEnabled; var diff = _effect.DiffuseColor; bool vc = _effect.VertexColorEnabled;
                _effect.TextureEnabled = false; _effect.VertexColorEnabled = false; _effect.DiffuseColor = new Vector3(1f, 0.15f, 0.15f);
                DrawBatches(false);
                _effect.TextureEnabled = tex; _effect.DiffuseColor = diff; _effect.VertexColorEnabled = vc;
            }
            GraphicsDevice.RasterizerState = _raster[_cull, w];
            DrawScene(_scene, true);
            if (_actorScene != null && !_creatureMode) DrawScene(_actorScene, false);
            DrawSelection();
        }
        base.Draw(gt);
        if (_opt.Pick.Length > 0 && _frames == 1) { var xy = _opt.Pick.Split(','); Pick(int.Parse(xy[0]), int.Parse(xy[1])); }
        if (_opt.Shot != null && ++_frames == 3) { Shot(_opt.Shot); Exit(); }
    }

    Texture2D? CurrentTexture(Batch b)
    {
        if (b.Frames == null) return b.Texture;
        int k = (int)(_time * _texHz) / b.StepTicks & 255;          // k = (tick / p1) & 255, frame = k % frameCount
        return b.Frames[k % b.Frames.Length];
    }

    /// <summary>The game's order: opaque and cutout parts first (alpha test), then translucent parts ascending by their sort key
    /// (alpha blend, depth test on, depth write off).</summary>
    void DrawScene(ObjScene sc, bool useSolo)
    {
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        for (int i = 0; i < sc.Batches.Count; i++)
        {
            var b = sc.Batches[i];
            if ((useSolo && _solo >= 0 && i != _solo) || b.Translucent) continue;
            _cutout.Texture = CurrentTexture(b) ?? _white; _cutout.DiffuseColor = b.Tint; _cutout.Alpha = 1f;
            GraphicsDevice.SamplerStates[0] = _samplers[_point ? 0 : 1, b.Sampler];
            foreach (var pass in _cutout.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3); }
        }
        GraphicsDevice.BlendState = BlendState.NonPremultiplied;
        GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        var diffuse = _effect.DiffuseColor; float alpha = _effect.Alpha;
        foreach (int i in Enumerable.Range(0, sc.Batches.Count).Where(i => sc.Batches[i].Translucent && (!useSolo || _solo < 0 || i == _solo)).OrderBy(i => sc.Batches[i].SortKey))
        {
            var b = sc.Batches[i];
            _effect.Texture = CurrentTexture(b) ?? _white; _effect.DiffuseColor = b.Tint; _effect.Alpha = b.Alpha;
            GraphicsDevice.SamplerStates[0] = _samplers[_point ? 0 : 1, b.Sampler];
            foreach (var pass in _effect.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3); }
        }
        _effect.DiffuseColor = diffuse; _effect.Alpha = alpha;
        GraphicsDevice.BlendState = BlendState.NonPremultiplied;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
    }

    void DrawBatches(bool textured)
    {
        for (int i = 0; i < _scene!.Batches.Count; i++)
        {
            if (_solo >= 0 && i != _solo) continue;
            var b = _scene.Batches[i];
            if (textured) { _effect.Texture = b.Texture ?? _white; GraphicsDevice.SamplerStates[0] = _samplers[_point ? 0 : 1, b.Sampler]; }
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3);
            }
        }
    }

    void Shot(string path)
    {
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth, h = GraphicsDevice.PresentationParameters.BackBufferHeight;
        var data = new Color[w * h];
        GraphicsDevice.GetBackBufferData(data);
        using var tex = new Texture2D(GraphicsDevice, w, h);
        tex.SetData(data);
        using var fs = File.Create(path);
        tex.SaveAsPng(fs, w, h);
    }
}
