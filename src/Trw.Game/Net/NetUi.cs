using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Trw.Game.Net;

/// <summary>
/// The lobby screens (sign in / register, host, browse, the room) and the in-game status line: a small immediate-mode UI on SpriteBatch with
/// the NetFont SpriteFont. Input is gathered in Update (typed characters from the window's TextInput event, key and mouse edges) and used
/// by the widgets while they draw, on a 960x720 canvas scaled into the window. Tab moves between text boxes, Enter does the screen's main
/// action, Esc goes back (and quits from the first screen).
/// </summary>
public sealed class NetUi
{
    private const int W = 960, H = 720;
    private readonly TrwWindow win;
    private SpriteBatch? sb;
    private SpriteFont? font, title;      // the game's Peaceful Wars (Content/Fonts): regular for text, italic for headings
    private Texture2D? px;
    // input gathered by Update, used by the widgets in Draw
    private readonly Queue<char> typed = new();
    private bool click, tab, shiftTab, enter, esc, up, down;
    private Vector2 mouse;
    private KeyboardState prevKeys;
    private bool prevLeft;
    private float scale = 1; private Vector2 offset;
    // focus and per-draw bookkeeping
    private string focus = "";
    private readonly List<string> focusOrder = new();
    private Action? defaultAction;
    private NetScreen lastScreen = (NetScreen)(-1);
    private readonly List<(string text, long until)> toasts = new();

    private static readonly Color Back = new(18, 20, 26), Panel = new(34, 38, 48), Edge = new(80, 90, 110), Text = new(225, 228, 235),
        Dim = new(140, 148, 160), Accent = new(230, 150, 40), Good = new(110, 200, 120), Bad = new(235, 95, 85), Field = new(14, 15, 20);

    public NetUi(TrwWindow window)
    {
        win = window;
        win.Window.TextInput += (_, e) =>
        {
            if (e.Character == '\b' || (e.Character >= 32 && e.Character < 127)) lock (typed) typed.Enqueue(e.Character);
        };
    }

    private void Ensure(GraphicsDevice device)
    {
        if (sb != null) return;
        sb = new SpriteBatch(device);
        font = win.Content.Load<SpriteFont>("NetFont");
        title = win.Content.Load<SpriteFont>("NetTitle");
        px = new Texture2D(device, 1, 1);
        px.SetData(new[] { Color.White });
    }

    public void Toast(string s) { lock (toasts) toasts.Add((s, Environment.TickCount64 + 8000)); }

    // ------------------------------------------------------------------------------------------ input

    public void Update(NetFlow flow, GameTime time)
    {
        var ks = Keyboard.GetState();
        bool Edge(Keys k) => ks.IsKeyDown(k) && !prevKeys.IsKeyDown(k);
        bool shift = ks.IsKeyDown(Keys.LeftShift) || ks.IsKeyDown(Keys.RightShift);
        if (Edge(Keys.Tab)) { if (shift) shiftTab = true; else tab = true; }
        if (Edge(Keys.Enter) && !ks.IsKeyDown(Keys.LeftAlt) && !ks.IsKeyDown(Keys.RightAlt)) enter = true;
        if (Edge(Keys.Escape)) esc = true;
        if (Edge(Keys.Up)) up = true;
        if (Edge(Keys.Down)) down = true;
        prevKeys = ks;
        var ms = Mouse.GetState();
        bool left = ms.LeftButton == ButtonState.Pressed;
        if (left && !prevLeft && win.IsActive) { click = true; mouse = new Vector2((ms.X - offset.X) / scale, (ms.Y - offset.Y) / scale); }
        prevLeft = left;
        if (!left && !click) mouse = new Vector2((ms.X - offset.X) / scale, (ms.Y - offset.Y) / scale);

        if (esc)
        {
            esc = false;
            switch (flow.Screen)
            {
                case NetScreen.SignIn: win.Exit(); break;
                case NetScreen.Menu: flow.SignOut(); break;
                case NetScreen.HostSetup: case NetScreen.Browse: flow.Screen = NetScreen.Menu; flow.Status = ""; break;
                case NetScreen.Room: flow.LeaveRoom(); break;
            }
        }
    }

