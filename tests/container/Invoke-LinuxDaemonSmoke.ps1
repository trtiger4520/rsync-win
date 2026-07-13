[CmdletBinding()]
param(
    [int] $CommandTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$suffix = [Guid]::NewGuid().ToString('N')
$clientImage = "rsyncwin-p11-client:$suffix"
$serverImage = "rsyncwin-p11-rsyncd:$suffix"
$serverContainer = "rsyncwin-p11-rsyncd-$suffix"
$network = "rsyncwin-p11-$suffix"
$volume = "rsyncwin-p11-data-$suffix"

function Invoke-Process {
    param(
        [Parameter(Mandatory)] [string] $FileName,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [int] $TimeoutSeconds = $CommandTimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FileName)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start $FileName"
    }

    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try {
            $process.Kill($true)
        }
        catch [System.InvalidOperationException] {
        }
        $process.WaitForExit()
        throw "$FileName timed out after $TimeoutSeconds seconds: $($Arguments -join ' ')"
    }

    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout.GetAwaiter().GetResult()
        StdErr = $stderr.GetAwaiter().GetResult()
    }
    $process.Dispose()
    return $result
}

function Invoke-Docker {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [int] $TimeoutSeconds = $CommandTimeoutSeconds,
        [switch] $AllowFailure
    )

    $result = Invoke-Process -FileName 'docker' -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
    if (-not $AllowFailure -and $result.ExitCode -ne 0) {
        throw "docker $($Arguments -join ' ') failed with exit $($result.ExitCode)`n$($result.StdErr)"
    }
    return $result
}

