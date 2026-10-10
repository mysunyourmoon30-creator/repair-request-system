<#
  Samples CPU % (of one logical core x logical core count => machine %) and working set of the processes that matter during a
  k6 run: the API host, sqlservr and k6, every few seconds, into a CSV. Stops when the stop-file appears or -Seconds elapse.
    .\sample-processes.ps1 -Out C:\scratch\cpu.csv -StopFile C:\scratch\stop.flag
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $StopFile = '',
    [int] $IntervalSeconds = 5,
    [int] $Seconds = 900
)

$cores = [Environment]::ProcessorCount
$previous = @{}
"time_utc,name,pid,cpu_machine_pct,working_set_mb" | Set-Content -Path $Out -Encoding ascii
$deadline = (Get-Date).AddSeconds($Seconds)

function Targets {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='sqlservr.exe' OR Name='k6.exe'" | ForEach-Object {
        $candidate = $_
        $label = switch ($candidate.Name) {
            'sqlservr.exe' { 'sqlservr' }
            'k6.exe' { 'k6' }
            default { if ($candidate.CommandLine -like '*RepairRequest.Api.dll*') { 'api' } else { $null } }
        }
        if ($label) { [pscustomobject]@{ Label = $label; Id = $candidate.ProcessId } }
    }
}

while ((Get-Date) -lt $deadline -and -not ($StopFile -and (Test-Path $StopFile))) {
    $now = Get-Date
    foreach ($target in Targets) {
        $process = Get-Process -Id $target.Id -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        $cpu = $process.TotalProcessorTime.TotalSeconds
        $pct = ''
        if ($previous.ContainsKey($target.Id)) {
            $elapsed = ($now - $previous[$target.Id].Time).TotalSeconds
            if ($elapsed -gt 0) { $pct = [math]::Round((($cpu - $previous[$target.Id].Cpu) / $elapsed) / $cores * 100, 1) }
        }
        $previous[$target.Id] = @{ Time = $now; Cpu = $cpu }
        "{0:o},{1},{2},{3},{4}" -f $now.ToUniversalTime(), $target.Label, $target.Id, $pct, [math]::Round($process.WorkingSet64 / 1MB, 0) | Add-Content -Path $Out -Encoding ascii
    }
    Start-Sleep -Seconds $IntervalSeconds
}
