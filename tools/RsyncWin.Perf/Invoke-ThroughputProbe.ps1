<#
.SYNOPSIS
    Bulk-throughput probe: rsync vs rsyncwin (FDD/R2R/AOT) on large files, transfer-bound.

.DESCRIPTION
    The 6-op harness stages data host<->volume every iteration, which is impractical for multi-GiB
    sets over Docker's 9p bind mount. This probe instead generates incompressible source data
    DIRECTLY in the named volume (no host copy at all), then times repeated full-copy transfers per
    mode/client and verifies the destination content against the source. Because the source is many
    GiB, transfer time dwarfs the ~0.85 s `docker compose run` startup floor, so MiB/s reflects real
    throughput rather than process startup. Requires the rsyncwin-perf:<mode> and stock images
    (build them once via Invoke-ComposeBenchmark.ps1 -Skip... or let this script's -Build do it).
#>
[CmdletBinding()]
param(
    [int]$FileCount = 4,
    [int]$FileSizeMiB = 1024,                                   # 4 x 1 GiB = 4 GiB total
    [ValidateRange(1, 20)] [int]$Iterations = 3,
    [ValidateSet('fdd', 'r2r', 'aot')] [string[]]$Modes = @('fdd', 'r2r', 'aot'),
    [switch]$KeepStack
)

$ErrorActionPreference = 'Stop'
$dockerDir = Join-Path $PSScriptRoot 'docker'
$compose = Join-Path $dockerDir 'docker-compose.perf.yml'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/perf/compose-work'))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
$totalBytes = [int64]$FileCount * $FileSizeMiB * 1MB
$totalMiB = $totalBytes / 1MB

$env:COMPOSE_FILE = $compose
$env:STOCK_IMAGE = 'rsyncwin-perf-rsync:3.4.3'
$env:WORK_HOST_DIR = $workRoot
$env:RSYNCWIN_IMAGE = "rsyncwin-perf:$($Modes[0])"

function Invoke-Transfer([string]$service, [string]$destName) {
    # Fresh empty dest each run, then a full copy. Returns elapsed seconds (wall, incl. ~0.85s startup).
    docker exec perf-helper sh -c "D=/bench/tp-dst/$destName; rm -rf `"`$D`"; mkdir -p `"`$D`"; chmod -R 0777 /bench/tp-dst" | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    docker compose run --rm --no-deps --name perf-client $service -r rsync://daemon/data/tp-src/ "/bench/tp-dst/$destName" *> $null
    $sw.Stop()
    if ($LASTEXITCODE -ne 0) { throw "$service transfer failed (exit $LASTEXITCODE)" }
    return $sw.Elapsed.TotalSeconds
}

try {
    docker compose up -d daemon helper | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'daemon/helper failed to come up' }

    Write-Host "Generating $FileCount x $FileSizeMiB MiB incompressible source directly in the volume..." -ForegroundColor Cyan
    docker exec perf-helper sh -c "mkdir -p /bench/tp-src; for i in `$(seq 1 $FileCount); do dd if=/dev/urandom of=/bench/tp-src/f`$i bs=1M count=$FileSizeMiB status=none; done; chmod -R 0777 /bench/tp-src"
    if ($LASTEXITCODE -ne 0) { throw 'source generation failed' }
    $srcSum = (docker exec perf-helper sh -c 'cd /bench/tp-src && sha256sum * | sha256sum | cut -d" " -f1').Trim()
    Write-Host "Source manifest sha256=$srcSum ($([math]::Round($totalMiB)) MiB)" -ForegroundColor Cyan

    $ready = $false
    foreach ($attempt in 1..60) {
        docker exec perf-daemon rsync "rsync://localhost/" *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'rsync daemon did not become ready' }

    $results = [System.Collections.Generic.List[object]]::new()

    function Measure-Client([string]$label, [string]$service, [string]$destName) {
        Invoke-Transfer $service $destName | Out-Null   # warm-up (discarded)
        $sum = (docker exec perf-helper sh -c "cd /bench/tp-dst/$destName && sha256sum * | sha256sum | cut -d' ' -f1").Trim()
        $ok = $sum -eq $srcSum
        $secs = foreach ($i in 1..$Iterations) { Invoke-Transfer $service $destName }
        $median = ($secs | Sort-Object)[[math]::Floor($secs.Count / 2)]
        $mibps = [math]::Round($totalMiB / $median, 1)
        Write-Host ("  {0,-18} median {1,6:N2}s  {2,7} MiB/s  verify={3}" -f $label, $median, $mibps, ($(if ($ok) { 'OK' } else { 'MISMATCH' })))
        $results.Add([pscustomobject]@{ Label = $label; Client = $destName; MedianSec = [math]::Round($median, 2); MiBps = $mibps; Verified = $ok })
    }

    foreach ($mode in $Modes) {
        $env:RSYNCWIN_IMAGE = "rsyncwin-perf:$mode"
        Write-Host "=== mode: $mode ===" -ForegroundColor Green
        Measure-Client "rsync (stock)" 'client-rsync' 'rsync'
        Measure-Client "rsyncwin:$mode" 'client-rsyncwin' "rsyncwin-$mode"
    }

    Write-Host "`n=== Bulk throughput ($([math]::Round($totalMiB)) MiB incompressible, median of $Iterations) ===" -ForegroundColor Cyan
    $results | Format-Table Label, MedianSec, MiBps, Verified -AutoSize | Out-Host
    $rsyncMbps = ($results | Where-Object { $_.Client -eq 'rsync' } | Select-Object -First 1).MiBps
    if ($rsyncMbps) {
        Write-Host "Relative to stock rsync ($rsyncMbps MiB/s):"
        foreach ($mode in $Modes) {
            $w = ($results | Where-Object { $_.Client -eq "rsyncwin-$mode" } | Select-Object -First 1)
            if ($w) { Write-Host ("  rsyncwin:{0,-4} {1,6:N2}x  ({2} MiB/s)" -f $mode, ($w.MiBps / $rsyncMbps), $w.MiBps) }
        }
    }
    $results | Export-Csv -Path (Join-Path $workRoot '..\throughput-probe.csv') -NoTypeInformation -Encoding utf8
}
finally {
    docker compose rm -f -s perf-client *> $null
    if (-not $KeepStack) { docker compose down -v --remove-orphans *> $null }
}
exit 0
