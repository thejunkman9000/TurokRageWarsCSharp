using Trw.Platform;
using Xunit;

public class MemTests
{
    static MemTests() { if (!Mem.IsMapped(0x80000000)) Mem.Map(0x80000000, 0x1000); }

    [Fact]
    public void WordsAreBigEndianInTheBackingStore()
    {
        Mem.Set(0x80000010, 0x11223344);
        Assert.Equal(0x11, Mem.U8(0x80000010));
        Assert.Equal(0x44, Mem.U8(0x80000013));
        Assert.Equal(0x1122, Mem.U16(0x80000010));
        Assert.Equal(0x11223344, Mem.S32(0x80000010));
    }

    [Fact]
    public void SignednessAndFloats()
    {
        Mem.Set(0x80000020, (byte)0xFF);
        Assert.Equal(-1, Mem.S8(0x80000020));
        Assert.Equal(255, Mem.U8(0x80000020));
        Mem.Set(0x80000024, 1.5f);
        Assert.Equal(0x3FC00000u, Mem.U32(0x80000024));
        Assert.Equal(1.5f, Mem.F32(0x80000024));
    }

    [Fact]
    public void UnmappedAccessFaultsWithTheAddress()
    {
        var e = Assert.Throws<Mem.GuestFault>(() => Mem.S32(0x80001000));
        Assert.Equal(0x80001000u, e.Address);
        Assert.Throws<Mem.GuestFault>(() => Mem.S32(0x12345678));
    }
}
