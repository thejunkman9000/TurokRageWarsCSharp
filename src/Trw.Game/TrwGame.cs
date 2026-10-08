using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Trw.Walk;

/// <summary>A walkable level: first-person controller with the game's movement constants, collision against the level triangles, the game's vertex lighting, fog,
/// translucent animated water. WASD move, mouse look, Space jump, F fog, N noclip fly, R respawn, PgUp/PgDn level, F12 screenshot, Esc quit.</summary>
public sealed class TrwGame : Game
{
    public sealed record Options(string DataRoot, int Level, string? Shot, int Frames, bool AutoWalk, int Width, int Height, bool NoFog = false);

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
    int _index, _frame;
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
        _white = new Texture2D(GraphicsDevice, 1, 1); _white.SetData([Color.White]);
        var modes = new[] { TextureAddressMode.Wrap, TextureAddressMode.Mirror, TextureAddressMode.Clamp };
        for (int m = 0; m < 3; m++) _samplers[m] = new SamplerState { Filter = TextureFilter.Point, AddressU = modes[m], AddressV = modes[m] };      // point filtering: the N64 look
        LoadLevel(_index);
    }

    void LoadLevel(int index)
    {
        if (_levels.Length == 0) { Window.Title = "no baked levels found: run `trw bake` first"; return; }
        _index = (index + _levels.Length) % _levels.Length;
        _level = GameLevel.Load(GraphicsDevice, _opt.DataRoot, _levels[_index]);
        var start = _level.PlayerStart;
        // some spawn points are recorded off the floor: use the nearest floor below, else the lowest floor above (not verified against the game)
        if (Collision.GroundHeight(_level, start, Player.Radius, start.Y + 60f) == null && Collision.LowestGroundAbove(_level, start, start.Y) is float up) start.Y = up;
        _level.PlayerStart = start;
        _player.Respawn(start, _level.PlayerFacing);
        // let the player settle onto the floor
        for (int i = 0; i < 90; i++) _player.Update(1f / 60f, default, _level);
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
        if (Pressed(k, Keys.N)) _player.NoClip = !_player.NoClip;
        if (Pressed(k, Keys.R)) _player.Respawn(_level.PlayerStart, _level.PlayerFacing);
        if (Pressed(k, Keys.F12)) Shot(Path.Combine(_opt.DataRoot, $"game_{_index:D2}.png"));

        PlayerInput input = default;
        if (_opt.AutoWalk)                                                                       // scripted input for headless checks: walk ahead, jump once
        {
            input.Forward = _frame > 5 ? 1f : 0f; input.Jump = _frame is > 60 and < 75;
            input.LookYaw = _frame is > 100 and < 130 ? 0.03f : 0f;
        }
        else if (IsActive)
        {
            int cx = Window.ClientBounds.Width / 2, cy = Window.ClientBounds.Height / 2;
            var m = Mouse.GetState();
            if (_focused) { input.LookYaw = (m.X - cx) * 0.0025f; input.LookPitch = -(m.Y - cy) * 0.0025f; }
            Mouse.SetPosition(cx, cy); _focused = true;
            input.Forward = (k.IsKeyDown(Keys.W) ? 1f : 0f) - (k.IsKeyDown(Keys.S) ? 1f : 0f);
            input.Strafe = (k.IsKeyDown(Keys.D) ? 1f : 0f) - (k.IsKeyDown(Keys.A) ? 1f : 0f);
            input.Jump = k.IsKeyDown(Keys.Space);
        }
        _player.Update(dt, input, _level);
        _prev = k;

        Window.Title = $"{_level.Name} | pos {_player.Feet.X:F0}, {_player.Feet.Y:F0}, {_player.Feet.Z:F0} | speed {_player.HorizontalSpeed:F0} u/s | {(_player.NoClip ? "NOCLIP" : _player.Grounded ? "on ground" : "in air")} | fog {(_fog ? "on" : "off")} (F) | WASD, mouse, Space jump, N noclip, R respawn, PgUp/PgDn level";

        if (_opt.AutoWalk && _frame % 30 == 0)
            File.AppendAllText(Path.Combine(_opt.DataRoot, "game_log.txt"), $"frame {_frame}: feet {_player.Feet.X:F1} {_player.Feet.Y:F1} {_player.Feet.Z:F1} grounded {_player.Grounded} speed {_player.HorizontalSpeed:F0}\n");
        base.Update(gt);
    }

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
        foreach (var b in _level.Batches.Where(b => !b.Translucent))
        {
            _cutout.Texture = Current(b) ?? _white; _cutout.DiffuseColor = b.Tint; _cutout.Alpha = 1f;
            GraphicsDevice.SamplerStates[0] = _samplers[b.Sampler];
            foreach (var pass in _cutout.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3); }
        }
        GraphicsDevice.BlendState = BlendState.NonPremultiplied;                                // translucent parts after the opaque ones, ordered by the game's sort key
        GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        foreach (var b in _level.Batches.Where(b => b.Translucent).OrderBy(b => b.SortKey))
        {
            _fx.Texture = Current(b) ?? _white; _fx.DiffuseColor = b.Tint; _fx.Alpha = b.Alpha;
            GraphicsDevice.SamplerStates[0] = _samplers[b.Sampler];
            foreach (var pass in _fx.CurrentTechnique.Passes) { pass.Apply(); GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, b.Vertices, 0, b.Vertices.Length / 3); }
        }

        // crosshair
        _sprites.Begin();
        int cx = GraphicsDevice.Viewport.Width / 2, cy = GraphicsDevice.Viewport.Height / 2;
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
