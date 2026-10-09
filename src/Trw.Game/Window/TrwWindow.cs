using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Trw.Platform;
using Trw.Platform.Gfx;

namespace Trw.Game;

/// <summary>
/// The MonoGame front end: a 60 Hz window that ticks the console's retrace, feeds controller input into the game's PIF emulation
/// and (later) draws the display lists the game builds.
/// </summary>
public sealed class TrwWindow : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager graphics;
    private readonly AudioOut audio = new();
    private readonly DisplayListInterpreter interpreter = new();
    private volatile IReadOnlyList<GfxCommand>? pendingFrame;
    private GfxRenderer? renderer;
    private int frame, drawnFrames, shotsTaken;
    // previews: TRW_SHOT_DIR=<dir> [TRW_SHOT_EVERY=<display lists>] [TRW_SHOT_MAX=<files>] saves PNGs of the rendered frames
    private readonly string? shotDir = Environment.GetEnvironmentVariable("TRW_SHOT_DIR");
    private readonly int shotEvery = int.TryParse(Environment.GetEnvironmentVariable("TRW_SHOT_EVERY"), out var se) && se > 0 ? se : 30;
    private readonly int shotMax = int.TryParse(Environment.GetEnvironmentVariable("TRW_SHOT_MAX"), out var sm) && sm > 0 ? sm : 20;
    private readonly double autoExitSeconds = double.TryParse(Environment.GetEnvironmentVariable("TRW_EXIT_AFTER"), out var ea) ? ea : 0;

    public TrwWindow()
    {
        graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 960, PreferredBackBufferHeight = 720 };
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);
        if (double.TryParse(Environment.GetEnvironmentVariable("TRW_FAST"), out var speed) && speed > 1)   // test runs: retrace ticks at <speed> x 60 Hz
        {
            TargetElapsedTime = TimeSpan.FromSeconds(1.0 / (60 * speed));
            graphics.SynchronizeWithVerticalRetrace = false;
        }
        Window.Title = "Turok: Rage Wars";
        Content.RootDirectory = "Content";
        Window.AllowUserResizing = true;
        // the display list is interpreted the moment the game starts the task (its memory is consistent then); the window thread draws the result
        Host.GraphicsTask = task => pendingFrame = interpreter.Run(task);
    }

    protected override void LoadContent()
    {
        renderer = new GfxRenderer(GraphicsDevice, Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("Combiner"));
        base.LoadContent();
    }

    protected override void Initialize()
    {
        Host.ReadPad = ReadPad;
        audio.Hook();
        base.Initialize();
    }

    // scripted input for unattended runs: TRW_PAD_SCRIPT="<tick>:<BUTTON>[+<BUTTON>...]:<ticks held>,..." drives pad 0 (ticks are 60 Hz window updates).
    // BUTTON = A B Z START UP DOWN LEFT RIGHT L R CUP CDOWN CLEFT CRIGHT SUP SDOWN SLEFT SRIGHT (S* = analogue stick at full tilt)
    private static readonly (int start, int len, ushort buttons, int sx, int sy)[] script = ParseScript(Environment.GetEnvironmentVariable("TRW_PAD_SCRIPT"));
    private static int scriptTick;

    private static (int, int, ushort, int, int)[] ParseScript(string? s)
    {
        var list = new List<(int, int, ushort, int, int)>();
        foreach (var item in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split(':');
            if (p.Length < 2 || !int.TryParse(p[0], out var start)) continue;
            int len = p.Length > 2 && int.TryParse(p[2], out var l) ? l : 6, sx = 0, sy = 0; ushort b = 0;
            foreach (var name in p[1].Split('+'))
                switch (name.ToUpperInvariant())
                {
                    case "A": b |= 0x8000; break; case "B": b |= 0x4000; break; case "Z": b |= 0x2000; break; case "START": b |= 0x1000; break;
                    case "UP": b |= 0x0800; break; case "DOWN": b |= 0x0400; break; case "LEFT": b |= 0x0200; break; case "RIGHT": b |= 0x0100; break;
                    case "L": b |= 0x0020; break; case "R": b |= 0x0010; break;
                    case "CUP": b |= 0x0008; break; case "CDOWN": b |= 0x0004; break; case "CLEFT": b |= 0x0002; break; case "CRIGHT": b |= 0x0001; break;
                    case "SUP": sy = 80; break; case "SDOWN": sy = -80; break; case "SLEFT": sx = -80; break; case "SRIGHT": sx = 80; break;
                }
            list.Add((start, len, b, sx, sy));
        }
        return list.ToArray();
    }

    /// <summary>N64 controller bits: A 8000, B 4000, Z 2000, Start 1000, D-pad 0800/0400/0200/0100, L 0020, R 0010, C-buttons 0008/0004/0002/0001.</summary>
    private static bool ReadPad(int pad, out ushort buttons, out sbyte stickX, out sbyte stickY)
    {
        buttons = 0; stickX = 0; stickY = 0;
        int x = 0, y = 0;
        bool connected = false;
        if (pad == 0)
            foreach (var (start, len, b, sx, sy) in script)
                if (scriptTick >= start && scriptTick < start + len) { buttons |= b; x += sx; y += sy; connected = true; }
        var gp = GamePad.GetState(pad);
        if (gp.IsConnected)
        {
            connected = true;
            var b = gp.Buttons;
            if (b.A == ButtonState.Pressed) buttons |= 0x8000;
            if (b.B == ButtonState.Pressed || b.X == ButtonState.Pressed) buttons |= 0x4000;
            if (b.Start == ButtonState.Pressed) buttons |= 0x1000;
            var d = gp.DPad;
            if (d.Up == ButtonState.Pressed) buttons |= 0x0800;
            if (d.Down == ButtonState.Pressed) buttons |= 0x0400;
            if (d.Left == ButtonState.Pressed) buttons |= 0x0200;
            if (d.Right == ButtonState.Pressed) buttons |= 0x0100;
            if (b.LeftShoulder == ButtonState.Pressed) buttons |= 0x0020;
            if (b.RightShoulder == ButtonState.Pressed) buttons |= 0x0010;
            if (gp.Triggers.Left > 0.4f || gp.Triggers.Right > 0.4f) buttons |= 0x2000;
            var rs = gp.ThumbSticks.Right;
            if (rs.Y > 0.5f) buttons |= 0x0008;
            if (rs.Y < -0.5f) buttons |= 0x0004;
            if (rs.X < -0.5f) buttons |= 0x0002;
            if (rs.X > 0.5f) buttons |= 0x0001;
            var ls = gp.ThumbSticks.Left;
            x += (int)(ls.X * 80); y += (int)(ls.Y * 80);
        }
        if (pad == 0)
        {
            connected = true;
            var k = Keyboard.GetState();
            if (k.IsKeyDown(Keys.Enter)) buttons |= 0x1000;
            if (k.IsKeyDown(Keys.Z)) buttons |= 0x2000;
            if (k.IsKeyDown(Keys.X)) buttons |= 0x4000;
            if (k.IsKeyDown(Keys.C)) buttons |= 0x8000;
            x += (k.IsKeyDown(Keys.Right) ? 80 : 0) - (k.IsKeyDown(Keys.Left) ? 80 : 0);
            y += (k.IsKeyDown(Keys.Up) ? 80 : 0) - (k.IsKeyDown(Keys.Down) ? 80 : 0);
        }
        stickX = (sbyte)Math.Clamp(x, -80, 80);
        stickY = (sbyte)Math.Clamp(y, -80, 80);
        return connected;
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape)) Exit();
        if (autoExitSeconds > 0 && gameTime.TotalGameTime.TotalSeconds > autoExitSeconds) Exit();
        Game.ViTick();
        audio.Pump();
        frame++;
        scriptTick = frame;
        base.Update(gameTime);
    }

    protected override void UnloadContent()
    {
        audio.Dispose();
        renderer?.Dispose();
        base.UnloadContent();
    }

    protected override void Draw(GameTime gameTime)
    {
        var cmds = Interlocked.Exchange(ref pendingFrame, null);
        if (cmds != null)
        {
            renderer!.Execute(cmds);
            drawnFrames++;
            if (shotDir != null && drawnFrames % shotEvery == 0 && shotsTaken < shotMax)
            {
                Directory.CreateDirectory(shotDir);
                renderer.SavePng(Path.Combine(shotDir, $"frame_{drawnFrames:D5}.png"));
                shotsTaken++;
            }
        }
        renderer!.Present(Window.ClientBounds.Width, Window.ClientBounds.Height);
        base.Draw(gameTime);
    }
}


