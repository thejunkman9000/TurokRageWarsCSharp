using Trw.Platform;
using Xunit;

public class CpuTests
{
    static CpuTests() { if (!Mem.IsMapped(0x80000000)) Mem.Map(0x80000000, 0x1000); }

    // memory at 0x80000100: 11 22 33 44 55 66 77 88  (big-endian words 11223344, 55667788)
    static void Setup() { Mem.Set(0x80000100, 0x11223344u); Mem.Set(0x80000104, 0x55667788u); }

    [Theory]
    [InlineData(0u, 0x11223344u)]
    [InlineData(1u, 0x223344AAu)]
    [InlineData(2u, 0x3344AAAAu)]
    [InlineData(3u, 0x44AAAAAAu)]
    public void LwlLoadsTheLeftBytes(uint k, uint expected)
    {
        Setup();
        Assert.Equal(expected, Cpu.Lwl(0xAAAAAAAAu, 0x80000100 + k));
    }

    [Theory]
    [InlineData(0u, 0xAAAAAA11u)]
    [InlineData(1u, 0xAAAA1122u)]
    [InlineData(2u, 0xAA112233u)]
    [InlineData(3u, 0x11223344u)]
    public void LwrLoadsTheRightBytes(uint k, uint expected)
    {
        Setup();
        Assert.Equal(expected, Cpu.Lwr(0xAAAAAAAAu, 0x80000100 + k));
    }

    [Fact]
    public void UnalignedWordLoadIsLwlPlusLwr()
    {
        Setup();
        // lwl rt,0(a) ; lwr rt,3(a) with a = ...101 loads bytes 22 33 44 55
        uint a = 0x80000101;
        uint rt = Cpu.Lwl(0, a);
        rt = Cpu.Lwr(rt, a + 3);
        Assert.Equal(0x22334455u, rt);
    }

    [Fact]
    public void UnalignedWordStoreIsSwlPlusSwr()
    {
        Setup();
        uint a = 0x80000101;
        Cpu.Swl(0xDEADBEEFu, a);
        Cpu.Swr(0xDEADBEEFu, a + 3);
        Assert.Equal(0x11DEADBEu, Mem.U32(0x80000100));
        Assert.Equal(0xEF667788u, Mem.U32(0x80000104));
    }

    [Fact]
    public void FpuRegistersAreIndependent64BitValues()
    {
        Cpu.SetD(12, 1.5);
        Cpu.SetD(13, -2.0);
        Assert.Equal(1.5, Cpu.D(12));
        Assert.Equal(-2.0, Cpu.D(13));
        Assert.Equal(0x3FF8000000000000UL, Cpu.Fpr[12]);
    }

    [Fact]
    public void SingleIsTheLowWordAndKeepsTheHighWord()
    {
        Cpu.Fpr[5] = 0xAAAAAAAA_00000000UL;
        Cpu.SetS(5, 1.0f);
        Assert.Equal(1.0f, Cpu.S(5));
        Assert.Equal(0x3F800000u, Cpu.W(5));
        Assert.Equal(0xAAAAAAAA3F800000UL, Cpu.Fpr[5]);
        Cpu.SetW(5, 0x40000000u);
        Assert.Equal(2.0f, Cpu.S(5));
    }
    [Fact]
    public void SDivAvoidsTheMinValueOverflow() => Assert.Equal(int.MinValue, Cpu.SDiv(int.MinValue, -1));
}

