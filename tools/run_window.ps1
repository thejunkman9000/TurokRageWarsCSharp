# Runs the game window unattended (scripted input, periodic screenshots) and prints what it logged.
#   usage: tools\run_window.ps1 -Seconds 40 -ShotDir <dir> [-Every 30] [-Max 60] [-Pad "150:SDOWN:5,200:A:6"] [-Fast <speed multiplier>] [-Status] [-NoBuild]
# The pad script is "<tick>:<BUTTON>[+<BUTTON>...]:<ticks held>,..." (see TrwWindow.ParseScript); -Fast removes the 60 Hz throttle.
param([int]$Seconds = 40, [string]$ShotDir = "", [int]$Every = 30, [int]$Max = 60, [string]$Pad = "", [double]$Fast = 0, [switch]$Status, [switch]$NoBuild,
      [string]$Rom = "D:\Projects\TurokRageWars\baserom.us.v11.z64")
$repo = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { dotnet build (Join-Path $repo "src\Trw.Game") --nologo -v q | Select-String ' error ' | Select-Object -First 5 }
if ($ShotDir) {
    if (Test-Path $ShotDir) { [IO.Directory]::Delete($ShotDir, $true) }
    $env:TRW_SHOT_DIR = $ShotDir; $env:TRW_SHOT_EVERY = "$Every"; $env:TRW_SHOT_MAX = "$Max"
}
$env:TRW_EXIT_AFTER = "$Seconds"
if ($Pad) { $env:TRW_PAD_SCRIPT = $Pad }
if ($Fast -gt 0) { $env:TRW_FAST = "$Fast" }
if ($Status) { $env:TRW_STATUS = "1" }
$out = Join-Path $env:TEMP "win_out.txt"; $err = Join-Path $env:TEMP "win_err.txt"
$p = Start-Process dotnet -ArgumentList "run --project `"$repo\src\Trw.Game`" --no-build -- --rom `"$Rom`"" -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
if (-not $p.WaitForExit(($Seconds + 60) * 1000)) { Stop-Process $p -Force; Write-Host "(timed out)" }
Write-Host "--- stderr (non-status)"
Get-Content $err | Where-Object { $_ -notmatch '^\[status\]' } | Select-Object -First 40 | ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0, 400) } else { $_ } }
if ($Status) { Write-Host "--- status"; Get-Content $err | Where-Object { $_ -match '^\[status\]' } | Select-Object -Last 3 }
if ($ShotDir) { Write-Host "--- $((Get-ChildItem $ShotDir -ErrorAction SilentlyContinue | Measure-Object).Count) screenshots in $ShotDir" }