    // ------------------------------------------------------------------------------------------ widgets

    private void Fill(Rectangle r, Color c) => sb!.Draw(px!, r, c);
    private void Frame(Rectangle r, Color c) { Fill(new(r.X, r.Y, r.Width, 1), c); Fill(new(r.X, r.Bottom - 1, r.Width, 1), c); Fill(new(r.X, r.Y, 1, r.Height), c); Fill(new(r.Right - 1, r.Y, 1, r.Height), c); }
    private void Label(string s, float x, float y, Color c, float size = 1f) => sb!.DrawString(font!, Clean(s), new Vector2(x, y), c, 0, Vector2.Zero, size, SpriteEffects.None, 0);
    private float Width(string s, float size = 1f) => font!.MeasureString(Clean(s)).X * size;
    private static string Clean(string s) => new(s.Select(ch => ch >= 32 && ch < 127 ? ch : '?').ToArray());

    private bool Hit(Rectangle r) => click && r.Contains((int)mouse.X, (int)mouse.Y);

    private bool Button(Rectangle r, string text, bool enabled = true, bool primary = false)
    {
        bool hover = r.Contains((int)mouse.X, (int)mouse.Y) && enabled;
        Fill(r, !enabled ? Panel : primary ? (hover ? new Color(245, 170, 60) : Accent) : (hover ? new Color(60, 68, 86) : new Color(46, 52, 66)));
        Frame(r, enabled ? Edge : Panel);
        Label(text, r.X + (r.Width - Width(text)) / 2, r.Y + (r.Height - font!.LineSpacing) / 2f, enabled ? (primary ? Color.Black : Text) : Dim);
        if (enabled && Hit(r)) { click = false; return true; }
        return false;
    }

    private void TextBox(string id, string caption, Rectangle r, ref string value, bool masked = false, int maxLen = 40)
    {
        focusOrder.Add(id);
        if (Hit(r)) { focus = id; click = false; }
        bool focused = focus == id;
        Label(caption, r.X, r.Y - font!.LineSpacing - 2, Dim);
        Fill(r, Field);
        Frame(r, focused ? Accent : Edge);
        if (focused)
        {
            lock (typed)
                while (typed.Count > 0)
                {
                    char ch = typed.Dequeue();
                    if (ch == '\b') { if (value.Length > 0) value = value[..^1]; }
                    else if (value.Length < maxLen) value += ch;
                }
        }
        string shown = masked ? new string('*', value.Length) : value;
        while (shown.Length > 0 && Width(shown) > r.Width - 16) shown = shown[1..];
        Label(shown, r.X + 8, r.Y + (r.Height - font.LineSpacing) / 2f, Text);
        if (focused && Environment.TickCount64 / 500 % 2 == 0) Fill(new Rectangle(r.X + 9 + (int)Width(shown), r.Y + 6, 2, r.Height - 12), Accent);
    }

    // ------------------------------------------------------------------------------------------ screens

    public void Draw(GraphicsDevice device, NetFlow flow)
    {
        Ensure(device);
        var vp = device.Viewport;
        scale = Math.Min(vp.Width / (float)W, vp.Height / (float)H);
        offset = new Vector2((vp.Width - W * scale) / 2, (vp.Height - H * scale) / 2);
        device.Clear(Back);
        sb!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null, null,
            Microsoft.Xna.Framework.Matrix.CreateScale(scale) * Microsoft.Xna.Framework.Matrix.CreateTranslation(offset.X, offset.Y, 0));
        focusOrder.Clear(); defaultAction = null;
        if (flow.Screen != lastScreen) { lastScreen = flow.Screen; focus = ""; }

