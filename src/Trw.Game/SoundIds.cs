namespace Trw.Walk;

/// <summary>Game sound ids, from docs/formats/sound_events.md. V = traced to a call site, H = hypothesis, G = a guess made here where the game has no cue we could find.</summary>
public static class SoundIds
{
    public const int PainTurok = 5005, DeathTurok = 5015;            // V: pain 5005 + voice offset (Turok = class 0, offset 0), death 5015 + offset
    public const int DeathThump = 530, Teleport = 560;                // V
    public const int WaterEnter = 730, WaterLeave = 734, Swim = 736;  // H: swim-state numbering is the C# project guess
    public const int BulletRounds = 3500, EnergyRounds = 3510, ExplosiveRounds = 3520, PowerUp = 3530, SpecialWeapon = 3596, AmmoPack = 3580;   // V (health pack: not found, uses the ammo pack cue = G)
    public const int NoAmmoClick = 3405;                              // V
    public const int ImpactWall = 1040, ImpactCharacter = 1060;       // V: bullet impact table (MAG 60 / assault rifle / minigun)
    public const int MuzzleFallback = 1110;                           // G: primary fire of MAG 60 / AR / minigun / shotgun has no sound record; the assault rifle alt-fire record is used so shots are audible
    public const int SelectMag60 = 2800, SelectShotgun = 2400, SelectAssault = 3400, SelectMinigun = 2600;   // V (weapon-order mapping of viewmodels is unverified)
    public const int MusicMenu = 52, MusicGameMin = 56, MusicGameCount = 4;      // V: gameplay = random 56..59, menu = 52
}
