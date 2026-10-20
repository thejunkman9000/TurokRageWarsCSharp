# Android (tablets)

`src/Trw.Android` is a second head for the same game: the Trw.Game sources (everything but the desktop's `Program.cs`) compiled for
MonoGame's Android platform, plus an activity (ROM pick, lifecycle, logcat) and the on-screen controller (`src/Trw.Game/Window/TouchControls.cs`,
shared code that is off on the desktop). Landscape, made for tablets; phones work but the controls are sized for a big screen.

## Build

Needs the .NET SDK (9 or 10) with the Android workload, a JDK 17 and the Android SDK (platform 35, build tools):

```
dotnet workload install android
dotnet build src/Trw.Android -t:InstallAndroidDependencies -f net9.0-android -p:AndroidSdkDirectory=D:\Android\sdk -p:JavaSdkDirectory=D:\Android\jdk -p:AcceptAndroidSDKLicenses=True
```

(the second line downloads the JDK and the SDK into those folders; any existing SDK / JDK works too). Then:

```
set JAVA_HOME=D:\Android\jdk
set ANDROID_HOME=D:\Android\sdk
dotnet publish src/Trw.Android -c Release
```

gives `src/Trw.Android/bin/Release/net9.0-android/publish/io.github.thejunkman9000.turokragewars-Signed.apk` (arm64 + x86_64, about 40 MB,
signed with the SDK's debug key; about 9 minutes). Release builds compile all the translated code ahead of time with LLVM (Mono AOT,
`RunAOTCompilation` + `EnableLLVM`, no startup profile; `-p:EnableLLVM=false` builds in ~2 minutes with ~60 % of the speed); trimming only touches the framework (`AndroidLinkMode=SdkOnly`), the game assemblies stay whole. A Debug build (`-c Debug`) runs on the
JIT: fine for menus, but far too slow in a level.

For a faster emulator-only build: `dotnet build src/Trw.Android -c Release -p:RuntimeIdentifiers=android-x64 -p:EmbedAssembliesIntoApk=true`.

The desktop head is unchanged (`dotnet run --project src/Trw.Game`); the only project change it sees is that the MonoGame package reference moved
from Trw.Platform (which never used it) to Trw.Game.

## Install

`adb install -r io.github.thejunkman9000.turokragewars-Signed.apk`, or copy the APK to the tablet and open it (allow installing from that source).

## First run: the ROM

The game needs your own cartridge image (Turok: Rage Wars, USA, 8 MB; `.z64`, byte-swapped `.v64` / `.n64` are converted). On the first start the
app asks for it: **Choose ROM file** opens the system file picker; the file is checked (title, region) and copied into the app's private storage,
so the original can be deleted. It is never part of the APK.

For testing, a ROM in the app's external files folder is used as well (no picker):

```
adb push baserom.us.v11.z64 /sdcard/Android/data/io.github.thejunkman9000.turokragewars/files/
```

Saves (the Controller Pak `.mpk`, `settings.json`, `touch.json`) are in the app's private storage (`files/saves`, `files/TurokRageWars`).
Uninstalling the app or clearing its data removes them.

## Controls

Touch (multi-touch, semi-transparent, they fade out while a gamepad or keyboard is used and come back with the next touch):

