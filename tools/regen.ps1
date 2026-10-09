# Regenerates src/Trw.Game/Generated from the C port's sources and ROM-derived assembly.
#   pass 1   Trw.Transpile  -> signatures of every function / prototype
#   mips2cs                 -> C# for the functions that have no C source (recompiled from MIPS), plus their signatures
#   pass 2   Trw.Transpile  -> C# for every C function, with call sites coerced to the merged signatures
param([string]$CRoot = "D:\Projects\TurokRageWars")
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$tmp = Join-Path $env:TEMP "trw_regen"; New-Item -ItemType Directory -Force $tmp | Out-Null
$gen = Join-Path $repo "src\Trw.Game\Generated"

# list of C sources
$list = Join-Path $tmp "all.txt"
$files = foreach ($d in "game","ovl_a","libultra") { Get-ChildItem (Join-Path $CRoot "src\$d") -Filter *.c | ForEach-Object { "src/$d/" + $_.Name } }
# hand-ported native C of the C port: the RNC decompressors (their MIPS helpers share registers with their caller, which a per-function recompile cannot express)
$files = @($files) + @("port/native/rnc.c", "port/native/rnc2_gen.c")
# hand-checked replacements for functions whose decompiled C needs a behavioural fix (tools/overrides/<function>.c replaces <function>)
$ovDir = Join-Path $repo "tools\overrides"
if (Test-Path $ovDir) { $files = @($files) + @(Get-ChildItem $ovDir -Filter *.c | ForEach-Object { $_.FullName }) }
$files | Set-Content $list

dotnet build tools/Trw.Transpile --nologo -v q | Out-Null
# how many argument registers the ROM code of each function reads: the C prototypes can be shorter (a (void) stub that is only `jal f` forwards a0..a3)
python tools/arity.py --c-root $CRoot --out (Join-Path $tmp "arity.txt")
$common = @("--ast-cache", (Join-Path $env:TEMP "trw_ast"), "--root", $CRoot, "--syms", (Join-Path $CRoot "symbol_addrs.txt"), "--exclude", "tools/Trw.Transpile/shim_replaced.txt", "--override-dir", $ovDir, "--arity-in", (Join-Path $tmp "arity.txt"))

Write-Host "pass 1: signatures"
dotnet run --project tools/Trw.Transpile --no-build -- @common --sigs-out (Join-Path $tmp "sigs1.txt") --defs-out (Join-Path $tmp "defs.txt") --used-out (Join-Path $tmp "used.txt") --called-out (Join-Path $tmp "called.txt") --mips-out (Join-Path $tmp "needs_mips.txt") "@$list" | Select-Object -First 1

# functions the C declares void but whose callers use a value: the C draft cannot carry the value (it lingered in v0/f0 in the original), so
# these are recompiled from the MIPS. Result type = what the callers expect (float/double win over integers).
$sigs1Map = @{}
foreach ($l in Get-Content (Join-Path $tmp "sigs1.txt")) { if ($l) { $sigs1Map[($l -split '\|')[0]] = ($l -split '\|')[1] } }
$defs = @{}
foreach ($d in Get-Content (Join-Path $tmp "defs.txt")) { if ($d) { $defs[$d] = 1 } }
$usedBy = @{}
foreach ($l in Get-Content (Join-Path $tmp "used.txt")) {
    if (-not $l) { continue }
    $p = $l -split '\|'
    if ($p[1] -eq "void") { continue }
    if (-not $usedBy.ContainsKey($p[0]) -or ($p[1] -in "float","double")) { $usedBy[$p[0]] = $p[1] }
}
$voidUsed = foreach ($n in $usedBy.Keys) { if ($defs.ContainsKey($n) -and $sigs1Map[$n] -eq "void" -and $n -match '^(func_[0-9A-Fa-f]{8}|[A-Za-z_]\w*)$') { "$n|$($usedBy[$n])" } }
$voidFile = Join-Path $tmp "void_used.txt"
"# functions the C declares void but whose callers use a value (name|result type)" | Set-Content $voidFile
$voidUsed | Sort-Object | Add-Content $voidFile
Write-Host "void in C but used with a value: $(@($voidUsed).Count) functions -> recompiled from MIPS"
# C functions the transpiler cannot express (asm, statement expressions, ...): the exact MIPS code stands in for them
$needsMips = Get-Content (Join-Path $tmp "needs_mips.txt") | Where-Object { $_ }
foreach ($n in $needsMips) { "$n|" | Add-Content $voidFile }
Write-Host "untranslatable C: $(@($needsMips).Count) functions -> recompiled from MIPS"

$extra = Get-Content tools/mips2cs/extra_names.txt | Where-Object { $_ }
$rcDir = Join-Path $tmp "Recompiled"
$sigs1 = Join-Path $tmp "sigs1_plus.txt"
$m = @{}
foreach ($f in (Join-Path $tmp "sigs1.txt"), "tools/extra_sigs.txt") { foreach ($l in Get-Content $f) { if ($l) { $m[($l -split '\|')[0]] = $l } } }
$m.Values | Sort-Object | Set-Content $sigs1
$merged = Join-Path $tmp "sigs_merged.txt"

# MIPS recompile -> signatures -> pass 2. Pass 2 can find more C functions it cannot express (a call whose argument registers hold values the C cannot
# name); those are recompiled from the MIPS as well, so the two steps repeat until no new function turns up.
for ($round = 1; $round -le 5; $round++) {
    Write-Host "recompiling MIPS-only functions (round $round)"
    if (Test-Path $rcDir) { [IO.Directory]::Delete($rcDir, $true) }
    python tools/mips2cs/mips2cs.py --c-root $CRoot --sigs $sigs1 --out-dir $rcDir --sigs-out (Join-Path $tmp "sigs_rc.txt") --void-used $voidFile --used (Join-Path $tmp "used.txt") --called (Join-Path $tmp "called.txt") --all-asm --defs (Join-Path $tmp "defs.txt") --called-out (Join-Path $tmp "rc_called.txt") @extra
    foreach ($l in Get-Content (Join-Path $tmp "sigs_rc.txt")) { if ($l) { $m[($l -split '\|')[0]] = $l } }
    $m.Values | Sort-Object | Set-Content $merged

    Write-Host "pass 2: C# for the C functions"
    if (Test-Path $gen) { [IO.Directory]::Delete($gen, $true) }
    $needs2 = Join-Path $tmp "needs_mips2.txt"
    dotnet run --project tools/Trw.Transpile --no-build -- @common --exclude $voidFile --sigs-in $merged --out-dir $gen --mips-out $needs2 --report "@$list"
    $already = @{}
    foreach ($l in Get-Content $voidFile) { if ($l -and -not $l.StartsWith("#")) { $already[($l -split '\|')[0]] = 1 } }
    $new = @(Get-Content $needs2 | Where-Object { $_ -and -not $already.ContainsKey($_) })
    if ($new.Count -eq 0) { break }
    foreach ($n in $new) { "$n|" | Add-Content $voidFile }
    Write-Host "round ${round}: $($new.Count) more C functions need the MIPS"
}
Copy-Item $rcDir (Join-Path $gen "Recompiled") -Recurse
Copy-Item $merged (Join-Path $gen "sigs.txt")          # signatures of every function (differential tests, Trw.Game/Debug/DiffTest)

# stubs for the functions that are called but defined nowhere (the C port stubs these too)
& (Join-Path $PSScriptRoot 'make_stubs.ps1') -Gen $gen -Merged $merged -CalledFiles @((Join-Path $tmp 'called.txt'), (Join-Path $tmp 'rc_called.txt'))
Write-Host "done"