        sb.DrawString(title!, "TUROK: RAGE WARS", new Vector2(60, 34), Accent);
        Label("Online", 60 + title!.MeasureString("TUROK: RAGE WARS").X + 16, 50, Text);
        Fill(new Rectangle(60, 90, W - 120, 2), Edge);
        switch (flow.Screen)
        {
            case NetScreen.SignIn: SignInScreen(flow); break;
            case NetScreen.Menu: MenuScreen(flow); break;
            case NetScreen.HostSetup: HostScreen(flow); break;
            case NetScreen.Browse: BrowseScreen(flow); break;
            case NetScreen.Room: case NetScreen.InGame: RoomScreen(flow); break;
        }
        // focus keys: Tab / Shift+Tab cycle the text boxes, Enter runs the main action
        if (focusOrder.Count > 0 && (focus.Length == 0 || !focusOrder.Contains(focus))) focus = focusOrder[0];
        if ((tab || shiftTab) && focusOrder.Count > 0) focus = focusOrder[(focusOrder.IndexOf(focus) + (tab ? 1 : focusOrder.Count - 1)) % focusOrder.Count];
        if (enter && !flow.Busy) defaultAction?.Invoke();
        tab = shiftTab = enter = up = down = click = false;
        lock (typed) typed.Clear();

