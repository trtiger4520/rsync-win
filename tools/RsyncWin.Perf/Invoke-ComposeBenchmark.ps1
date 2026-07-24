<#
.SYNOPSIS
    Fair RsyncWin-vs-rsync benchmark over docker compose, comparing FDD / R2R / AOT publish modes.

.DESCRIPTION
    Brings up a stock rsync 3.4.3 daemon and a root helper sharing a named volume (transfer stays
    inside the Docker Linux VM). For each requested publish mode it builds the rsyncwin image and
    runs the measurement harness (1 warm-up + N measured iterations, alternating clients). The
    harness stages data host<->volume through the helper so the timed transfer never crosses the
    Windows bind mount. Finally prints a side-by-side table (rsync baseline vs rsyncwin per mode).

    Data location is the named volume `work`; clients are pinned to cores 0-3 with 8 GiB, the
    daemon to cores 4-7, matching the P11 fair-track budget.
#>
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'full')] [string]$Profile = 'smoke',
    [ValidateRange(1, 100)] [int]$Iterations = 1,
    [ValidateSet('all', 'small-files', 'large-files', 'mixed-tree', 'delta', 'compressible', 'incompressible')]
    [string]$Scenario = 'small-files',
    [ValidateSet('fdd', 'r2r', 'aot')] [string[]]$Modes = @('fdd', 'r2r', 'aot'),
    [string]$Output = 'artifacts/perf/compose',
    [switch]$SkipBuild,
    [switch]$KeepStack
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dockerDir = Join-Path $PSScriptRoot 'docker'
$compose = Join-Path $dockerDir 'docker-compose.perf.yml'
$workRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/perf/compose-work'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repo $Output))
$perfProject = Join-Path $PSScriptRoot 'RsyncWin.Perf.csproj'
$stockDockerfile = Join-Path $dockerDir 'Dockerfile.rsync'
$stockImage = 'rsyncwin-perf-rsync:3.4.3'
$runtimeBase = 'mcr.microsoft.com/dotnet/runtime:10.0-alpine@sha256:036b39f319141abc97fb32652ecfa97294e8108840f807999a0d467f4f1118ab'
$alpineBase = 'alpine:3.21@sha256:48b0309ca019d89d40f670aa1bc06e426dc0931948452e8491e3d65087abc07d'

New-Item -ItemType Directory -Force -Path $workRoot, $outputRoot | Out-Null

# Compose validates the whole file (including ${RSYNCWIN_IMAGE:?}) on every invocation, so these
# must be set before any `docker compose` call — even `up -d daemon helper`.
# COMPOSE_FILE carries the file path via the environment so the harness command templates need no
# `-f "path"` argument — quotes in a template get doubled by `cmd /d /s /c` and break the .env lookup.
$env:COMPOSE_FILE = $compose
$env:STOCK_IMAGE = $stockImage
$env:WORK_HOST_DIR = $workRoot
$env:RSYNCWIN_IMAGE = "rsyncwin-perf:$($Modes[0])"

# Dest has no trailing slash: rsync treats it identically, and it sidesteps a LocalTreePruner
# containment-check bug that crashes rsyncwin --delete when the dest root ends in a separator.
$winTemplate = "docker compose run --rm --no-deps --name perf-client client-rsyncwin {flags} rsync://daemon/data/{scenario}/ /bench/results/rsyncwin/{scenario}"
$rsyncTemplate = "docker compose run --rm --no-deps --name perf-client client-rsync {flags} rsync://daemon/data/{scenario}/ /bench/results/rsync/{scenario}"

