using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Walk;

/// <summary>Tiny seven-segment number display drawn from rectangles (no font asset needed).</summary>
public static class Hud
{
    // bit 6 top, 5 top-left, 4 top-right, 3 middle, 2 bottom-left, 1 bottom-right, 0 bottom
    static readonly int[] Digits = [0b1110111, 0b0010010, 0b1011101, 0b1011011, 0b0111010, 0b1101011, 0b1101111, 0b1010010, 0b1111111, 0b1111011];

    /// <summary>Draws <paramref name="value"/> with its top-left corner at (x, y); returns the x after the last digit. Each digit is 5*unit wide, 9*unit tall.</summary>
    public static int Number(SpriteBatch sb, Texture2D white, int x, int y, int unit, int value, Color colour)
    {
        string s = Math.Max(0, value).ToString();
        foreach (char ch in s)
        {
            int bits = Digits[ch - '0'];
            void Seg(int bit, int sx, int sy, int w, int h) { if ((bits >> bit & 1) != 0) sb.Draw(white, new Rectangle(x + sx * unit, y + sy * unit, w * unit, h * unit), colour); }
            Seg(6 - 0, 1, 0, 3, 1); Seg(6 - 1, 0, 1, 1, 3); Seg(6 - 2, 4, 1, 1, 3); Seg(6 - 3, 1, 4, 3, 1); Seg(6 - 4, 0, 5, 1, 3); Seg(6 - 5, 4, 5, 1, 3); Seg(6 - 6, 1, 8, 3, 1);
            x += 6 * unit;
        }
        return x;
    }
}