| control | where | N64 | what |
|---|---|---|---|
| MOVE stick | left third; appears under the thumb | C buttons (free play), D-pad (menus) | walk, strafe; menu cursor |
| look | drag anywhere on the right part of the screen (free play; Minecraft style) | native look (like the desktop's mouse) | turn and tilt the view 1:1 |
| C stick | right half, left of the buttons, in menus; appears under the thumb, its knob shows a C | C buttons | the menus' C-button actions ("SKINS = C-up", "OPTIONS = C-up" ...) |
| AIM stick (option) | `"LookStick": true` in touch.json: the right stick stays in free play as the analogue stick instead of the drag look | analogue stick | aim / turn, N64 style |
| FIRE | bottom right, big | Z | fire; dragging from the button keeps aiming |
| JUMP | bottom right corner | R | jump |
| ALT / BACK | above JUMP | B | secondary fire; back in menus |
| WEAPON / OK | above FIRE | A | tap = next weapon, hold + drag on the right side = weapon wheel; confirm in menus |
| L | top of the right column | L | |
| START (pause icon) | top centre | Start | pause, title screen |
| system back | | Start in play, B in menus | |

A quick tap is held for a few frames so the game's controller poll always sees it; buttons vibrate briefly when pressed (`Haptics`).
`touch.json` (`files/TurokRageWars/touch.json`): `LookSensitivity` (1 = 0.11 degrees per pixel at 1080 pixels of screen height), `InvertY`,
`Size` (0.6-1.6), `Opacity` (0.2-1.5), `Haptics`.

Gamepads (Bluetooth / USB) work through MonoGame's Android gamepad support with the desktop's gamepad layout (left stick moves, right stick looks,
RT fires, A jumps, X/Y weapon, LT/B secondary, bumpers L/R, Start). Hardware keyboards use the desktop keyboard layout (no mouse look).

The input path is the desktop's: the touch state is merged into pad 0's controller read (`TrwWindow.ReadPad`) and the look into the native
look (`Host.ReadMouse`); the device in use is reported to `ActiveInput` (Touch, Xbox / PlayStation by the pad's name, KeyboardMouse), which
the HUD's button glyphs follow. On the desktop `TRW_TOUCH=1` turns the touch controls on with the mouse as one finger.

## Test switches

The desktop's `TRW_*` environment variables, through an intent extra or a file:

```
adb shell am start -n io.github.thejunkman9000.turokragewars/io.github.thejunkman9000.turokragewars.MainActivity --es env "'TRW_PERF=1;TRW_STATUS=1'"
```

or one `NAME=value` per line in `/sdcard/Android/data/io.github.thejunkman9000.turokragewars/files/trw_env.txt`. Console output goes to logcat,
tag `TRW` (`adb logcat -s TRW`). Useful ones: `TRW_PERF=1` (updates, draws and game frames per second every 5 s), `TRW_STATUS=1`,
`TRW_TOUCH_SENS=<x>`, `TRW_GLYPHS=...`, and `TRW_BENCH=<ticks>` with `TRW_DETERMINISTIC=1`: the desktop's headless oracle loop instead of the
window (ticks per second of the translated code on the device's CPU; with `TRW_ORACLE_OUT=<file>` it writes the same oracle file as the desktop).

## Measured (emulator, 2560 x 1600 tablet AVD, Android 14 x86_64, WHPX, host GPU; Ryzen 7 9800X3D host)

* Release (AOT): 60 window updates / 60 draws per second, the game at its own 30 frames per second in menus, the attract demo and a 1-player trial;
  level loads take about 1 s. Debug (JIT): about 15 game frames per second in a level, level loads of ~25 s.
* `TRW_BENCH=6000` (the desktop's deterministic check: arena 10, 400 ops per tick, 6000 ticks): LLVM AOT 538 ticks/s overall, ~400 ticks/s in
  the level (6-7x real time); Mono AOT without LLVM 317 / ~240 ticks/s. Desktop CoreCLR Release on the same machine: 626 ticks/s. The oracle
  output of the Android runs (both AOT flavours) is byte-identical to the desktop's reference (`base2_a.txt`): Mono computes exactly what
  CoreCLR computes.
* A modern ARM64 tablet core is roughly half to two thirds of this desktop core, so the expected margin there is ~3x real time; measure with
  `TRW_BENCH` on the device (not measured on real hardware yet).

## Known limits

* Netplay: built without the jsNetwork client (no accounts / lobbies); the direct-connection code is in but the online screens are not
  reachable from the Android activity (no command line).
* Name entry: no `TextInput` on MonoGame Android, so a hardware keyboard does not type names; the game's own letter cycler works with the controls.
* Hardware keyboards and gamepads are recognised by MonoGame only when the device reports a vendor / product id: adb-injected keys and the
  emulator's virtual keyboard are ignored (real Bluetooth / USB devices work). Gamepads and the multi-finger cases (move + look + fire at once,
  the weapon wheel by drag) were not exercised on the emulator (adb input is single-touch).
* No mouse capture / mouse look on Android.
* The 4:3 menu screens sit between the controls; on very narrow tablets (4:3 screens) the right-hand buttons overlap the menu's right edge.
* The APK is signed with the debug key; sign it with your own key for distribution (`AndroidKeyStore` properties).
