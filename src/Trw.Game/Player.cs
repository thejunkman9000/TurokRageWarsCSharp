using Microsoft.Xna.Framework;

namespace Trw.Walk;

/// <summary>Player input for one frame: stick values in -1..1, buttons, and mouse look in radians.</summary>
public struct PlayerInput
{
    public float Forward, Strafe, LookYaw, LookPitch;
    public bool Jump;
}

/// <summary>First-person walking controller. Constants marked [spec] come from docs/GAMEPLAY_SPEC.md section 7 (verified in the game code); [assumed] ones are not in the
/// data (gravity, step height, the wall collision shape) and were chosen to feel plausible. One game tick is 1/15 s; speeds in the spec are units per tick.</summary>
public sealed class Player
{
    // [spec] size
    public const float Radius = 56.32f, Height = 184.32f, EyeHeight = 165.89f;
    // [spec] ground and air movement: forward accel/max/decel, strafe accel/max/decel (units per tick)
    const float FAccel = 10.24f, FMax = 30.72f, FDecel = 13.44f, SAccel = 10.24f, SMax = 35.84f, SDecel = 13.44f;
    const float TicksPerSecond = 15f;
    // [spec] jump impulse 389.12 (units per second here); allowed up to 11.25 ticks after leaving the ground; the impulse is re-applied for 3 ticks (variable height)
    const float JumpSpeed = 389.12f, CoyoteTicks = 11.25f, JumpHoldTicks = 3f;
    // [assumed]
    const float Gravity = 1100f, StepHeight = 48f, SnapDown = 24f, MaxFall = 3000f;

    public Vector3 Feet;
    public float Yaw, Pitch;                  // forward = (sin yaw cos pitch, sin pitch, cos yaw cos pitch)
    public bool Grounded;
    public bool NoClip;
    float _fwd, _strafe, _vy, _sinceGround, _jumpHeld;

    public Vector3 Eye => Feet + new Vector3(0, EyeHeight, 0);
    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));
    public float HorizontalSpeed => MathF.Sqrt(_fwd * _fwd + _strafe * _strafe) * TicksPerSecond;

    public void Respawn(Vector3 position, float yaw)
    {
        Feet = position; Yaw = yaw; Pitch = 0; _fwd = _strafe = _vy = 0; Grounded = false; _sinceGround = 0;
    }

    /// <summary>Advances the player by <paramref name="seconds"/> using fixed 1/60 s sub-steps.</summary>
    public void Update(float seconds, PlayerInput input, GameLevel level)
    {
        Yaw += input.LookYaw; Pitch = Math.Clamp(Pitch + input.LookPitch, -1.5f, 1.5f);
        const float step = 1f / 60f;
        for (float t = seconds; t > 0; t -= step) Step(MathF.Min(step, t), input, level);
    }

    static float Approach(float v, float stick, float accel, float max, float decel, float dt)
    {
        if (stick != 0)
        {
            float a = accel; if (v != 0 && MathF.Sign(v) != MathF.Sign(stick)) a *= 1.5f;      // reversing is quicker
            float lim = MathF.Abs(stick) * max, before = v;
            if (MathF.Abs(before) > lim) v = MathF.Sign(before) * MathF.Max(lim, MathF.Abs(before) - 0.5f * decel * dt);   // already over the limit (e.g. after a shove): decays at half the deceleration
            else v = Math.Clamp(before + stick * a * dt, -lim, lim);                           // otherwise accelerate, clamped to |stick| * max
        }
        else v -= MathF.Sign(v) * MathF.Min(MathF.Abs(v), decel * dt);
        return v;
    }

    void Step(float dtSeconds, PlayerInput input, GameLevel level)
    {
        float dt = dtSeconds * TicksPerSecond;                                           // ticks
        _fwd = Approach(_fwd, input.Forward, FAccel, FMax, FDecel, dt);
        _strafe = Approach(_strafe, input.Strafe, SAccel, SMax, SDecel, dt);
        float cap = MathF.Abs(_fwd) >= MathF.Abs(_strafe) ? FMax : SMax;                 // combined vector clamp
        var move2 = new Vector2(_fwd, _strafe);
        if (move2.Length() > cap) { move2 *= cap / move2.Length(); }

        var fwd = new Vector3(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
        var right = new Vector3(-MathF.Cos(Yaw), 0, MathF.Sin(Yaw));                     // screen right for forward = (sin yaw, 0, cos yaw) in XNA's right-handed view: cross(forward, up)
        var delta = (fwd * move2.X + right * move2.Y) * dt;

        if (NoClip)
        {
            Feet += Forward * (input.Forward * 600f * dtSeconds) + right * (input.Strafe * 600f * dtSeconds) + Vector3.Up * ((input.Jump ? 1f : 0f) * 400f * dtSeconds);
            return;
        }

        // horizontal move with wall push-out
        Feet.X += delta.X; Feet.Z += delta.Z;
        Collision.PushOutOfWalls(level, ref Feet, Radius, Height, StepHeight);
        Collision.PushOutOfObjects(level, ref Feet, Radius, Height, StepHeight);

        // jump
        if (input.Jump)
        {
            if (_sinceGround <= CoyoteTicks && _jumpHeld == 0) { _vy = JumpSpeed; _jumpHeld = dt; _sinceGround = CoyoteTicks + 1; Grounded = false; }
            else if (_jumpHeld > 0 && _jumpHeld < JumpHoldTicks) { _vy = JumpSpeed; _jumpHeld += dt; }
        }
        else _jumpHeld = 0;

        // vertical
        float? ground = Collision.GroundHeight(level, Feet, Radius, Feet.Y + StepHeight);
        if (Grounded && _vy <= 0)
        {
            if (ground is float g && Feet.Y - g <= SnapDown) { Feet.Y = g; _vy = 0; _sinceGround = 0; }        // stay glued to ramps and steps
            else Grounded = false;
        }
        if (!Grounded)
        {
            _vy = MathF.Max(-MaxFall, _vy - Gravity * dtSeconds);
            float newY = Feet.Y + _vy * dtSeconds;
            if (_vy > 0 && Collision.CeilingHeight(level, Feet, Radius, Feet.Y + Height) is float c && newY + Height > c) { newY = c - Height; _vy = 0; }
            float? g2 = Collision.GroundHeight(level, new Vector3(Feet.X, Feet.Y, Feet.Z), Radius, MathF.Max(Feet.Y, newY) + StepHeight * 0.1f);
            if (_vy <= 0 && g2 is float gg && newY <= gg + 0.01f) { Feet.Y = gg; _vy = 0; Grounded = true; _sinceGround = 0; }
            else { Feet.Y = newY; _sinceGround += dt; }
        }
        if (Feet.Y < -6000f) Respawn(level.PlayerStart, level.PlayerFacing);
    }
}
