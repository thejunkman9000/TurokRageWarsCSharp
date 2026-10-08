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
    // [spec] wading (water over 5.12 deep), surface swim and deep swim: forward accel/max/decel, strafe accel/max/decel
    static readonly float[] Wade = [5.12f, 15.36f, 3.84f, 5.12f, 17.92f, 3.84f], Surface = [7.68f, 23.04f, 5.76f, 7.68f, 26.88f, 5.76f], Deep = [1.024f, 7.68f, 0.512f, 0f, 0f, 0.512f];
    const float WadeDepth = 5.12f, SwimDepth = 92.16f, DeepDepth = 184.32f;       // [assumed] depth thresholds: waist for swimming, over the head for deep swimming
    public const float LavaHit = 70f, MaxHealth = 100f;                            // [spec] lava hit = 70 hp; hit cadence [assumed] 1 per second
    const float TicksPerSecond = 15f;
    // [spec] jump impulse 389.12 (units per second here); allowed up to 11.25 ticks after leaving the ground; the impulse is re-applied for 3 ticks (variable height)
    const float JumpSpeed = 389.12f, CoyoteTicks = 11.25f, JumpHoldTicks = 3f;
    // [assumed]
    const float Gravity = 1100f, StepHeight = 48f, SnapDown = 24f, MaxFall = 3000f;

    public Vector3 Feet;
    public float Yaw, Pitch;                  // forward = (sin yaw cos pitch, sin pitch, cos yaw cos pitch)
    public bool Grounded;
    public bool NoClip;
    public float Health = MaxHealth, WaterDepth, DeadTimer, HurtFlash;
    public bool Dead => Health <= 0;
    public bool EyeUnderwater => WaterDepth > EyeHeight;
    float _lavaTimer;
    float _fwd, _strafe, _vy, _sinceGround, _jumpHeld;

    public Vector3 Eye => Feet + new Vector3(0, EyeHeight, 0);
    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));
    public float HorizontalSpeed => MathF.Sqrt(_fwd * _fwd + _strafe * _strafe) * TicksPerSecond;

    public void Respawn(Vector3 position, float yaw)
    {
        Feet = position; Yaw = yaw; Pitch = 0; _fwd = _strafe = _vy = 0; Grounded = false; _sinceGround = 0; Health = MaxHealth; DeadTimer = 0; WaterDepth = 0; _lavaTimer = 0;
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

    public void Hurt(float hp)
    {
        if (Dead) return;
        Health = MathF.Max(0, Health - hp); HurtFlash = 0.4f;
    }

    void Step(float dtSeconds, PlayerInput input, GameLevel level)
    {
        float dt = dtSeconds * TicksPerSecond;                                           // ticks
        if (Dead) { DeadTimer += dtSeconds; input = default; if (DeadTimer > 2f) Respawn(level.PlayerStart, level.PlayerFacing); }
        HurtFlash = MathF.Max(0, HurtFlash - dtSeconds);
        float? surface = Collision.SurfaceAbove(level.Water, Feet.X, Feet.Z, Feet.Y);
        WaterDepth = surface is float sy ? sy - Feet.Y : 0;
        bool swimming = !NoClip && WaterDepth > SwimDepth, deep = swimming && WaterDepth > DeepDepth;
        float[] cfg = deep ? Deep : swimming ? Surface : WaterDepth > WadeDepth ? Wade : [FAccel, FMax, FDecel, SAccel, SMax, SDecel];
        _fwd = Approach(_fwd, input.Forward, cfg[0], cfg[1], cfg[2], dt);
        _strafe = Approach(_strafe, deep ? 0 : input.Strafe, cfg[3], cfg[4], cfg[5], dt);
        float cap = MathF.Abs(_fwd) >= MathF.Abs(_strafe) ? cfg[1] : cfg[4];                 // combined vector clamp
        var move2 = new Vector2(_fwd, _strafe);
        if (cap > 0 && move2.Length() > cap) { move2 *= cap / move2.Length(); }

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

        // lava: standing on (or just above) a lava surface hurts
        if (Collision.SurfaceAbove(level.Lava, Feet.X, Feet.Z, Feet.Y - 6f) is float lavaY && Feet.Y - lavaY < 6f && !Dead)
        {
            _lavaTimer -= dtSeconds;
            if (_lavaTimer <= 0) { Hurt(LavaHit); _lavaTimer = 1f; }
        }
        else _lavaTimer = 0;

        if (swimming)
        {
            // [assumed] buoyancy: float with the head above the surface; space swims up; deep water follows the look pitch
            float target = surface!.Value - 0.85f * Height, vy;
            if (input.Jump) vy = 130f;
            else if (deep) vy = MathF.Sin(Pitch) * move2.X * TicksPerSecond * 0.9f + (target - Feet.Y) * 0.5f * (input.Forward == 0 ? 1f : 0f);
            else vy = Math.Clamp((target - Feet.Y) * 4f, -120f, 120f);
            _vy = vy; Grounded = false; _sinceGround = CoyoteTicks + 1; _jumpHeld = 1;
            float newY = Feet.Y + vy * dtSeconds;
            float? floor = Collision.GroundHeight(level, Feet, Radius, MathF.Max(Feet.Y, newY) + StepHeight * 0.1f);
            Feet.Y = floor is float fy && newY < fy ? fy : newY;
            return;
        }

        // jump
        if (input.Jump && WaterDepth <= WadeDepth)
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
