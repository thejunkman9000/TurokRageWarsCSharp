using Trw.Platform;
using Xunit;

public class ViewTests
{
    static ViewTests() { if (!Mem.IsMapped(0x80000000)) Mem.Map(0x80000000, 0x1000); }

    [Fact]
    public void ScalarPointerViewsIndexByElementSize()
    {
        var p = new S16Ptr(0x800001A0);
        p.Set(3, -2);
        Assert.Equal((short)-2, Mem.S16(0x800001A6));
        Assert.Equal((short)-2, p[3]);
        Assert.Equal(0x800001A6u, (uint)p.Add(3));
        Assert.True(new F32Ptr(0).IsNull);
    }

    [Fact]
    public void NegativeIndexWrapsLikeGuestAddressArithmetic()
    {
        Mem.Set(0x800002A0, 7);
        Assert.Equal(7, new S32Ptr(0x800002A4)[-1]);
    }
}
