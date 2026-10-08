namespace Trw.Tools.Audio;

/// <summary>Big-endian read helpers over a byte array.</summary>
public static class Be
{
    public static int U8(byte[] d, int o) => d[o];
    public static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    public static short S16(byte[] d, int o) => unchecked((short)((d[o] << 8) | d[o + 1]));
    public static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    public static int S32(byte[] d, int o) => unchecked((int)U32(d, o));
}

/// <summary>
/// The game's generic section container: u32 count, u32 offset[count+1] (relative to container start).
/// Matches func_0028FD94(tbl, idx) = tbl + tbl[idx+1].
/// </summary>
public readonly struct Container
{
    public readonly byte[] Data;
    public readonly int Start;
    public Container(byte[] data, int start) { Data = data; Start = start; }
    public int Count => (int)Be.U32(Data, Start);
    public int SectionOffset(int i) => Start + (int)Be.U32(Data, Start + 4 + 4 * i);
    public int SectionEnd(int i) => Start + (int)Be.U32(Data, Start + 4 + 4 * (i + 1));
    public Container Sub(int i) => new Container(Data, SectionOffset(i));
}
