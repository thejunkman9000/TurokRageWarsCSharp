using System.Text.RegularExpressions;

namespace Trw.Game;

/// <summary>
/// Scripted input for pads 1-3 (test runs of multiplayer matches): TRW_PAD2_SCRIPT, TRW_PAD3_SCRIPT, TRW_PAD4_SCRIPT drive the 2nd..4th controller
/// with the TRW_PAD_SCRIPT syntax subset "&lt;tick&gt;:BUTTON[+..][:hold]", "R&lt;screen&gt;+&lt;delay&gt;:BUTTON[:hold]" and "T&lt;state&gt;+&lt;delay&gt;:BUTTON[:hold]"
/// (ticks are 60 Hz window updates; R/T steps press once, &lt;delay&gt; ticks after the UI screen / game state became that id).
/// A pad with a script counts as connected for the whole run. Off (no effect) when the variables are not set.
/// </summary>
internal static class ExtraPadScripts
{
    private sealed class Step { public int Kind; public int Id, Delay, Start, Len; public ushort Buttons; public int Sx, Sy; public int FiredAt = -1; }   // Kind 0 absolute, 1 screen, 2 state
    private static readonly Step[]?[] scripts = Enumerable.Range(0, 4).Select(p => p == 0 ? null : Parse(Environment.GetEnvironmentVariable($"TRW_PAD{p + 1}_SCRIPT"))).ToArray();

    private static readonly int ConnectAt = int.TryParse(Environment.GetEnvironmentVariable("TRW_PADS_CONNECT_AT"), out var ca) ? ca : 0;
    public static bool Active(int pad) => pad > 0 && pad < 4 && scripts[pad] != null;