        var status = flow.Busy ? flow.Status + "  ..." : flow.Status;
        Fill(new Rectangle(0, H - 56, W, 56), Panel);
        Label(status, 60, H - 42, status.StartsWith("Signed") || status.StartsWith("Connected") ? Good : Text);
        Label("Esc: back", W - 60 - Width("Esc: back"), H - 42, Dim);
        sb!.End();
        Snapshot(device, flow);
    }

    // TRW_NET_SHOT=<dir>: saves each lobby screen once (after it has been up for a second) as net_<screen>.png, for docs and tests
    private static readonly string? shotDir = Environment.GetEnvironmentVariable("TRW_NET_SHOT");
    private readonly HashSet<NetScreen> shot = new();
    private int shownFrames;
    private NetScreen shotScreen = (NetScreen)(-1);
    private void Snapshot(GraphicsDevice device, NetFlow flow)
    {
        if (shotDir == null) return;
        shownFrames = flow.Screen == shotScreen ? shownFrames + 1 : 0;
        shotScreen = flow.Screen;
        if (shownFrames < 60 || !shot.Add(flow.Screen)) return;
        try
        {
            int w = device.PresentationParameters.BackBufferWidth, h = device.PresentationParameters.BackBufferHeight;
            var data = new Color[w * h];
            device.GetBackBufferData(data);
            using var tex = new Texture2D(device, w, h);
            tex.SetData(data);
            Directory.CreateDirectory(shotDir);
            using var fs = File.Create(Path.Combine(shotDir, $"net_{flow.Screen}.png"));
            tex.SaveAsPng(fs, w, h);
        }
        catch (Exception e) { Console.Error.WriteLine("[net] screenshot failed: " + e.Message); }
    }

    private void SignInScreen(NetFlow f)
    {
        Label("Sign in with your jsNetwork account (one account for every jsNetwork game).", 60, 120, Text);
        Label("Server: " + f.Master.Url + (MasterLink.Available ? "" : "  (not available in this build)"), 60, 150, Dim);
        TextBox("user", "Username", new Rectangle(60, 230, 420, 44), ref f.User, maxLen: 20);
        TextBox("pass", "Password", new Rectangle(60, 330, 420, 44), ref f.Pass, masked: true, maxLen: 64);
        if (focus.Length == 0 && f.User.Length > 0) focus = "pass";
        bool can = !f.Busy && MasterLink.Available;
        if (Button(new Rectangle(60, 410, 200, 48), "Sign in", can, primary: true)) f.SignIn(false);
        if (Button(new Rectangle(280, 410, 200, 48), "Create account", can)) f.SignIn(true);
        if (Button(new Rectangle(60, 480, 420, 44), "Quit")) win.Exit();
        Label("New here? Type a username (3-20 letters, digits, _ or -)", 60, 560, Dim);
        Label("and a password, then Create account.", 60, 586, Dim);
        defaultAction = () => f.SignIn(false);
    }

    private void MenuScreen(NetFlow f)
    {
        Label($"Signed in as {f.Me?.Name}", 60, 120, Text);
        if (Button(new Rectangle(60, 190, 360, 56), "Host a game", !f.Busy, primary: true)) f.HostSetup();
        if (Button(new Rectangle(60, 260, 360, 56), "Find a game", !f.Busy)) f.Browse();
        if (Button(new Rectangle(60, 330, 360, 48), "Sign out")) f.SignOut();
        if (Button(new Rectangle(60, 390, 360, 48), "Quit")) win.Exit();
        Label("Everyone boots the game together when the host starts.", 60, 470, Dim);
        Label("Host = player 1, the others follow in join order.", 60, 496, Dim);
        defaultAction = f.HostSetup;
    }

    private void HostScreen(NetFlow f)
    {
        Label("Host a game", 60, 120, Text);
        TextBox("name", "Game name", new Rectangle(60, 200, 480, 44), ref f.LobbyName, maxLen: 40);
        Label("Players", 60, 268, Dim);
        for (int n = 2; n <= 4; n++)
            if (Button(new Rectangle(60 + (n - 2) * 90, 294, 80, 44), n.ToString(), true, primary: f.MaxPlayers == n)) f.MaxPlayers = n;
        TextBox("pw", "Password (optional)", new Rectangle(60, 400, 480, 44), ref f.LobbyPass, masked: false, maxLen: 64);
        if (Button(new Rectangle(60, 470, 230, 48), "Create", !f.Busy, primary: true)) f.Host();
        if (Button(new Rectangle(310, 470, 230, 48), "Back")) { f.Screen = NetScreen.Menu; f.Status = ""; }
        defaultAction = f.Host;
    }

    private void BrowseScreen(NetFlow f)
    {
        Label("Games", 60, 110, Text);
        var list = new Rectangle(60, 140, W - 120, 360);
        Fill(list, Field); Frame(list, Edge);
        Label("Name", 76, 148, Dim); Label("Host", 440, 148, Dim); Label("Players", 660, 148, Dim);
        if (up && f.Selected > 0) f.Selected--;
        if (down && f.Selected < f.Lobbies.Count - 1) f.Selected++;
        for (int i = 0; i < Math.Min(f.Lobbies.Count, 10); i++)
        {
            var l = f.Lobbies[i];
            var row = new Rectangle(62, 176 + i * 32, W - 124, 30);
            if (Hit(row)) { f.Selected = i; click = false; }
            if (i == f.Selected) Fill(row, new Color(60, 52, 36));
            var c = l.Joinable ? Text : Dim;
            Label(l.Name + (l.HasPassword ? "  [password]" : ""), 76, row.Y + 3, c);
            Label(l.Host, 440, row.Y + 3, c);
            Label($"{l.Players}/{l.MaxPlayers}{(l.State == "playing" ? "  playing" : "")}", 660, row.Y + 3, c);
        }
        TextBox("jpw", "Game password (if it has one)", new Rectangle(60, 540, 360, 44), ref f.JoinPass, maxLen: 64);
        var sel = f.Selected >= 0 && f.Selected < f.Lobbies.Count ? f.Lobbies[f.Selected] : null;
        if (Button(new Rectangle(440, 540, 140, 44), "Join", sel != null && sel.Joinable && !f.Busy, primary: true)) f.Join(sel!);
        if (Button(new Rectangle(600, 540, 140, 44), "Refresh", !f.Busy)) f.Refresh();
        if (Button(new Rectangle(760, 540, 140, 44), "Back")) { f.Screen = NetScreen.Menu; f.Status = ""; }
        defaultAction = () => { if (sel != null && sel.Joinable) f.Join(sel); };
    }

    private void RoomScreen(NetFlow f)
    {
        var room = f.Room;
        if (room == null) return;
        sb!.DrawString(title!, Clean(room.LobbyName), new Vector2(60, 100), Text, 0, Vector2.Zero, 0.7f, SpriteEffects.None, 0);
        Label(room.IsHost ? "You are hosting." : "Waiting for the host to start.", 60, 146, Dim);
        var members = room.Members;
        var box = new Rectangle(60, 190, W - 120, 4 * 56 + 16);
        Fill(box, Field); Frame(box, Edge);
        for (int i = 0; i < room.MaxPlayers; i++)
        {
            int y = box.Y + 8 + i * 56;
            Label($"P{i + 1}", 80, y + 14, Accent, 1.2f);
            if (i >= members.Count) { Label("(open)", 150, y + 16, Dim); continue; }
            var m = members[i];
            bool me = m.MemberId == room.LocalMemberId;
            Label(m.Name + (i == 0 ? "  (host)" : "") + (me ? "  - you" : ""), 150, y + 16, Text);
            Label(m.Ready ? "READY" : "not ready", 600, y + 16, m.Ready ? Good : Bad);
            Label(i == 0 ? "" : m.PingMs < 0 ? "-" : $"{m.PingMs} ms", 760, y + 16, Dim);
            if (room.IsHost && m.Build.Length > 0 && m.Build != room.HostBuild) Label("different build: desync possible", 150, y + 38, Bad, 0.7f);
        }
        if (f.Screen == NetScreen.InGame) { Label("Starting...", 60, 470, Good); return; }
        if (room is NetHostRoom h)
        {
            bool all = members.Count >= 2 && members.All(m => m.Ready);
            if (Button(new Rectangle(60, 470, 240, 52), "Start", all, primary: true)) f.StartMatch();
            defaultAction = () => { if (all) f.StartMatch(); };
            Label($"Port {h.Port}. Input delay {f.Cfg.InputDelay} ticks.", 330, 486, Dim);
        }
        else
        {
            if (Button(new Rectangle(60, 470, 240, 52), f.LocalReady ? "Not ready" : "Ready", true, primary: !f.LocalReady)) f.ToggleReady();
            defaultAction = f.ToggleReady;
            if (room is NetClientRoom c) Label($"Connected {c.Via}." + (c.HostBuild.Length > 0 && f.Me != null && c.HostBuild != f.Me.Build ? "  The host runs a different build: desync possible." : ""), 330, 486, Dim);
        }
        if (Button(new Rectangle(60, 540, 240, 44), "Leave")) f.LeaveRoom();
    }

    // ------------------------------------------------------------------------------------------ in game

    /// <summary>The session status (waiting, desync, host gone) and recent notices, top left, over the game.</summary>
    public void DrawOverlay(GraphicsDevice device)
    {
        Ensure(device);
        var lines = new List<(string, Color)>();
        if (NetSession.Status.Length > 0) lines.Add((NetSession.Status, NetSession.Desynced ? Bad : Accent));
        lock (toasts)
        {
            toasts.RemoveAll(t => t.until < Environment.TickCount64);
            foreach (var t in toasts) if (t.text != NetSession.Status) lines.Add((t.text, Text));
        }
        if (Environment.GetEnvironmentVariable("TRW_NET_PERF") == "1") lines.Add(($"{NetPlay.TicksPerSecond:F1} ticks/s  tick {NetSession.Tick}", Dim));
        if (lines.Count == 0) return;
        sb!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        int y = 8;
        foreach (var (s, c) in lines)
        {
            var size = font!.MeasureString(Clean(s)) * 0.8f;
            Fill(new Rectangle(6, y - 2, (int)size.X + 12, (int)size.Y + 4), new Color(0, 0, 0, 170));
            Label(s, 12, y, c, 0.8f);
            y += (int)size.Y + 6;
        }
        sb!.End();
    }
}
