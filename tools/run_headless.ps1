# Runs the game headless for a few seconds and prints what it logged.   usage: tools\run_headless.ps1 [-Seconds 8]
param([int]$Seconds = 8, [string]$Rom = "D:\Projects\TurokRageWars\baserom.us.v11.z64", [switch]$NoBuild)
$repo = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { dotnet build (Join-Path $repo "src\Trw.Game") --nologo -v q | Select-String ' error ' | Select-Object -First 5 }
$out = Join-Path $env:TEMP "boot_out.txt"; $err = Join-Path $env:TEMP "boot_err.txt"
$p = Start-Process dotnet -ArgumentList "run --project `"$repo\src\Trw.Game`" --no-build -- --rom `"$Rom`" --headless --seconds $Seconds" -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
if (-not $p.WaitForExit(($Seconds + 40) * 1000)) { Stop-Process $p -Force; Write-Host "(timed out)" }
Write-Host "--- stdout"; Get-Content $out -TotalCount 30
Write-Host "--- stderr"; Get-Content $err -TotalCount 60
