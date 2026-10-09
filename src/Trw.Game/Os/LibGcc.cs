using Trw.Platform;

namespace Trw.Game;

/// <summary>libgcc's 64-bit integer routines (the originals are written in terms of hi/lo word pairs and a lookup table); the host has native 64-bit integers.</summary>
public static partial class Game
{
    public static long __divdi3(long a, long b) => b == 0 ? 0 : b == -1 ? unchecked(-a) : a / b;
    public static long __moddi3(long a, long b) => b == 0 ? 0 : b == -1 ? 0 : a % b;
    public static ulong __udivdi3(ulong a, ulong b) => b == 0 ? 0 : a / b;
    public static ulong __umoddi3(ulong a, ulong b) => b == 0 ? 0 : a % b;
}
