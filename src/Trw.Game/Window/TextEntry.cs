using Microsoft.Xna.Framework.Input;
using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// Keyboard text entry for the game's NEW PLAYER name screen (Controller Pak screen, UI screen 19). The game itself has no keyboard: its name
/// entry is a letter cycler driven by pad buttons (overlay A, player record <c>O05Plr</c> of the lobby block <c>*D_800E54A4</c>, state 12):
/// D-pad up / down step the letter at the cursor through 0x20..0x5A (NameEntry_LetterUp / NameEntry_LetterDown), left / right move the cursor over the
/// 7 letter slots (NameEntry_EnterSlot2 / NameEntry_EnterSlot; a slot entered for the first time becomes 'A'), A is DONE (PlayerMenu_StepStateMachine: copies the slots,
/// trims trailing blanks, state 16) and B (NameEntry_Cancel) leaves the pak screen when it was opened from the 1-player menu (lobby phase 7),
/// otherwise blanks the letter.
/// Typed characters are turned into exactly those pad presses, closed-loop: the next press is chosen from the cursor and letter the game holds
/// in its RAM, so every field changes the way the game's own handlers change it. Backspace and space step the letter to the blank (B is not
/// used: it can leave the screen); Escape presses B only where B is the game's cancel (phase 7). While the screen is active the keyboard's
/// gameplay / menu bindings are off (keys held when it closes stay off until released); mouse and gamepads keep working.
/// Scripted typing for headless runs: TRW_TYPE_SCRIPT="&lt;delay&gt;:&lt;text&gt;,..." types each text &lt;delay&gt; ticks after the name entry opened
/// ({BS} = Backspace, {ENTER} = Enter, {ESC} = Escape, {COMMA} = ',').
/// </summary>
internal static class TextEntry
{
    private const uint LobbyPtr = 0x800E54A4;                                  // O05Lobby *D_800E54A4
    private const uint PlrOffset = 0x58, PlrStride = 0xB68;                   // O05Plr plr[4]
    private const uint OffState = 0x0, OffName = 0xB34, OffCursor = 0xB44;    // state, name[8 x {glyph, 0}], fB44 (cursor slot)
    private const uint OffPhase = 0x54;                                       // lobby h.f54: 7 = opened from the 1-player menu (B = back)
    private const int NameEntryState = 12, Slots = 7;
    private const byte FirstChar = 0x20, LastChar = 0x5A, CharCount = LastChar - FirstChar + 1;
    private const int Backspace = -1, Enter = -2, Cancel = -3;
    private const int HoldPolls = 1, SettlePolls = 12, ConfirmPolls = 40;

    private static readonly bool log = Environment.GetEnvironmentVariable("TRW_STATUS") != null;
    private static readonly object gate = new();
    private static readonly Queue<int> pending = new();       // characters, Backspace, Enter (window thread -> game thread)
    private static volatile bool active, cancelAllowed;
    private static int typed, activeSince;
    // press machine: hold the button HoldPolls polls, release it HoldPolls polls, then wait until the game's fields change (or SettlePolls) before the next decision
    private static ushort pressButton;
    private static int holdLeft, releaseLeft, settleLeft;
    private static (int state, int cursor, byte ch) snapshot;

    private static int Tick => global::Trw.Game.Game.CurrentTick;

    /// <summary>The name entry screen is open: the keyboard types into it.</summary>
    public static bool Active => active;

    // ---------------------------------------------------------------------------------------------- game thread

    private static bool Locate(out uint plr)
    {
        plr = 0;
        if (global::Trw.Game.Game.ScreenId != 19) return false;
        uint lobby = Mem.U32(LobbyPtr);
        if (lobby == 0 || !Mem.IsMapped(lobby) || !Mem.IsMapped(lobby + PlrOffset + PlrStride)) return false;
        plr = lobby + PlrOffset;                                               // pad 0 = player record 0
        int cur = Mem.S32(plr + OffCursor);
        return Mem.S32(plr + OffState) == NameEntryState && cur >= 0 && cur < Slots;
    }

    private static (int state, int cursor, byte ch) Read(uint plr)
    {
        int cur = Mem.S32(plr + OffCursor);
        return (Mem.S32(plr + OffState), cur, cur >= 0 && cur < 8 ? Mem.U8(plr + OffName + (uint)cur * 2) : (byte)0);
    }

    /// <summary>The buttons the name entry adds to pad 0 for this poll (called once per controller read of pad 0).</summary>
    public static ushort Poll()
    {
        bool now = Locate(out uint plr);
        if (now != active)
        {
            active = now;
            lock (gate) pending.Clear();
            typed = 0; pressButton = 0; holdLeft = releaseLeft = settleLeft = 0;
            if (now) activeSince = Tick;
            if (log) Console.Error.WriteLine($"[text] tick {Tick}: name entry {(now ? "opened" : "closed")}");
        }
        if (!active) return 0;
        cancelAllowed = Mem.S32(Mem.U32(LobbyPtr) + OffPhase) == 7;
        RunScript();
        if (holdLeft > 0) { holdLeft--; return pressButton; }
        if (releaseLeft > 0) { releaseLeft--; return 0; }                     // the game sees a release before the next press
        var s = Read(plr);
        if (settleLeft > 0 && s == snapshot) { settleLeft--; return 0; }
        settleLeft = 0;

        int act;
        lock (gate) { if (pending.Count == 0) return 0; act = pending.Peek(); }
        ushort press = Decide(act, s);
        if (press == 0 || act == Cancel) lock (gate) if (pending.Count > 0) pending.Dequeue();     // this action is complete
        if (press == 0) return 0;
        pressButton = press; holdLeft = HoldPolls - 1; releaseLeft = HoldPolls; snapshot = s; settleLeft = press == N64.A ? ConfirmPolls : SettlePolls;
        return press;
    }

