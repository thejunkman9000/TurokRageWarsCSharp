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
        Mem.Set(0x80000300, 1);              // osTvType = NTSC
        Mem.Set(0x80000318, 0x800000);       // osMemSize
    }

    /// <summary>Connects the RSP audio tasks to the audio microcode emulation.</summary>
    private static void InstallAudio()
    {
        var hle = new AudioHle();
        bool first = true;
        Host.AudioTask = (addr, len) =>
        {
            if (first)
            {
                first = false;
                // the volume options normally come from a controller pak save; without one the three bytes are zero (silence): use the defaults
                if (Mem.U8(0x801462E0) == 0 && Mem.U8(0x801462E1) == 0 && Mem.U8(0x801462E2) == 0)
                {
                    Mem.Set(0x801462E0, (byte)0xC0); Mem.Set(0x801462E1, (byte)0xC0); Mem.Set(0x801462E2, (byte)0x80);
                }
            }
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



