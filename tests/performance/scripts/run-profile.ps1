<#
  One measured k6 run: snapshot -> (process sampler + k6) -> snapshot, everything written under -OutDir (outside the repository).
    .\run-profile.ps1 -K6 C:\tools\k6.exe -Profile large -Fixture C:\scratch\fixture.json -OutDir C:\scratch\runs -Label large-run1
  Extra k6 environment (THINK, VUS, HOLD, DEEP_WOS, DEBUG) is passed through when set in the calling environment.
#>
param(
    [Parameter(Mandatory)] [string] $K6,
    [Parameter(Mandatory)] [string] $Profile,
    [Parameter(Mandatory)] [string] $Fixture,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Label = $Profile,
    [string] $BaseUrl = 'http://127.0.0.1:5199'
)

New-Item -ItemType Directory -Force $OutDir | Out-Null
$script = Join-Path $PSScriptRoot '..\k6\audit-timeline.js'
$stop = Join-Path $OutDir "$Label.stop"
Remove-Item $stop -ErrorAction SilentlyContinue

& (Join-Path $PSScriptRoot 'capture-snapshot.ps1') -Out (Join-Path $OutDir "$Label.before.json")

$sampler = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-File', (Join-Path $PSScriptRoot 'sample-processes.ps1'), '-Out', (Join-Path $OutDir "$Label.cpu.csv"), '-StopFile', $stop
$started = Get-Date

& $K6 run --quiet --no-color `
    -e "PROFILE=$Profile" -e "FIXTURE=$Fixture" -e "BASE_URL=$BaseUrl" -e "SUMMARY_OUT=$(Join-Path $OutDir "$Label.summary.json")" `
    -e "THINK=$(if ($null -ne $env:THINK) { $env:THINK } else { '0.5' })" `
    $script *> (Join-Path $OutDir "$Label.k6.log")
$exit = $LASTEXITCODE

New-Item -ItemType File -Force $stop | Out-Null
$sampler | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
& (Join-Path $PSScriptRoot 'capture-snapshot.ps1') -Out (Join-Path $OutDir "$Label.after.json")

[pscustomobject]@{ label = $Label; profile = $Profile; k6ExitCode = $exit; startedUtc = $started.ToUniversalTime().ToString('o'); finishedUtc = (Get-Date).ToUniversalTime().ToString('o') } |
    ConvertTo-Json | Set-Content -Path (Join-Path $OutDir "$Label.run.json") -Encoding utf8
Get-Content (Join-Path $OutDir "$Label.k6.log") -Tail 12
exit $exit