    private static Step[]? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var list = new List<Step>();
        foreach (var item in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split(':');
            if (p.Length < 2) continue;
            var st = new Step { Len = p.Length > 2 && int.TryParse(p[2], out var l) ? l : 6 };
            var m = Regex.Match(p[0], @"^([RT])(-?\d+)\+(\d+)$");
            if (m.Success) { st.Kind = m.Groups[1].Value == "R" ? 1 : 2; st.Id = int.Parse(m.Groups[2].Value); st.Delay = int.Parse(m.Groups[3].Value); }
            else if (int.TryParse(p[0], out var t)) st.Start = t;
            else continue;
            foreach (var name in p[1].Split('+'))
                switch (name.ToUpperInvariant())
                {
                    case "A": st.Buttons |= 0x8000; break; case "B": st.Buttons |= 0x4000; break; case "Z": st.Buttons |= 0x2000; break; case "START": st.Buttons |= 0x1000; break;
                    case "UP": st.Buttons |= 0x0800; break; case "DOWN": st.Buttons |= 0x0400; break; case "LEFT": st.Buttons |= 0x0200; break; case "RIGHT": st.Buttons |= 0x0100; break;
                    case "L": st.Buttons |= 0x0020; break; case "R": st.Buttons |= 0x0010; break;
                    case "CUP": st.Buttons |= 0x0008; break; case "CDOWN": st.Buttons |= 0x0004; break; case "CLEFT": st.Buttons |= 0x0002; break; case "CRIGHT": st.Buttons |= 0x0001; break;
                    case "SUP": st.Sy = 80; break; case "SDOWN": st.Sy = -80; break; case "SLEFT": st.Sx = -80; break; case "SRIGHT": st.Sx = 80; break;
                }
            list.Add(st);
        }
        return list.ToArray();
    }

    // TRW_PAD2_HUNT=1 .. TRW_PAD4_HUNT=1: during a match that pad turns towards the nearest other player, walks up to it and fires (test aid for
    // scoreboard checks: produces frags without hand-written aim). Actor fields: position f32 x/y/z at +0x08/+0x0C/+0x10, yaw f32 at +0x6C
    // (radians, not wrapped; forward = (-sin yaw, -cos yaw); stick right increases it), measured with TRW_PEEK while turning and walking.
    private static readonly bool[] hunt = Enumerable.Range(0, 4).Select(p => p > 0 && Environment.GetEnvironmentVariable($"TRW_PAD{p + 1}_HUNT") == "1").ToArray();

    private static readonly bool huntLog = Environment.GetEnvironmentVariable("TRW_MATCH_LOG") == "1";
    private static int lastHuntLog;
    private static readonly float[] huntX = new float[4], huntZ = new float[4];
    private static readonly int[] huntMoved = new int[4], huntStrafeUntil = new int[4], huntSide = new int[4];
    private static bool Hunt(int pad, ref ushort buttons, ref int x, ref int y)
    {
        if (!hunt[pad] || !MatchInfo.InMatch) return false;
        try
        {
            var ps = MatchInfo.Players();
            int me = ps.FindIndex(p => p.Port == pad);
            if (me < 0) return false;
            uint a = ps[me].Actor;
            float mx = Trw.Platform.Mem.F32(a + 8), mz = Trw.Platform.Mem.F32(a + 0x10), yaw = Trw.Platform.Mem.F32(a + 0x6C);
            float best = float.MaxValue, dx = 0, dz = 0;
            foreach (var p in ps)
            {
                if (p.Actor == a) continue;
                float ex = Trw.Platform.Mem.F32(p.Actor + 8) - mx, ez = Trw.Platform.Mem.F32(p.Actor + 0x10) - mz, d = ex * ex + ez * ez;
                if (d < best) { best = d; dx = ex; dz = ez; }
            }
            if (best == float.MaxValue) return false;
            float want = MathF.Atan2(-dx, -dz), err = want - yaw;
            err -= MathF.Round(err / (2 * MathF.PI)) * 2 * MathF.PI;
            if (huntLog && TrwWindow.scriptTick - lastHuntLog >= 120) { lastHuntLog = TrwWindow.scriptTick; Console.Error.WriteLine($"[hunt] pad {pad}: me ({mx:F0},{mz:F0}) yaw {yaw:F2}, target at distance {MathF.Sqrt(best):F0}, turn error {err:F2}"); }
            int sx = (int)Math.Clamp(err * 120f, -80f, 80f);
            if (MathF.Abs(err) > 0.02f && Math.Abs(sx) < 10) sx = Math.Sign(err) * 10;
            x += sx;
            // stuck (a wall between us): strafe for a while, alternating sides, so the walk slides along the wall
            int t = TrwWindow.scriptTick;
            if (MathF.Abs(mx - huntX[pad]) + MathF.Abs(mz - huntZ[pad]) > 8f || best < 300f * 300f) { huntX[pad] = mx; huntZ[pad] = mz; huntMoved[pad] = t; }
            if (t - huntMoved[pad] > 90 && t > huntStrafeUntil[pad]) { huntStrafeUntil[pad] = t + 150; huntSide[pad] ^= 1; huntMoved[pad] = t; }
            if (t < huntStrafeUntil[pad]) buttons |= (ushort)(huntSide[pad] == 0 ? 0x0002 : 0x0001);    // C-left / C-right
            if (MathF.Abs(err) < 0.3f && best > 300f * 300f) buttons |= 0x0008;         // C-up: walk towards the target
            if (MathF.Abs(err) < 0.07f && (t & 15) < 4) buttons |= 0x2000;                 // Z: fire in pulses once aimed
            return true;
        }
        catch (Trw.Platform.Mem.GuestFault) { return false; }
    }

    /// <summary>Adds the scripted buttons / stick of <paramref name="pad"/> (1..3) for this tick; true when the pad has a script (= connected).</summary>
    public static bool Read(int pad, ref ushort buttons, ref int x, ref int y)
    {
        if (!Active(pad)) return false;
        int tick = TrwWindow.scriptTick;
        if (tick < ConnectAt) return false;
        Hunt(pad, ref buttons, ref x, ref y);
        lock (scripts[pad]!)
            foreach (var st in scripts[pad]!)
            {
                int from;
                if (st.Kind == 0) from = st.Start;
                else
                {
                    if (st.FiredAt < 0 && (st.Kind == 1 ? Game.ScreenId == st.Id && Game.CurrentTick - Game.ScreenSince >= st.Delay
                                                        : Game.StateId == st.Id && Game.CurrentTick - Game.StateSince >= st.Delay))
                        st.FiredAt = tick;
                    from = st.FiredAt;
                    if (from < 0) continue;
                }
                if (tick >= from && tick < from + st.Len) { buttons |= st.Buttons; x += st.Sx; y += st.Sy; }
            }
        return true;
    }
}
