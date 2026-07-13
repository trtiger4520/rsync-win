[CmdletBinding()]
param(
    [ValidateSet('smoke', 'full')]
    [string]$Profile = 'smoke',
    [ValidateRange(1, 100)]
    [int]$Iterations = 1,
    [ValidateSet('all', 'small-files', 'large-files', 'mixed-tree', 'delta', 'compressible', 'incompressible')]
    [string]$Scenario = 'small-files',
    [string]$Output = 'artifacts/perf/windows-practical'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts/perf/windows-work'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repo $Output))
$config = Join-Path $PSScriptRoot 'docker/rsyncd.conf'
$stockDockerfile = Join-Path $PSScriptRoot 'docker/Dockerfile.rsync'
$daemon = 'rsyncwin-perf-windows-daemon'
$stockImage = 'rsyncwin-perf-rsync:3.4.3'
$cli = Join-Path $repo 'src/RsyncWin.Cli/bin/Release/net10.0/rsyncwin.exe'

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

function Remove-Container([string]$Name) {
    docker rm -f $Name 2>$null | Out-Null
}

try {
    docker version --format '{{.Server.Version}}' | Out-Host
    dotnet build (Join-Path $PSScriptRoot 'RsyncWin.Perf.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'performance harness build failed' }
    dotnet build (Join-Path $repo 'src/RsyncWin.Cli/RsyncWin.Cli.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'RsyncWin CLI build failed' }
    docker build --tag $stockImage --file $stockDockerfile $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw 'stock rsync image build failed' }

    Remove-Container $daemon
    docker run --detach --name $daemon --cpus 4 --memory 8g --publish '127.0.0.1::873' `
        --mount "type=bind,source=$workRoot,target=/bench" `
        --mount "type=bind,source=$config,target=/etc/rsyncd.conf,readonly" `
        $stockImage rsync --daemon --no-detach --config=/etc/rsyncd.conf | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'rsync daemon failed to start' }

    $mapping = docker port $daemon 873/tcp
    if ($mapping -notmatch ':(?<port>\d+)\s*$') { throw "could not parse daemon port mapping: $mapping" }
    $port = $Matches.port
    $ready = $false
    foreach ($attempt in 1..60) {
        docker exec $daemon rsync 'rsync://localhost/' *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'rsync daemon did not become ready within 30 seconds' }
    $endpoint = "rsync://127.0.0.1:$port/data/{scenario}/"

    dotnet run --project (Join-Path $PSScriptRoot 'RsyncWin.Perf.csproj') -c Release --no-build -- benchmark `
        --profile $Profile --scenario $Scenario --warmups 1 --iterations $Iterations `
        --root $workRoot --output $outputRoot --timeout-seconds 300 --clients rsyncwin `
        --direct-executable $cli --direct-endpoint $endpoint
    if ($LASTEXITCODE -ne 0) { throw "benchmark failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Container $daemon
}