    /// <summary>The next pad button for the action, or 0 when the game's fields already show it done.</summary>
    private static ushort Decide(int act, (int state, int cursor, byte ch) s)
    {
        if (act == Enter) return N64.A;                                        // DONE (repeated until the screen leaves state 12)
        if (act == Cancel) return N64.B;                                       // pressed once (Poll drops it)
        int slot = act == Backspace ? typed - 1 : typed;
        if (slot < 0 || slot >= Slots) return 0;                               // nothing to erase / name full
        if (s.cursor != slot) return s.cursor < slot ? N64.DRight : N64.DLeft;
        byte target = act == Backspace ? (byte)' ' : (byte)act;
        if (s.ch == target)
        {
            typed += act == Backspace ? -1 : 1;
            if (log) Console.Error.WriteLine($"[text] tick {Tick}: {(act == Backspace ? "erased" : $"typed '{(char)act}'")}, {typed} letters");
            return 0;
        }
        int cur = s.ch == 0 ? 'A' : s.ch;                                      // an empty slot counts as 'A' (the handlers fill it first)
        int up = ((target - cur) % CharCount + CharCount) % CharCount;
        return up <= CharCount - up ? N64.DUp : N64.DDown;
    }

    // ---------------------------------------------------------------------------------------------- input (window thread)

    /// <summary>A character from the window's text input: letters are upper-cased; characters the game does not have are dropped.</summary>
    public static void Type(char c)
    {
        if (!active) return;
        c = char.ToUpperInvariant(c);
        if (c < FirstChar || c > LastChar) return;
        lock (gate) pending.Enqueue(c);
    }

    public static void Erase() { if (active) lock (gate) pending.Enqueue(Backspace); }
    public static void Confirm() { if (active) lock (gate) pending.Enqueue(Enter); }
    public static void Back() { if (active && cancelAllowed) lock (gate) pending.Enqueue(Cancel); }

    private static volatile HashSet<Keys> swallowed = new();
    private static HashSet<Keys> heldAtOpen = new();
    private static bool wasActive, backDown, enterDown, escDown;
    private static double backRepeatAt;

    /// <summary>Window.TextInput: printable characters (shift and keyboard layout already applied); Backspace / Enter come from the key states.</summary>
    public static void OnTextInput(char c, Keys key)
    {
        if (char.IsControl(c) || heldAtOpen.Contains(key)) return;
        Type(c);
    }

    /// <summary>Once per window update: Backspace (with repeat), Enter and Escape from the key states, and the keys the gameplay mapping must not see.</summary>
    public static void Sample(KeyboardState ks, double seconds)
    {
        bool on = active;
        if (on && !wasActive) heldAtOpen = new HashSet<Keys>(ks.GetPressedKeys());   // a key held when the screen opened does not type
        else heldAtOpen.RemoveWhere(k => !ks.IsKeyDown(k));
        wasActive = on;
        // while typing every key is swallowed; when the screen closes the keys still held stay swallowed until released (the Enter that confirmed is not Start)
        var set = new HashSet<Keys>(on ? ks.GetPressedKeys() : swallowed.Where(ks.IsKeyDown));
        swallowed = set;
        bool back = ks.IsKeyDown(Keys.Back);
        if (back && (!backDown || seconds >= backRepeatAt)) { Erase(); backRepeatAt = seconds + (backDown ? 0.06 : 0.45); }
        backDown = back;
        bool enter = ks.IsKeyDown(Keys.Enter) && !ks.IsKeyDown(Keys.LeftAlt) && !ks.IsKeyDown(Keys.RightAlt);
        if (enter && !enterDown && !heldAtOpen.Contains(Keys.Enter)) Confirm();
        enterDown = enter;
        bool esc = ks.IsKeyDown(Keys.Escape);
        if (esc && !escDown && !heldAtOpen.Contains(Keys.Escape)) Back();
        escDown = esc;
    }

    /// <summary>The gameplay / menu mapping must not see this key (typing, or held since typing).</summary>
    public static bool Swallows(Keys key) => active || swallowed.Contains(key);

    // ---------------------------------------------------------------------------------------------- scripted typing

    private static readonly (int delay, string text)[] script = ParseScript(Environment.GetEnvironmentVariable("TRW_TYPE_SCRIPT"));
    private static int scriptIndex;

    private static (int, string)[] ParseScript(string? s)
    {
        var list = new List<(int, string)>();
        foreach (var item in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = item.IndexOf(':');
            if (colon <= 0 || !int.TryParse(item[..colon].Trim(), out var d)) continue;
            list.Add((d, item[(colon + 1)..]));
        }
        return list.ToArray();
    }

    private static void RunScript()
    {
        while (scriptIndex < script.Length && Tick - activeSince >= script[scriptIndex].delay)
        {
            var t = script[scriptIndex++].text;
            if (log) Console.Error.WriteLine($"[text] tick {Tick}: script types \"{t}\"");
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '{')
                {
                    int end = t.IndexOf('}', i);
                    switch (end > i ? t[(i + 1)..end].ToUpperInvariant() : "")
                    {
                        case "BS": Erase(); i = end; continue;
                        case "ENTER": Confirm(); i = end; continue;
                        case "ESC": Back(); i = end; continue;
                        case "COMMA": Type(','); i = end; continue;
                    }
                }
                Type(t[i]);
            }
        }
    }
}
