using Trw.Platform;

namespace Trw.Game;

/// <summary>Sets up the console's memory and starts the game's own boot thread (port of the C port's game_glue.c).</summary>
public static class GameBoot
{
    public const uint Ram = 0x80000000, RamSize = 0x800000;
    public const uint PagedStart = 0x800000;          // asset space: pages are loaded from the cartridge when first touched
    private const uint RegionList = 0x80103F88;       // the game's own list of paged regions: next@0, base page@4, pages@6, rom address@8

    /// <summary>Maps RAM, the low code/overlay/asset space and the host's helper regions.</summary>
    public static void MapMemory()
    {
        Mem.Map(Ram, (int)RamSize);
        Mem.MapPaged(0x00000000, 0x1000000, PagedStart, PageIn);
        Mem.MapPaged(0x01000000, 0x1000000, 0x01000000, PageIn);
        Str.Init();
        Mem.Map(0x7F000000, 0x1000000);       // guest stacks: osCreateThread threads in the first half, the boot thread in the second
    }

    /// <summary>
    /// The game maps resources at virtual addresses &gt;= 0x400000 through the N64 TLB and loads each 4 KB page from the cartridge when it is
    /// first touched. Here the page is looked up in the game's region list and copied from the ROM.
    /// </summary>
    private static bool PageIn(uint addr)
    {
        uint page = addr >> 12;
        uint r = RegionList;
        for (int guard = 0; r != 0 && guard < 4096; guard++)
        {
            uint basePage = Mem.U16(r + 4), pages = Mem.U16(r + 6);
            if (page >= basePage && page < basePage + pages)
            {
                uint rom = (Mem.U32(r + 8) & 0x0FFFFFFF) + ((page - basePage) << 12);
                var buf = new byte[0x1000];
                if (rom < Host.Rom.Length)
                    Array.Copy(Host.Rom, rom, buf, 0, (int)Math.Min(0x1000, Host.Rom.Length - rom));
                Mem.Write(page << 12, buf);
                return true;
            }
            r = Mem.U32(r);
        }
        Console.Error.WriteLine($"page-in: no region for {addr:X8}");
        return false;
    }

    /// <summary>
    /// ROM data to guest RAM. VA = 0x80000000 + (ROM - 0xC00) for the main image's data; overlay A: VA 0x400000 = ROM 0x16E000.
    /// The boot loader zero-fills RAM from D_800E8000 up (= .bss), so only the data below 0x800E8000 (ROM 0xE8C00) is initialised.
    /// </summary>
    public static void LoadGuestData()
    {
        var rom = Host.Rom;
        Mem.Write(Ram + 0xC7010 - 0xC00, rom.AsSpan(0xC7010, 0xE8C00 - 0xC7010));
        Mem.Write(0x400000 + 0x1BCDA0 - 0x16E000, rom.AsSpan(0x1BCDA0, 0x1EE000 - 0x1BCDA0));
        // two data blobs sit inside overlay A's code range (C port asm/1B94A0.s, asm/1B9DD0.s): the overlay code is translated, so they must be copied on their own.
        // D_44B4A0 is the template table func_0023EDF0 copies into the collision profiles (0x80103FF0..0x80104460, the flags word func_00243A80 gets as arg4
        // for every actor state); without it every profile stays 0 and actors walk through walls and never fall. D_44BDD0 is passed on by func_0026AC38.
        Mem.Write(0x400000 + 0x1B94A0 - 0x16E000, rom.AsSpan(0x1B94A0, 0x1B9A40 - 0x1B94A0));
        Mem.Write(0x400000 + 0x1B9DD0 - 0x16E000, rom.AsSpan(0x1B9DD0, 0x1B9FD0 - 0x1B9DD0));
        // Cold boot: the ROM entry point (func_00200400) calls func_002A81B0, which finds no "systembootdone" marker and sets D_8014D330 = 1. We start at the
        // boot thread instead, so without this func_002911E8 skips func_00444030, the initialiser of the whole settings block 0x801462C8..0x8014684C (Game Speed,
        // volumes, match / bot / item options): actors never moved, no items, wrong damage (C port 4675740, verified against Project64).
        Mem.Set(0x8014D330, 1);
        Mem.Set(0x80000300, 1);              // osTvType = NTSC
        Mem.Set(0x80000318, 0x800000);       // osMemSize
    }

    /// <summary>--selftest-math: the game's own libm (translated from the ROM) against the host's, on a few inputs.</summary>
    public static int SelfTestMath()
    {
        int bad = 0;
        var t = new Thread(() =>
        {
            Stack.Init(0x7F800000, 0x100000);
            Game.RegisterFunctions(); Game.RegisterRecompiled();
            foreach (float x in new[] { 0f, 0.1f, 0.5f, 1f, 1.5707964f, 2f, 3.1415927f, -0.7f, -2.5f, 6f, 100f })
            {
                float s = Game.sinf(x), c = Game.cosf(x);
                bool ok = Math.Abs(s - MathF.Sin(x)) < 1e-3 && Math.Abs(c - MathF.Cos(x)) < 1e-3;
                if (!ok) bad++;
                Console.WriteLine($"{(ok ? "ok " : "BAD")} x={x}: sinf={s} (host {MathF.Sin(x)})  cosf={c} (host {MathF.Cos(x)})");
            }
            foreach (float x in new[] { 0f, 1f, 2f, 9f, 0.25f })
            {
                float r = Game.sqrtf(x);
                bool ok = Math.Abs(r - MathF.Sqrt(x)) < 1e-5;
                if (!ok) bad++;
                Console.WriteLine($"{(ok ? "ok " : "BAD")} sqrtf({x}) = {r}");
            }
        });
        t.Start(); t.Join();
        Console.WriteLine(bad == 0 ? "math self test passed" : $"math self test: {bad} mismatches");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Connects the RSP audio tasks to the audio microcode emulation.</summary>
    private static void InstallAudio()
    {
        var hle = new AudioHle();
        Host.AudioTask = (addr, len) =>
        {
            hle.Run(addr, len);
        };
    }

    /// <summary>Starts the game's boot thread (the function the C port starts: sprintf, FPU setup, then the main loop).</summary>
    public static Thread Start()
    {
        Game.RegisterFunctions();
        Game.RegisterRecompiled();
        InstallAudio();
        var t = new Thread(() =>
        {
            Stack.Init(0x7F800000, 0x100000);
            try { Game.func_00293518(); }
            catch (Exception e) { Console.Error.WriteLine($"game thread ended: {e}"); }
        }, 4 << 20) { IsBackground = true, Name = "game boot" };
        t.Start();
        return t;
    }
}