try {
    docker version --format '{{.Server.Version}}' | Out-Host

    if (-not $SkipBuild) {
        dotnet build $perfProject -c Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'performance harness build failed' }

        docker build --tag $stockImage --file $stockDockerfile $PSScriptRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'stock rsync image build failed' }
        $version = docker run --rm $stockImage rsync --version | Select-Object -First 1
        if ($version -notmatch 'version 3\.4\.3') { throw "Expected rsync 3.4.3, got: $version" }

        foreach ($mode in $Modes) {
            $base = if ($mode -eq 'aot') { $alpineBase } else { $runtimeBase }
            Write-Host "Building rsyncwin-perf:$mode (PUBLISH_MODE=$mode)" -ForegroundColor Cyan
            docker build --tag "rsyncwin-perf:$mode" --file (Join-Path $repo 'Dockerfile') `
                --build-arg "PUBLISH_MODE=$mode" --build-arg "FINAL_BASE=$base" $repo | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "rsyncwin image build failed for mode $mode" }
        }
    }

    docker compose -f $compose up -d daemon helper | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'daemon/helper failed to come up' }

    $ready = $false
    foreach ($attempt in 1..60) {
        docker exec perf-daemon rsync "rsync://localhost/" *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'rsync daemon did not become ready within 30 seconds' }

    foreach ($mode in $Modes) {
        $env:RSYNCWIN_IMAGE = "rsyncwin-perf:$mode"
        $modeOut = Join-Path $outputRoot $mode
        Write-Host "=== Benchmarking mode: $mode ===" -ForegroundColor Green
        dotnet run --project $perfProject -c Release --no-build -- benchmark `
            --profile $Profile --scenario $Scenario --warmups 1 --iterations $Iterations `
            --root $workRoot --output $modeOut --container perf-client --bridge-container perf-helper `
            --timeout-seconds 300 --rsyncwin-command $winTemplate --rsync-command $rsyncTemplate | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "benchmark for mode $mode failed with exit code $LASTEXITCODE" }
    }

    # ---- side-by-side aggregation ----
    $rows = foreach ($mode in $Modes) {
        Import-Csv (Join-Path $outputRoot "$mode/summary.csv") | ForEach-Object {
            [pscustomobject]@{
                Mode = $mode; Scenario = $_.scenario; Operation = $_.operation; Client = $_.client
                HasResult = $_.has_result; ElapsedMs = $_.elapsed_median_ms; MiBps = $_.throughput_median_mibps
                CpuMs = $_.cpu_median_ms; PeakRss = $_.peak_ws_median_bytes
            }
        }
    }

    $ops = $rows | Select-Object -ExpandProperty Operation -Unique
    $scenarios = $rows | Select-Object -ExpandProperty Scenario -Unique
    $lines = @('# RsyncWin vs rsync — publish-mode comparison', '',
        "Profile: $Profile | Iterations: $Iterations | Modes: $($Modes -join ', ')", '',
        '`elapsed median ms` (lower is better); `rss` = cgroup peak memory MiB. `n/a` = no manifest-verified result.', '')
    foreach ($sc in $scenarios) {
        $lines += "## $sc", ''
        $header = '| operation | rsync | ' + (($Modes | ForEach-Object { "rsyncwin:$_" }) -join ' | ') + ' | aot/rsync |'
        $sep = '|' + ('---|' * ($Modes.Count + 3))
        $lines += $header, $sep
        foreach ($op in $ops) {
            $rsync = ($rows | Where-Object { $_.Scenario -eq $sc -and $_.Operation -eq $op -and $_.Client -eq 'rsync' } | Select-Object -First 1)
            $cells = foreach ($mode in $Modes) {
                $w = ($rows | Where-Object { $_.Mode -eq $mode -and $_.Scenario -eq $sc -and $_.Operation -eq $op -and $_.Client -eq 'rsyncwin' } | Select-Object -First 1)
                if ($w -and $w.HasResult -eq 'True') { '{0:N0}' -f [double]$w.ElapsedMs } else { 'n/a' }
            }
            $rsyncCell = if ($rsync -and $rsync.HasResult -eq 'True') { '{0:N0}' -f [double]$rsync.ElapsedMs } else { 'n/a' }
            $aot = ($rows | Where-Object { $_.Mode -eq 'aot' -and $_.Scenario -eq $sc -and $_.Operation -eq $op -and $_.Client -eq 'rsyncwin' } | Select-Object -First 1)
            $ratio = if ($aot -and $aot.HasResult -eq 'True' -and $rsync -and $rsync.HasResult -eq 'True' -and [double]$rsync.ElapsedMs -gt 0) {
                '{0:N2}x' -f ([double]$aot.ElapsedMs / [double]$rsync.ElapsedMs)
            } else { 'n/a' }
            $lines += "| $op | $rsyncCell | " + ($cells -join ' | ') + " | $ratio |"
        }
        $lines += ''
    }

    $comparison = Join-Path $outputRoot 'comparison.md'
    $lines | Set-Content -Path $comparison -Encoding utf8
    $rows | Export-Csv -Path (Join-Path $outputRoot 'comparison.csv') -NoTypeInformation -Encoding utf8
    $lines | Out-Host
    Write-Host "`nWrote $comparison" -ForegroundColor Cyan
}
finally {
    docker compose -f $compose rm -f -s perf-client *> $null
    if (-not $KeepStack) {
        docker compose -f $compose down -v --remove-orphans *> $null
    }
}

# Reached only on success (an unhandled error propagates out of the finally above and exits non-zero
# first). Without this, the last native command in finally leaks its exit code as the script's.
exit 0