function Invoke-Client {
    param(
        [Parameter(Mandatory)] [string] $Step,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $container = "rsyncwin-p11-client-$Step-$suffix"
    try {
        return Invoke-Docker -Arguments (@(
            'run', '--name', $container, '--rm',
            '--network', $network,
            '--mount', "source=$volume,target=/data",
            $clientImage
        ) + $Arguments)
    }
    finally {
        [void] (Invoke-Docker -Arguments @('rm', '-f', $container) -AllowFailure)
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool] $Condition,
        [Parameter(Mandatory)] [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Success {
    param(
        [Parameter(Mandatory)] $Result,
        [Parameter(Mandatory)] [string] $Step
    )

    Assert-True ($Result.ExitCode -eq 0) "$Step exited $($Result.ExitCode)`nstdout:`n$($Result.StdOut)`nstderr:`n$($Result.StdErr)"
}

function Get-ClientManifest {
    param([Parameter(Mandatory)] [string] $Path)

    $result = Invoke-Docker -Arguments @(
        'run', '--rm', '--entrypoint', 'sh',
        '--mount', "source=$volume,target=/data",
        $clientImage, '-c',
        "cd '$Path' && find . -type f -print0 | sort -z | xargs -0 -r sha256sum"
    )
    return $result.StdOut.Trim()
}

function Get-ServerManifest {
    param([Parameter(Mandatory)] [string] $Path)

    $result = Invoke-Docker -Arguments @(
        'exec', $serverContainer, 'sh', '-c',
        "cd '$Path' && find . -type f -print0 | sort -z | xargs -0 -r sha256sum"
    )
    return $result.StdOut.Trim()
}

try {
    Assert-Success (Invoke-Docker -Arguments @('build', '--tag', $clientImage, '--file', 'Dockerfile', '.')) 'client image build'
    $configuredUser = Invoke-Docker -Arguments @('image', 'inspect', '--format', '{{.Config.User}}', $clientImage)
    Assert-Success $configuredUser 'client image user inspection'
    Assert-True (
        -not [string]::IsNullOrWhiteSpace($configuredUser.StdOut) -and
        $configuredUser.StdOut.Trim() -notin @('0', 'root')
    ) 'client image does not configure a non-root user'
    Assert-Success (Invoke-Docker -Arguments @('build', '--tag', $serverImage, 'tests/container/rsyncd')) 'rsyncd image build'

    $version = Invoke-Docker -Arguments @('run', '--rm', '--entrypoint', 'rsync', $serverImage, '--version')
    Assert-Success $version 'rsync version check'
    Assert-True $version.StdOut.StartsWith('rsync  version 3.4.3', [StringComparison]::Ordinal) 'server is not rsync 3.4.3'

    Assert-Success (Invoke-Docker -Arguments @('network', 'create', $network)) 'network create'
    Assert-Success (Invoke-Docker -Arguments @('volume', 'create', $volume)) 'volume create'
    Assert-Success (Invoke-Docker -Arguments @(
        'run', '--rm', '--entrypoint', 'sh',
        '--mount', "source=$volume,target=/data",
        $serverImage, '-c', 'chown 1654:1654 /data'
    )) 'volume ownership setup'
    Assert-Success (Invoke-Docker -Arguments @(
        'run', '--detach', '--name', $serverContainer,
        '--network', $network, '--network-alias', 'rsyncd',
        $serverImage
    )) 'rsyncd start'

    $ready = $false
    for ($attempt = 0; $attempt -lt 30 -and -not $ready; $attempt++) {
        $probe = Invoke-Docker -Arguments @('exec', $serverContainer, 'rsync', 'rsync://127.0.0.1/') -AllowFailure
        $ready = $probe.ExitCode -eq 0 -and $probe.StdOut.Contains('tree', [StringComparison]::Ordinal)
        if (-not $ready) {
            Start-Sleep -Seconds 1
        }
    }
    Assert-True $ready 'rsync daemon did not become ready within 30 seconds'

    $list = Invoke-Client -Step 'list' -Arguments @('rsync://rsyncd/')
    Assert-Success $list 'module listing'
    Assert-True ($list.StdOut.Contains('tree', [StringComparison]::Ordinal) -and $list.StdOut.Contains('push', [StringComparison]::Ordinal)) 'module listing omitted tree or push'

    $pull = Invoke-Client -Step 'pull' -Arguments @('-rtz', 'rsync://rsyncd/tree/', '/data/pull')
    Assert-Success $pull 'pull'
    $sourceManifest = Get-ServerManifest '/srv/tree'
    $pullManifest = Get-ClientManifest '/data/pull'
    Assert-True ($pullManifest -ceq $sourceManifest) 'pull SHA-256 manifest differs from source'
    Assert-True ($pullManifest.Contains('./a:b.txt', [StringComparison]::Ordinal) -and $pullManifest.Contains('./a_b.txt', [StringComparison]::Ordinal)) 'Linux pull did not preserve a:b and a_b'
    Assert-True ($pullManifest.Contains('./Case.txt', [StringComparison]::Ordinal) -and $pullManifest.Contains('./case.txt', [StringComparison]::Ordinal)) 'Linux pull did not preserve case-distinct names'

    $mutateScript = @'
stamp=$(stat -c %y '/data/pull/a:b.txt'); printf 'wrong\n' > '/data/pull/a:b.txt'; touch -d "$stamp" '/data/pull/a:b.txt'; printf 'extra\n' > '/data/pull/delete-me.txt'
'@
    $mutate = Invoke-Docker -Arguments @(
        'run', '--rm', '--entrypoint', 'sh',
        '--mount', "source=$volume,target=/data",
        $clientImage, '-c',
        $mutateScript
    )
    Assert-Success $mutate 'checksum and delete setup'

    $repair = Invoke-Client -Step 'repair' -Arguments @('-rtcz', '--delete', 'rsync://rsyncd/tree/', '/data/pull')
    Assert-Success $repair 'checksum compression delete pull'
    Assert-True ((Get-ClientManifest '/data/pull') -ceq $sourceManifest) 'checksum/delete repair manifest differs from source'

    $pullRerun = Invoke-Client -Step 'pull-rerun' -Arguments @('-rtcz', '--delete', 'rsync://rsyncd/tree/', '/data/pull')
    Assert-Success $pullRerun 'pull rerun'
    Assert-True $pullRerun.StdErr.Contains('files transferred: 0, bytes: 0', [StringComparison]::Ordinal) 'pull rerun was not a zero transfer'

    $seed = Invoke-Docker -Arguments @(
        'run', '--rm', '--entrypoint', 'sh',
        '--mount', "source=$volume,target=/data",
        $clientImage, '-c',
        "mkdir -p /data/pushsrc/nested; printf 'colon\n' > '/data/pushsrc/a:b.txt'; printf 'underscore\n' > /data/pushsrc/a_b.txt; printf 'upper\n' > /data/pushsrc/Case.txt; printf 'lower\n' > /data/pushsrc/case.txt; printf 'nested\n' > /data/pushsrc/nested/deep.txt"
    )
    Assert-Success $seed 'push source setup'
    Assert-Success (Invoke-Docker -Arguments @('exec', $serverContainer, 'sh', '-c', "printf 'extra\n' > /srv/push/delete-me.txt")) 'remote delete setup'

    $push = Invoke-Client -Step 'push' -Arguments @('-rtcz', '--delete', '/data/pushsrc', 'rsync://rsyncd/push/')
    Assert-Success $push 'push'
    $pushManifest = Get-ClientManifest '/data/pushsrc'
    Assert-True ((Get-ServerManifest '/srv/push') -ceq $pushManifest) 'push SHA-256 manifest differs from source'

    $pushRerun = Invoke-Client -Step 'push-rerun' -Arguments @('-rtcz', '--delete', '/data/pushsrc', 'rsync://rsyncd/push/')
    Assert-Success $pushRerun 'push rerun'
    Assert-True $pushRerun.StdErr.Contains('files sent: 0, literal bytes: 0', [StringComparison]::Ordinal) 'push rerun was not a zero transfer'

    Write-Host 'PASS: Linux daemon image smoke completed'
    Write-Host 'PASS: module list, pull, push, checksum, delete, compression, zero reruns, SHA-256 manifests'
    Write-Host 'PASS: a:b/a_b and Case/case remain distinct'
}
finally {
    [void] (Invoke-Docker -Arguments @('rm', '-f', $serverContainer) -AllowFailure)
    [void] (Invoke-Docker -Arguments @('network', 'rm', $network) -AllowFailure)
    [void] (Invoke-Docker -Arguments @('volume', 'rm', '-f', $volume) -AllowFailure)
    [void] (Invoke-Docker -Arguments @('image', 'rm', '-f', $clientImage, $serverImage) -AllowFailure)
}
