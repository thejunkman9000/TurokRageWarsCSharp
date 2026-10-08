using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Trw.Walk;

/// <summary>A walkable level: first-person controller with the game's movement constants, collision against the level triangles, the game's vertex lighting, fog,
/// translucent animated water. WASD move, mouse look, Space jump, F fog, N noclip fly, R respawn, PgUp/PgDn level, F12 screenshot, Esc quit.</summary>
public sealed class TrwGame : Game
{
    public sealed record Options(string DataRoot, int Level, string? Shot, int Frames, bool AutoWalk, int Width, int Height, bool NoFog = false, bool NoSound = false, bool NoBotAi = false);

    readonly GraphicsDeviceManager _gfx;
    readonly Options _opt;
    readonly string[] _levels;
    readonly Dictionary<int, SamplerState> _samplers = new();
    BasicEffect _fx = null!;
    AlphaTestEffect _cutout = null!;
    SpriteBatch _sprites = null!;
    Texture2D _white = null!;
    GameLevel _level = null!;
    readonly Player _player = new();
    GameAudio _audio = null!;
    Creature? _viewModel;
    Vector3 _vmMin = new(1e9f), _vmMax = new(-1e9f);
    readonly float[] _vmTune = [0.08f, 30f, -90f, -5f];   // scale, x, y, z offsets (view space) - tuned by eye
    double _lastShot = -10;
    Matrix _vmFit = Matrix.Identity;

