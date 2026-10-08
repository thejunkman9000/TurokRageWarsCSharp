using Microsoft.Xna.Framework;

namespace Trw.Walk;

/// <summary>Simple bot brain after docs/GAMEPLAY_SPEC.md section 7: detect the player (front 1536, side 921.6, rear 512 units, needs line of sight), chase to a desired range of
/// 600-780 units re-rolled every 12-15 ticks, strafe, shoot with a random aim error; otherwise wander. The real brain (weapon choice, health seeking, skill tables) is not reproduced.</summary>
public sealed partial class Bot
{
    // [spec] detection ranges; speeds are the ground movement maxima x the easy-bot time scale
    const float FrontRange = 1536f, SideRange = 921.6f, RearRange = 512f;
    const float WalkSpeed = 30.72f * 15f * 0.8f, Gravity = 1100f, StepHeight = 48f, SnapDown = 24f;
    const float ShotInterval = 0.5f, ShotDamage = 8f, AimErrorDegrees = 15f;        // [assumed] bot fire cadence; damage 10 x 0.8 (normal difficulty scale)
    static readonly Random Rng = new();

    public int Score;                             // frags (the bot killed the player)
    public bool EvFire;                           // one-frame event for the sound layer
    float _vy, _rangeTimer, _wanderTimer, _wanderYaw, _desiredRange = 700f, _strafeDir = 1f, _shotTimer = 1f;
    bool _moving;

    public void Reset() { Health = 100f; DeadTimer = 0; _deathClip = -1; Feet = Home; _vy = 0; _shotTimer = 1f; }

    /// <summary>Advances the brain and physics by dt seconds. Returns true when the bot shot at (and hit) the player.</summary>
    public void Update(float dt, GameLevel level, Player player)
    {
        EvFire = false;
        // [spec] Slow Time slows the others to x0.25, Petrify freezes them (75 ticks)
        if (player.Items.PowerUp == "Slow Time") dt *= 0.25f;
        else if (player.Items.PowerUp == "Petrify" && player.Items.PowerUpTicks > 75f) dt = 0f;
        _animClock += dt;
        if (Dead) { _deathClock += dt; return; }
        var toPlayer = new Vector3(player.Feet.X - Feet.X, 0, player.Feet.Z - Feet.Z);
        float dist = toPlayer.Length();
        bool sees = !player.Dead && !player.Invisible && CanSee(level, player, toPlayer, dist);
        float moveForward = 0, moveStrafe = 0;
        if (sees)
        {
            float want = MathF.Atan2(toPlayer.X, toPlayer.Z);
            Facing = TurnToward(Facing, want, 4f * dt);
            _rangeTimer -= dt * 15f;
            if (_rangeTimer <= 0) { _rangeTimer = 12 + (float)Rng.NextDouble() * 3; _desiredRange = 600 + (float)Rng.NextDouble() * 180; _strafeDir = Rng.Next(2) == 0 ? -1 : 1; }
            moveForward = dist > _desiredRange + 40 ? 1f : dist < _desiredRange - 40 ? -0.7f : 0f;
            moveStrafe = 0.5f * _strafeDir;                                          // strafe magnitude skill * 0.6 [assumed skill 0.8]
            _shotTimer -= dt;
            if (_shotTimer <= 0 && MathF.Abs(Wrap(want - Facing)) < 0.35f) { _shotTimer = ShotInterval; Shoot(level, player); }
        }
        else
        {
            _wanderTimer -= dt;
            if (_wanderTimer <= 0) { _wanderTimer = 2f + (float)Rng.NextDouble() * 3f; _wanderYaw = (float)(Rng.NextDouble() * MathF.PI * 2); if (Rng.Next(3) == 0) _wanderYaw = float.NaN; }   // sometimes stand still
            if (!float.IsNaN(_wanderYaw)) { Facing = TurnToward(Facing, _wanderYaw, 2f * dt); if (MathF.Abs(Wrap(_wanderYaw - Facing)) < 0.5f) moveForward = 0.5f; }
        }
        Move(dt, level, moveForward, moveStrafe);
        _moving = moveForward != 0 || moveStrafe != 0;
        _animClip = _moving ? Mesh.ClipByRole(moveForward < 0 ? "walk_back" : "walk") : -1;
    }

    bool CanSee(GameLevel level, Player player, Vector3 toPlayer, float dist)
    {
        if (dist > FrontRange) return false;
        float angle = MathF.Abs(Wrap(MathF.Atan2(toPlayer.X, toPlayer.Z) - Facing));
        float range = angle <= MathF.PI * 0.25f ? FrontRange : angle <= MathF.PI * 0.75f ? SideRange : RearRange;
        if (dist > range) return false;
        var from = Feet + new Vector3(0, 102.4f, 0); var to = player.Feet + new Vector3(0, 102.4f, 0);          // [spec] rays 102.4 above the feet
        var dir = to - from; float len = dir.Length(); if (len < 1f) return true; dir /= len;
        return Collision.RayLevel(level, from, dir, len) == null;
    }

    void Shoot(GameLevel level, Player player)
    {
        EvFire = true;
        var from = Feet + new Vector3(0, 140f, 0); var aim = player.Feet + new Vector3(0, 100f, 0);
        var dir = Vector3.Normalize(aim - from);
        float err = AimErrorDegrees * MathF.PI / 180f;                                // random-walk aim error [assumed uniform within +-15 deg]
        dir = Vector3.Transform(dir, Matrix.CreateRotationY((float)(Rng.NextDouble() * 2 - 1) * err) * Matrix.CreateRotationX((float)(Rng.NextDouble() * 2 - 1) * err * 0.5f));
        float limit = Collision.RayLevel(level, from, dir, 5000f) ?? 5000f;
        if (Collision.RayCylinder(from, dir, player.Feet, Player.Radius, Player.Height) is float d && d < limit)
        {
            bool wasAlive = !player.Dead;
            player.Hurt(ShotDamage, this);
            if (wasAlive && player.Dead) Score++;
        }
    }

    void Move(float dt, GameLevel level, float forward, float strafe)
    {
        var fwd = new Vector3(MathF.Sin(Facing), 0, MathF.Cos(Facing)); var right = new Vector3(-fwd.Z, 0, fwd.X) * -1f;
        var step = (fwd * forward + right * strafe) * WalkSpeed * dt;
        Feet.X += step.X; Feet.Z += step.Z;
        Collision.PushOutOfWalls(level, ref Feet, Radius, Height, StepHeight);
        float? ground = Collision.GroundHeight(level, Feet, Radius * 0.5f, Feet.Y + StepHeight);
        if (ground is float g && Feet.Y - g <= SnapDown && _vy <= 0) { Feet.Y = g; _vy = 0; }
        else
        {
            _vy -= Gravity * dt; Feet.Y += _vy * dt;
            if (ground is float g2 && Feet.Y <= g2) { Feet.Y = g2; _vy = 0; }
            if (Feet.Y < -6000f) Reset();
        }
    }

    static float Wrap(float a) { while (a > MathF.PI) a -= MathF.PI * 2; while (a < -MathF.PI) a += MathF.PI * 2; return a; }
    static float TurnToward(float from, float to, float maxStep) { float d = Wrap(to - from); return from + Math.Clamp(d, -maxStep, maxStep); }
}
