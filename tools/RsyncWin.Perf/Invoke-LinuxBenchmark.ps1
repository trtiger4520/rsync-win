[CmdletBinding()]
param(
    [ValidateSet('smoke', 'full')]
    [string]$Profile = 'smoke',
    [ValidateRange(1, 100)]
    [int]$Iterations = 1,
    [ValidateSet('all', 'small-files', 'large-files', 'mixed-tree', 'delta', 'compressible', 'incompressible')]
    [string]$Scenario = 'all',
    [string]$Output = 'artifacts/perf/linux-smoke'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts/perf/linux-work'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repo $Output))
$config = Join-Path $PSScriptRoot 'docker/rsyncd.conf'
$stockDockerfile = Join-Path $PSScriptRoot 'docker/Dockerfile.rsync'
$network = 'rsyncwin-perf-net'
$daemon = 'rsyncwin-perf-daemon'
$client = 'rsyncwin-perf-client'
$rsyncWinImage = 'rsyncwin-p11:local'
$stockImage = 'rsyncwin-perf-rsync:3.4.3'

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

function Remove-Container([string]$Name) {
    docker rm -f $Name 2>$null | Out-Null
}

try {
    docker version --format '{{.Server.Version}}' | Out-Host
    dotnet build (Join-Path $PSScriptRoot 'RsyncWin.Perf.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'performance harness build failed' }
    docker build --tag $rsyncWinImage --file (Join-Path $repo 'Dockerfile') $repo
    if ($LASTEXITCODE -ne 0) { throw 'RsyncWin image build failed' }
    docker build --tag $stockImage --file $stockDockerfile $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw 'stock rsync image build failed' }

    $version = docker run --rm $stockImage rsync --version | Select-Object -First 1
    if ($version -notmatch 'version 3\.4\.3') {
        throw "Expected rsync 3.4.3, got: $version"
    }

    docker network inspect $network *> $null
    if ($LASTEXITCODE -ne 0) {
        docker network create $network | Out-Null
    }

    Remove-Container $daemon
    docker run --detach --name $daemon --network $network --cpus 4 --memory 8g `
        --mount "type=bind,source=$workRoot,target=/bench" `
        --mount "type=bind,source=$config,target=/etc/rsyncd.conf,readonly" `
        $stockImage rsync --daemon --no-detach --config=/etc/rsyncd.conf | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'rsync daemon failed to start' }

    $ready = $false
    foreach ($attempt in 1..60) {
        docker exec $daemon rsync "rsync://localhost/" *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'rsync daemon did not become ready within 30 seconds' }

    $mount = '--mount "type=bind,source=' + $workRoot + ',target=/bench"'
    $common = '--rm --name ' + $client + ' --network ' + $network + ' --cpus 4 --memory 8g ' + $mount
    $rsyncWinCommand = 'docker run ' + $common + ' ' + $rsyncWinImage + ' {flags} rsync://' + $daemon + '/data/{scenario}/ /bench/results/rsyncwin/{scenario}/'
    $rsyncCommand = 'docker run ' + $common + ' ' + $stockImage + ' rsync {flags} rsync://' + $daemon + '/data/{scenario}/ /bench/results/rsync/{scenario}/'

    dotnet run --project (Join-Path $PSScriptRoot 'RsyncWin.Perf.csproj') -c Release --no-build -- benchmark `
        --profile $Profile --scenario $Scenario --warmups 1 --iterations $Iterations `
        --root $workRoot --output $outputRoot --container $client --timeout-seconds 300 `
        --rsyncwin-command $rsyncWinCommand --rsync-command $rsyncCommand
    if ($LASTEXITCODE -ne 0) { throw "benchmark failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Container $client
    Remove-Container $daemon
    docker network rm $network 2>$null | Out-Null
}