    /// <summary>Fits a viewmodel into the lower right of the view: the model (x, y, z) -> view (x, z, y), scaled so its forward extent is about 60 units, with its
    /// rear-bottom-right corner anchored just in front of and below the eye. (Viewmodel placement is not decoded; this is tuned by eye.)</summary>
    void FitViewModel()
    {
        if (_viewModel == null) return;
        var swap = new Matrix(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        int clip = _viewModel.ClipByRole("idle");
        Vector3 min = new(1e9f), max = new(-1e9f);
        foreach (var b in _viewModel.Pose(GraphicsDevice, _opt.DataRoot, clip, 0, swap, 0, out _))
            foreach (var v in b.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
        float forward = MathF.Max(1f, max.Z - min.Z), s = 52f / forward;
        // anchor: x so the model sits right of centre, y just below the view centre, rear end (max z) 14 units in front of the eye
        _vmFit = swap * Matrix.CreateScale(s) * Matrix.CreateTranslation(15f - (min.X + max.X) * 0.5f * s, -21f - min.Y * s, -32f - max.Z * s);
    }
    /// <summary>Hitscan weapons. Damage per hit is from the spec (section 2); pellet count, spread, ammo cost and fire interval are assumed (cadence is not decoded).
    /// The viewmodel is object 152 + weapon index (gameplay_defs order), an unverified guess.</summary>
    sealed record WeaponDef(string Name, int ViewModel, float Damage, int Pellets, float SpreadDegrees, float Interval, int AmmoCost);
    static readonly WeaponDef[] Weapons =
    [
        new("MAG 60", 157, 10f, 1, 0f, 0.12f, 1),
        new("SHOTGUN", 158, 6f, 8, 4f, 0.9f, 1),
        new("ASSAULT RIFLE", 159, 17f, 1, 1f, 0.09f, 1),
        new("MINIGUN", 160, 5f, 1, 2.5f, 0.05f, 2),
    ];
    int _weapon;
    int _index, _frame;
    double _nextShot;
    int _kills;
    bool _fog = true, _focused;
    double _time;
    KeyboardState _prev;
    const float TextureHz = 20f;                 // the game's texture tick rate is not known; matches the viewer's default

    public TrwGame(Options opt)
    {
        _opt = opt;
        _gfx = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = opt.Width, PreferredBackBufferHeight = opt.Height, GraphicsProfile = GraphicsProfile.HiDef };
        IsMouseVisible = false;
        string baked = Path.Combine(opt.DataRoot, "baked");
        _levels = Directory.Exists(baked) ? Directory.GetDirectories(baked).Select(Path.GetFileName).Where(n => n != null && char.IsDigit(n[0])).OrderBy(n => n).ToArray()! : [];
        _index = Math.Clamp(opt.Level, 0, Math.Max(0, _levels.Length - 1));
        _fog = !opt.NoFog;
    }

    protected override void LoadContent()
    {
        _fx = new BasicEffect(GraphicsDevice) { TextureEnabled = true, VertexColorEnabled = true, LightingEnabled = false };
        _cutout = new AlphaTestEffect(GraphicsDevice) { VertexColorEnabled = true, AlphaFunction = CompareFunction.Greater, ReferenceAlpha = 127 };
        _sprites = new SpriteBatch(GraphicsDevice);
        _viewModel = Creature.Load(_opt.DataRoot, $"obj_{Weapons[0].ViewModel}");                                   // [unverified] viewmodel objects 152-173 in weapon order: 157 = MAG 60
        if (Environment.GetEnvironmentVariable("TRW_VM") is { Length: > 0 } vm) { var q = vm.Split(','); _viewModel = Creature.Load(_opt.DataRoot, $"obj_{q[0]}"); for (int i = 1; i < q.Length && i < 5; i++) _vmTune[i - 1] = float.Parse(q[i], CultureInfo.InvariantCulture); }
        FitViewModel();
        _audio = new GameAudio(_opt.DataRoot) { Enabled = !_opt.NoSound };
        _white = new Texture2D(GraphicsDevice, 1, 1); _white.SetData([Color.White]);
        var modes = new[] { TextureAddressMode.Wrap, TextureAddressMode.Mirror, TextureAddressMode.Clamp };
        for (int m = 0; m < 3; m++) _samplers[m] = new SamplerState { Filter = TextureFilter.Point, AddressU = modes[m], AddressV = modes[m] };      // point filtering: the N64 look
        LoadLevel(_index);
        if (int.TryParse(Environment.GetEnvironmentVariable("TRW_WEAPON"), out int w0)) SelectWeapon(w0);
    }

    void LoadLevel(int index)
    {
        if (_levels.Length == 0) { Window.Title = "no baked levels found: run `trw bake` first"; return; }
        _index = (index + _levels.Length) % _levels.Length;
        _level = GameLevel.Load(GraphicsDevice, _opt.DataRoot, _levels[_index]);
        var start = _level.PlayerStart;
        // some spawn points are recorded off the floor (level 1: 18 units below it): snap to the floor just above, else the nearest one below, else the lowest above
        if (Collision.GroundHeight(_level, start, Player.Radius, start.Y + 60f) is float near) start.Y = near;
        else if (Collision.LowestGroundAbove(_level, start, start.Y) is float up) start.Y = up;
        _level.PlayerStart = start;
        _player.Respawn(start, _level.PlayerFacing);
        if (Environment.GetEnvironmentVariable("TRW_POS") is { Length: > 0 } pv)                // debugging: x,y,z,yawDegrees
        {
            var q = pv.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            _player.Respawn(new Vector3(q[0], q[1], q[2]), MathHelper.ToRadians(q[3]));
        }
        // let the player settle onto the floor
        for (int i = 0; i < 90; i++) _player.Update(1f / 60f, default, _level);
        _player.EvLand = _player.EvJump = _player.EvHurt = _player.EvTeleport = false;
        _audio?.PlayMusic(SongForLevel(_index));
    }

    protected override void Update(GameTime gt)
    {
        var k = Keyboard.GetState();
        float dt = Math.Min((float)gt.ElapsedGameTime.TotalSeconds, 0.1f);
        _time += dt; _frame++;
        if (k.IsKeyDown(Keys.Escape)) Exit();
        if (_levels.Length == 0) return;
        if (Pressed(k, Keys.PageUp)) LoadLevel(_index + 1);
        if (Pressed(k, Keys.PageDown)) LoadLevel(_index - 1);
        if (Pressed(k, Keys.F)) _fog = !_fog;
        if (Pressed(k, Keys.D1)) SelectWeapon(0);
        if (Pressed(k, Keys.D2)) SelectWeapon(1);
        if (Pressed(k, Keys.D3)) SelectWeapon(2);
        if (Pressed(k, Keys.D4)) SelectWeapon(3);
        if (Pressed(k, Keys.M)) _audio.MusicVolume = _audio.MusicVolume > 0 ? 0f : 0.5f;
        if (Pressed(k, Keys.N)) _player.NoClip = !_player.NoClip;
        if (Pressed(k, Keys.R)) _player.Respawn(_level.PlayerStart, _level.PlayerFacing);
        if (Pressed(k, Keys.F12)) Shot(Path.Combine(_opt.DataRoot, $"game_{_index:D2}.png"));

        PlayerInput input = default;
        if (_opt.AutoWalk && Environment.GetEnvironmentVariable("TRW_IDLE") == null)                                                                       // scripted input for headless checks: forward, strafe right, jump, turn right
        {
            input.Forward = _frame is > 5 and < 40 ? 1f : 0f;
            input.Strafe = _frame is >= 40 and < 80 ? 1f : 0f;
            input.Jump = _frame is > 90 and < 105;
            input.LookYaw = _frame is > 120 and < 150 ? -0.03f : 0f;                              // negative = turn right (same as moving the mouse right)
        }
        else if (IsActive)
        {
            int cx = Window.ClientBounds.Width / 2, cy = Window.ClientBounds.Height / 2;
            var m = Mouse.GetState();
            if (_focused) { input.LookYaw = -(m.X - cx) * 0.0025f; input.LookPitch = -(m.Y - cy) * 0.0025f; }   // mouse right = yaw decreases (yaw grows towards screen left)
            Mouse.SetPosition(cx, cy); _focused = true;
            input.Forward = (k.IsKeyDown(Keys.W) ? 1f : 0f) - (k.IsKeyDown(Keys.S) ? 1f : 0f);
            input.Strafe = (k.IsKeyDown(Keys.D) ? 1f : 0f) - (k.IsKeyDown(Keys.A) ? 1f : 0f);
            input.Jump = k.IsKeyDown(Keys.Space);
        }
        // lifts: carry whoever stands on them, start trips when a rider has waited long enough
        foreach (var l in _level.Lifts)
        {
            bool rider = _player.Grounded && !_player.NoClip && l.Covers(_player.Feet.X, _player.Feet.Z) && MathF.Abs(_player.Feet.Y - l.Top) < 4f;
            l.Update(dt * 15f, rider);
            if (rider) { _player.Feet += l.Delta; }
        }
        _player.Update(dt, input, _level);
        PlayEvents();
        // hitscan MAG 60: left mouse
        bool fire = _opt.AutoWalk ? Environment.GetEnvironmentVariable("TRW_FIRE") != null : IsActive && _focused && Mouse.GetState().LeftButton == ButtonState.Pressed;
        if (fire && !_player.Dead && _time >= _nextShot && _player.Items.Ammo[0] >= Weapons[_weapon].AmmoCost) { _nextShot = _time + Weapons[_weapon].Interval; _lastShot = _time; Fire(); }
        foreach (var bot in _level.Bots)
        {
            if (bot.Dead && (bot.DeadTimer += dt * 15f) > 75f) bot.Reset();
            if (!_opt.NoBotAi) bot.Update(dt, _level, _player);
            if (bot.EvFire) _audio.Play(SoundIds.FireMag60, _time, 0.6f);
        }
        foreach (var pk in _level.Pickups) { pk.Update(dt * 15f); if (!_player.Dead && pk.Touches(_player.Feet)) { if (pk.Grant(_player)) _audio.Play(SoundIds.Pickup, _time); } }
        if (!_player.Dead) foreach (var tp in _level.Teleporters) if (tp.Touches(_player.Feet)) { _player.Teleport(tp.Destination, tp.DestinationFacing); break; }
        _prev = k;

        Window.Title = $"{_level.Name} | pos {_player.Feet.X:F0}, {_player.Feet.Y:F0}, {_player.Feet.Z:F0} | speed {_player.HorizontalSpeed:F0} u/s | {(_player.NoClip ? "NOCLIP" : _player.Grounded ? "on ground" : "in air")} | fog {(_fog ? "on" : "off")} (F) | WASD, mouse, Space jump, N noclip, R respawn, PgUp/PgDn level";

        if ((_opt.AutoWalk || Environment.GetEnvironmentVariable("TRW_LOG") != null) && _frame % 30 == 0)
            File.AppendAllText(Path.Combine(_opt.DataRoot, "game_log.txt"), $"frame {_frame}: feet {_player.Feet.X:F1} {_player.Feet.Y:F1} {_player.Feet.Z:F1} grounded {_player.Grounded} speed {_player.HorizontalSpeed:F0} hp {_player.Health:F0} ammo {string.Join("/", _player.Items.Ammo.Take(3))} pu {_player.Items.PowerUp} water {_player.WaterDepth:F0} weapon {Weapons[_weapon].Name} kills {_kills}\n");
        _audio.Update(_time);
        base.Update(gt);
    }

    /// <summary>Sound hooks. The game sound ids are provisional guesses until docs/formats/sound_events.md is verified.</summary>
    void PlayEvents()
    {
        if (_player.EvJump) _audio.Play(SoundIds.Jump, _time);
        if (_player.EvLand) _audio.Play(SoundIds.Land, _time);
        if (_player.EvHurt) _audio.Play(SoundIds.Hurt, _time);
        if (_player.EvTeleport) _audio.Play(SoundIds.Teleport, _time);
        _player.EvJump = _player.EvLand = _player.EvHurt = _player.EvTeleport = false;
    }

    void SelectWeapon(int i)
    {
        if (i < 0 || i >= Weapons.Length || i == _weapon) return;
        _weapon = i;
        _viewModel = Creature.Load(_opt.DataRoot, $"obj_{Weapons[i].ViewModel}");
        FitViewModel();
    }

    void Fire()
    {
        var w = Weapons[_weapon];
        _player.Items.Ammo[0] -= w.AmmoCost;
        var origin = _player.Eye;
        var rng = Random.Shared;
        for (int p = 0; p < w.Pellets; p++)
        {
            var dir = _player.Forward;
            if (w.SpreadDegrees > 0)
            {
                float s = w.SpreadDegrees * MathF.PI / 180f;
                dir = Vector3.Normalize(Vector3.Transform(dir, Matrix.CreateRotationY((float)(rng.NextDouble() * 2 - 1) * s) * Matrix.CreateFromAxisAngle(Vector3.Cross(dir, Vector3.Up), (float)(rng.NextDouble() * 2 - 1) * s)));
            }
            float limit = Collision.RayLevel(_level, origin, dir, 20000f) ?? 20000f;
            Bot? target = null;
            foreach (var bot in _level.Bots)
            {
                if (bot.Dead) continue;
                if (Collision.RayCylinder(origin, dir, bot.Feet, Bot.Radius, Bot.Height) is float d && d < limit) { limit = d; target = bot; }
            }
            if (target != null) { target.Damage(w.Damage); if (target.Dead) _kills++; }
        }
        _audio.Play(SoundIds.FireMag60, _time);
    }

    // placeholder until the level -> song mapping is decoded (docs/formats/sound_events.md): cycle through the long songs
    static int SongForLevel(int index) => new[] { 52, 56, 57, 58, 59, 65 }[index % 6];

    bool Pressed(KeyboardState k, Keys key) => k.IsKeyDown(key) && !_prev.IsKeyDown(key);

    protected override void Draw(GameTime gt)
    {
        GraphicsDevice.Clear(_fog ? _level.FogColour : new Color(24, 26, 32));
        float aspect = GraphicsDevice.Viewport.AspectRatio;
        var view = Matrix.CreateLookAt(_player.Eye, _player.Eye + _player.Forward, Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60), aspect, 8f, 8000f);
        float far = _level.FogFar, fogStart = 16f / (1f - 0.995f * (far - 16f) / far);          // fog starts where the game's depth value passes 995/1000, full at the far plane
        foreach (var fx in new IEffectFog[] { _fx, _cutout })
        { fx.FogEnabled = _fog; fx.FogColor = _level.FogColour.ToVector3(); fx.FogStart = fogStart; fx.FogEnd = far; }
        _fx.World = _cutout.World = Matrix.Identity; _fx.View = _cutout.View = view; _fx.Projection = _cutout.Projection = proj;

        GraphicsDevice.RasterizerState = RasterizerState.CullClockwise;                         // front faces are counter-clockwise
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        var extra = new List<Batch>();
        foreach (var l in _level.Lifts) if (l.ModelPositions.Length > 0) extra.Add(l.Mesh());
        foreach (var tp in _level.Teleporters) extra.AddRange(tp.Batches);
        foreach (var pk in _level.Pickups) if (pk.Active) extra.Add(pk.Mesh(_time));
        foreach (var bot in _level.Bots.Where(b => !b.Dead)) extra.AddRange(bot.Batches(GraphicsDevice, _opt.DataRoot, _time));
        foreach (var b in _level.Batches.Concat(extra.Where(b => !b.Translucent)).Where(b => !b.Translucent))
        {
            _cutout.Texture = Current(b) ?? _white; _cutout.DiffuseColor = b.Tint; _cutout.Alpha = 1f;
            GraphicsDevice.SamplerStates[0] = _samplers[b.Sampler];
            foreach (var pass in _cutout.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3); }
        }
        GraphicsDevice.BlendState = BlendState.NonPremultiplied;                                // translucent parts after the opaque ones, ordered by the game's sort key
        GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        foreach (var b in _level.Batches.Concat(extra).Where(b => b.Translucent).OrderBy(b => b.SortKey))
        {
            _fx.Texture = Current(b) ?? _white; _fx.DiffuseColor = b.Tint; _fx.Alpha = b.Alpha;
            GraphicsDevice.SamplerStates[0] = _samplers[b.Sampler];
            foreach (var pass in _fx.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3); }
        }

        if (_viewModel != null)                                                                  // first-person weapon: drawn in view space on a cleared depth buffer
        {
            GraphicsDevice.Clear(ClearOptions.DepthBuffer, Color.Black, 1f, 0);
            GraphicsDevice.BlendState = BlendState.Opaque; GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            _cutout.View = Matrix.Identity; _fx.View = Matrix.Identity;
            foreach (var fx in new IEffectFog[] { _fx, _cutout }) fx.FogEnabled = false;
            int fireClip = _viewModel.ClipByRole("fire"), clip = _viewModel.ClipByRole("idle"), frame;
            if (fireClip >= 0 && _time - _lastShot < _viewModel.Clips[fireClip].Frames / 15.0) { clip = fireClip; frame = (int)((_time - _lastShot) * 15); }
            else frame = _viewModel.FrameAt(clip, _time);
            var swap = new Matrix(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);              // model (x, y, z) -> view (x, z, y): model y negative = forward = view -Z
            var toView = Environment.GetEnvironmentVariable("TRW_VM") != null ? swap * Matrix.CreateScale(_vmTune[0]) * Matrix.CreateTranslation(_vmTune[1], _vmTune[2], _vmTune[3]) : _vmFit;
            foreach (var b in _viewModel.Pose(GraphicsDevice, _opt.DataRoot, clip, frame, toView, 0, out _))
            {
                if (_frame == 15) foreach (var v in b.Vertices) { _vmMin = Vector3.Min(_vmMin, v.Position); _vmMax = Vector3.Max(_vmMax, v.Position); }
                if (b.Translucent) continue;
                _cutout.Texture = b.Texture ?? _white; _cutout.DiffuseColor = b.Tint; _cutout.Alpha = 1f;
                GraphicsDevice.SamplerStates[0] = _samplers[b.Sampler];
                foreach (var pass in _cutout.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3); }
            }
                    if (_frame == 15) File.AppendAllText(Path.Combine(_opt.DataRoot, "game_log.txt"), $"viewmodel bounds {_vmMin} {_vmMax}" + Environment.NewLine);
        }

        // HUD: water tint, damage flash, health bar, crosshair
        _sprites.Begin();
        int cx = GraphicsDevice.Viewport.Width / 2, cy = GraphicsDevice.Viewport.Height / 2;
        var screen = new Rectangle(0, 0, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        if (_player.EyeUnderwater) _sprites.Draw(_white, screen, new Color(20, 60, 110) * 0.45f);
        if (_player.HurtFlash > 0) _sprites.Draw(_white, screen, new Color(200, 0, 0) * (_player.HurtFlash * 1.2f));
        if (_player.Dead) _sprites.Draw(_white, screen, new Color(120, 0, 0) * 0.6f);
        int ax = 16, ay = GraphicsDevice.Viewport.Height - 52;                                  // ammo pips: bullet, energy, explosive; then a power-up block
        Color[] ammoColours = [new Color(240, 210, 70), new Color(80, 160, 255), new Color(240, 140, 50)];
        for (int i = 0; i < 3; i++) _sprites.Draw(_white, new Rectangle(ax, ay - i * 8, Math.Min(_player.Items.Ammo[i], 100) * 2, 5), ammoColours[i]);
        if (_player.Items.PowerUpTicks > 0) _sprites.Draw(_white, new Rectangle(ax + 220, GraphicsDevice.Viewport.Height - 30, (int)(_player.Items.PowerUpTicks / 4), 14), new Color(190, 80, 230));
        int barW = 200, barH = 14, bx = 16, by = GraphicsDevice.Viewport.Height - 30;
        _sprites.Draw(_white, new Rectangle(bx - 2, by - 2, barW + 4, barH + 4), Color.Black * 0.6f);
        _sprites.Draw(_white, new Rectangle(bx, by, (int)(barW * _player.Health / Player.MaxHealth), barH), _player.Health > 30 ? new Color(60, 190, 70) : new Color(210, 50, 40));
        _sprites.Draw(_white, new Rectangle(cx - 8, cy - 1, 6, 2), Color.White); _sprites.Draw(_white, new Rectangle(cx + 2, cy - 1, 6, 2), Color.White);
        _sprites.Draw(_white, new Rectangle(cx - 1, cy - 8, 2, 6), Color.White); _sprites.Draw(_white, new Rectangle(cx - 1, cy + 2, 2, 6), Color.White);
        _sprites.End();
        base.Draw(gt);
        if (_opt.Shot != null && _frame >= _opt.Frames) { Shot(_opt.Shot); Exit(); }
    }

    Texture2D? Current(Batch b)
    {
        if (b.Frames == null) return b.Texture;
        int k = (int)(_time * TextureHz) / b.StepTicks & 255;                                  // k = (tick / p1) & 255, frame = k % frameCount
        return b.Frames[k % b.Frames.Length];
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
