[CmdletBinding()]
param(
    [ValidateSet('Smoke', 'Full', 'Guard')]
    [string]$Profile = 'Smoke',

    [Alias('Peer')]
    [string[]]$PeerId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'test-fixtures/interop/peer-matrix.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

function Invoke-Captured {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    $output = & $FilePath @ArgumentList 2>&1 | Out-String
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output.TrimEnd()
    }
}

function Invoke-Streaming {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Assert-DockerReady {
    if ($null -eq (Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue)) {
        throw 'Docker Desktop is not running'
    }

    $probe = Invoke-Captured docker @('info', '--format', '{{.ServerVersion}}')
    if ($probe.ExitCode -ne 0) {
        throw "Docker daemon is not reachable: $($probe.Output)"
    }

    Write-Host "Docker Desktop ready, server $($probe.Output.Trim())"
}

function Get-SelectedPeers {
    if ($null -eq $PeerId -or $PeerId.Count -eq 0) {
        return @($manifest.peers)
    }

    $selected = foreach ($id in $PeerId) {
        $peer = @($manifest.peers) | Where-Object { $_.id -eq $id }
        if ($null -eq $peer) {
            throw "Unknown peer '$id'. Available peers: $($manifest.peers.id -join ', ')"
        }
        $peer
    }
    @($selected)
}

function Build-AndProbePeer {
    param(
        [Parameter(Mandatory)]
        $Peer
    )

    $dockerfile = Join-Path $repoRoot $manifest.dockerfile
    $context = Join-Path $repoRoot 'test-fixtures/interop'
    $packageVersion = if ($Peer.PSObject.Properties.Name -contains 'packageVersion') {
        [string]$Peer.packageVersion
    } else {
        ''
    }
    $pinDescription = if ([string]::IsNullOrWhiteSpace($packageVersion)) {
        "pinned source $($Peer.sourceSha256)"
    } else {
        "pinned package $packageVersion"
    }
    Write-Host "Building $($Peer.id) from $pinDescription"
    Invoke-Streaming docker @(
        'build',
        '--build-arg', "BASE_IMAGE=$($manifest.baseImage)",
        '--build-arg', "RSYNC_VERSION=$($Peer.version)",
        '--build-arg', "RSYNC_SHA256=$($Peer.sourceSha256)",
        '--build-arg', "RSYNC_PACKAGE=$packageVersion",
        '--tag', $Peer.image,
        '--file', $dockerfile,
        $context
    )

    $versionProbe = Invoke-Captured docker @('run', '--rm', $Peer.image, 'rsync', '--version')
    if ($versionProbe.ExitCode -ne 0) {
        throw "Could not probe $($Peer.image): $($versionProbe.Output)"
    }

    $versionLine = ($versionProbe.Output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
    $expected = "rsync  version $($Peer.version) "
    if (-not $versionLine.StartsWith($expected, [StringComparison]::Ordinal)) {
        throw "Peer $($Peer.id) reported '$versionLine', expected '$expected'"
    }

    $imageProbe = Invoke-Captured docker @('image', 'inspect', $Peer.image, '--format', '{{.Id}}')
    if ($imageProbe.ExitCode -ne 0) {
        throw "Could not inspect $($Peer.image): $($imageProbe.Output)"
    }

    [pscustomobject]@{
        VersionLine = $versionLine
        ImageId = $imageProbe.Output.Trim()
        SourceUrl = [string]$Peer.sourceUrl
        SourceSha256 = [string]$Peer.sourceSha256
        PackageVersion = $packageVersion
    }
}

function Get-TestFilter {
    switch ($Profile) {
        'Smoke' { return 'Category=Interop&Profile=Smoke' }
        'Guard' { return 'Category=Interop&Profile=Guard' }
        'Full' { return 'Category=Interop' }
    }
    throw "Unsupported profile $Profile"
}

Assert-DockerReady
$peers = Get-SelectedPeers
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $repoRoot "artifacts/live-interop/$stamp"
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$filter = Get-TestFilter
$results = [System.Collections.Generic.List[object]]::new()

foreach ($peer in $peers) {
    $peerRoot = Join-Path $runRoot $peer.id
    New-Item -ItemType Directory -Force -Path $peerRoot | Out-Null
    $started = Get-Date
    $exitCode = 1
    $probe = $null
    $previousPeer = $env:RSYNCWIN_INTEROP_PEER
    $previousArtifacts = $env:RSYNCWIN_INTEROP_ARTIFACTS

    try {
        $probe = Build-AndProbePeer $peer
        $env:RSYNCWIN_INTEROP_PEER = $peer.id
        $env:RSYNCWIN_INTEROP_ARTIFACTS = $peerRoot

        $trxName = "interop-$Profile.trx"
        $stdoutPath = Join-Path $peerRoot 'dotnet-test.stdout.log'
        $stderrPath = Join-Path $peerRoot 'dotnet-test.stderr.log'
        Write-Host "Running $Profile matrix for $($peer.id) with filter $filter"
        & dotnet test (Join-Path $repoRoot 'tests/RsyncWin.Interop.Tests/RsyncWin.Interop.Tests.csproj') `
            '--configuration' 'Release' `
            '--no-restore' `
            '--filter' $filter `
            '--results-directory' $peerRoot `
            '--logger' "trx;LogFileName=$trxName" `
            1> $stdoutPath 2> $stderrPath
        $exitCode = $LASTEXITCODE
        Get-Content -LiteralPath $stdoutPath | Write-Host
        if ((Get-Item -LiteralPath $stderrPath).Length -gt 0) {
            Get-Content -LiteralPath $stderrPath | Write-Warning
        }
    }
    catch {
        $exitCode = 1
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $peerRoot 'runner-error.txt')
        Write-Warning $_
    }
    finally {
        if ($null -eq $previousPeer) { Remove-Item Env:RSYNCWIN_INTEROP_PEER -ErrorAction SilentlyContinue }
        else { $env:RSYNCWIN_INTEROP_PEER = $previousPeer }
        if ($null -eq $previousArtifacts) { Remove-Item Env:RSYNCWIN_INTEROP_ARTIFACTS -ErrorAction SilentlyContinue }
        else { $env:RSYNCWIN_INTEROP_ARTIFACTS = $previousArtifacts }
    }

    $duration = ((Get-Date) - $started).TotalSeconds
    $summary = [pscustomobject]@{
        peer = $peer.id
        version = $peer.version
        image = $peer.image
        imageId = if ($null -eq $probe) { $null } else { $probe.ImageId }
        versionLine = if ($null -eq $probe) { $null } else { $probe.VersionLine }
        sourceUrl = if ($null -eq $probe) { $null } else { $probe.SourceUrl }
        sourceSha256 = if ($null -eq $probe) { $null } else { $probe.SourceSha256 }
        packageVersion = if ($null -eq $probe) { $null } else { $probe.PackageVersion }
        profile = $Profile
        filter = $filter
        exitCode = $exitCode
        durationSeconds = [math]::Round($duration, 2)
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $peerRoot 'summary.json')
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $peerRoot 'peer-info.json')
    $results.Add($summary)
}

$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'matrix-summary.json')
$leftovers = Invoke-Captured docker @('ps', '-a', '--filter', 'label=rsyncwin-interop=1', '--format', '{{.ID}}')
if ($leftovers.ExitCode -ne 0) {
    throw "Could not inspect interop containers: $($leftovers.Output)"
}
if (-not [string]::IsNullOrWhiteSpace($leftovers.Output)) {
    $leftovers.Output | Set-Content -LiteralPath (Join-Path $runRoot 'orphan-containers.txt')
    throw "Interop containers remain after the matrix. See $runRoot/orphan-containers.txt"
}
if (@($results | Where-Object { $_.exitCode -ne 0 }).Count -gt 0) {
    throw "Live interop matrix failed. See $runRoot"
}

Write-Host "Live interop matrix passed. Artifacts: $runRoot"
