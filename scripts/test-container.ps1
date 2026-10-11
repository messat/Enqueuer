<#
.SYNOPSIS
    Builds the enqueuer image and checks it, its connection to Azurite and a real run end to end (Issue 5, #115).

.DESCRIPTION
    Checks, in order, stopping at the first that fails:

        1-3    Docker is running with Linux containers, the Azure CLI is installed, and .env is complete
        4-9    the image builds, runs as a non-root user, has no .NET SDK, no git and no Development settings, and its
               Dockerfile tags every base image with something other than latest
        10-11  a container reaches Azurite on host.docker.internal:10001, and the queue .env names exists and is empty
        12     a run with a bad setting exits 2, with one Fatal line and nothing queued
        13     a real run lists the organisation's repositories, queues one scan request per repository to scan and
               exits 0; its log is JSON, its summary is Completed, and the Queued lines, the summary and the messages
               on the queue agree, by correlation ID

    Needs Docker Desktop, Azurite and the Azure CLI, a network connection to Azure DevOps, and a complete .env
    (README: Run in a container). The requests from check 13 are left on the queue for the scanner. Never prints a
    value from .env. Exits 0 when every check passes, 1 at the first failure.

.PARAMETER Tag
    The image tag to build and run: v1 by default, giving enqueuer:v1.

.PARAMETER EnvFile
    The settings file passed to the container: .env at the repository root by default.

.EXAMPLE
    .\scripts\test-container.ps1

.EXAMPLE
    .\scripts\test-container.ps1 -Tag v1 -EnvFile .env
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Tag = 'v1',

    [string] $EnvFile = ''
)

$ErrorActionPreference = 'Stop'

# Found from the script's own folder, so the script works whichever folder it is started from
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrEmpty($EnvFile)) {
    $EnvFile = Join-Path $root '.env'
}
$image = "enqueuer:$Tag"
$checkCount = 13
$script:passed = 0

# The script reads the queue from the host, where Azurite is 127.0.0.1; the container reaches the same Azurite through
# host.docker.internal (Issue 5 design, decision 17)
$hostConnectionString = 'UseDevelopmentStorage=true'

# The settings the container needs (Issue 5 design, .env)
$requiredNames = @(
    'Enqueuer__Queue__Name',
    'Enqueuer__Queue__ConnectionString',
    'Enqueuer__AzureDevOps__OrganizationUrl',
    'Enqueuer__AzureDevOps__Pat'
)

function Write-Pass {
    param([string] $Name)
    $script:passed++
    Write-Host "PASS $Name" -ForegroundColor Green
}

# Stops the script: a later check would only fail for the same reason, or test something that is not there
function Stop-WithFailure {
    param([string] $Name, [string] $Reason)
    Write-Host "FAIL ${Name}: $Reason" -ForegroundColor Red
    Write-Host "$script:passed of $checkCount checks passed"
    exit 1
}

# Runs a native command and returns its standard output as one string, dropping standard error; the exit code is
# left in $LASTEXITCODE. 'Continue' because Windows PowerShell 5.1 turns redirected standard-error lines into
# terminating errors under 'Stop'.
function Invoke-Quiet {
    param([string] $FilePath, [string[]] $Arguments)
    $ErrorActionPreference = 'Continue'
    $output = & $FilePath @Arguments 2>$null
    return (($output | Out-String).Trim())
}

# Runs a native command with everything it prints shown, as for the build; the exit code is left in $LASTEXITCODE.
# 'Continue' for the same reason: docker writes its progress to standard error.
function Invoke-Shown {
    param([string] $FilePath, [string[]] $Arguments)
    $ErrorActionPreference = 'Continue'
    & $FilePath @Arguments | Out-Host
}

# Runs a native command and returns the lines it printed to standard output, without showing them, so the enqueuer's
# log is shown only once it is known not to hold the PAT. The comma keeps a one-line result an array.
function Invoke-Captured {
    param([string] $FilePath, [string[]] $Arguments)
    $ErrorActionPreference = 'Continue'
    $lines = @(& $FilePath @Arguments)
    return , $lines
}

# Every string in a parsed JSON value, however deeply it is nested in objects and arrays
function Get-StringValues {
    param($Value)
    if ($null -eq $Value) {
        return
    }
    if ($Value -is [string]) {
        $Value
    }
    elseif ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            Get-StringValues $property.Value
        }
    }
    elseif ($Value -is [System.Collections.IEnumerable]) {
        foreach ($item in $Value) {
            Get-StringValues $item
        }
    }
}

# The PAT must not be in the enqueuer's output: searched in each raw line, and in every string of each line read as
# JSON, since JSON may escape a character of it, such as + as \u002B (Issue 5 design, decision 20). Never prints it.
function Assert-PatAbsent {
    param([string] $Name, [string[]] $Lines)
    foreach ($line in $Lines) {
        if ($line.Contains($script:pat)) {
            Stop-WithFailure $Name "the PAT is in the enqueuer's output, which is not shown"
        }
        try {
            $entry = $line | ConvertFrom-Json
        }
        catch {
            continue
        }
        foreach ($text in (Get-StringValues $entry)) {
            if ($text.Contains($script:pat)) {
                Stop-WithFailure $Name "the PAT is in the enqueuer's output, which is not shown"
            }
        }
    }
}

# Each line of the enqueuer's log as one JSON object, as Log Analytics unpacks it. The container is in Production,
# whose default is JSON (Issue 5 design, decision 18). $entry, not $event, which PowerShell keeps.
function ConvertFrom-LogLines {
    param([string] $Name, [string[]] $Lines)
    $entries = New-Object System.Collections.Generic.List[object]
    $lineNumber = 0
    foreach ($line in $Lines) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        try {
            $entry = $line | ConvertFrom-Json
        }
        catch {
            Stop-WithFailure $Name "line $lineNumber of the enqueuer's log is not JSON (design, decision 18: check .env does not set Enqueuer__Logging__Format)"
        }
        $entries.Add($entry)
    }
    return , $entries.ToArray()
}

# Up to $Count messages from the queue, read from the host without taking them. az prints [] for an empty queue.
function Get-PeekedMessages {
    param([string] $Name, [int] $Count)
    $json = Invoke-Quiet 'az' @('storage', 'message', 'peek', '--queue-name', $script:queueName, '--num-messages', "$Count", '--connection-string', $hostConnectionString, '--output', 'json')
    if ($LASTEXITCODE -ne 0) {
        Stop-WithFailure $Name "az could not peek at $script:queueName in Azurite"
    }
    # Not piped: Windows PowerShell 5.1 would pass a JSON array down the pipeline as one object
    $parsed = ConvertFrom-Json -InputObject $json
    if ($null -eq $parsed) {
        return , @()
    }
    return , @($parsed)
}

# 1. The image is Linux, so Docker Desktop in Windows containers mode would fail the build with a manifest error.
$check = 'Docker is running'
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Stop-WithFailure $check 'docker was not found. Install Docker Desktop'
}
Invoke-Quiet 'docker' @('version', '--format', '{{.Server.Version}}') | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'Start Docker Desktop and try again'
}
$serverOs = Invoke-Quiet 'docker' @('version', '--format', '{{.Server.Os}}')
if ($serverOs -ne 'linux') {
    Stop-WithFailure $check "Docker Desktop is using $serverOs containers: switch to Linux containers"
}
Write-Pass $check

# 2
$check = 'The Azure CLI is installed'
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Stop-WithFailure $check 'az was not found. Install the Azure CLI (https://learn.microsoft.com/cli/azure/install-azure-cli)'
}
Write-Pass $check

# 3. Problems are named by line number and setting, never by value, since the file holds the PAT and Azurite's key.
$check = '.env is complete'
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
    Stop-WithFailure $check "there is no file at $EnvFile. Copy .env.example to .env and fill it in (README: Run in a container)"
}
# A full path, so .NET and docker read the file Test-Path found: Windows PowerShell 5.1 resolves a relative path
# for .NET from the folder the process started in, not the current one
$EnvFile = (Resolve-Path -LiteralPath $EnvFile).ProviderPath
# .NET reads a UTF-16 file as easily as UTF-8, but docker cannot, so the container would get none of its settings.
# A UTF-8 file with a byte order mark and Windows line endings is fine for both.
$bytes = [System.IO.File]::ReadAllBytes($EnvFile)
if ($bytes.Length -ge 2 -and (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))) {
    Stop-WithFailure $check "$EnvFile is saved as UTF-16, which docker cannot read: save it as UTF-8"
}
$found = @{}
$script:queueName = ''
$script:pat = ''
$lineNumber = 0
foreach ($line in [System.IO.File]::ReadAllLines($EnvFile)) {
    $lineNumber++
    # Docker skips blank lines and lines starting with #, so they are skipped here too
    if ($line.Trim().Length -eq 0 -or $line.TrimStart().StartsWith('#')) {
        continue
    }
    # The name stops at the first =, as docker reads it: the connection string's own = signs are part of its value
    if ($line -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        Stop-WithFailure $check "line $lineNumber of $EnvFile is not NAME=value"
    }
    $name = $Matches[1]
    $value = $Matches[2]
    if ($value.Length -eq 0) {
        Stop-WithFailure $check "line $lineNumber, $name, is empty"
    }
    # Docker keeps quotes as part of the value, so a quoted queue name or PAT would not work
    if ($value.StartsWith('"') -or $value.StartsWith("'")) {
        Stop-WithFailure $check "line $lineNumber, $name, is in quotes. Docker keeps them as part of the value: remove them"
    }
    # Docker keeps spaces too, so a PAT pasted with a trailing space would be refused as if it were wrong
    if ($value -ne $value.Trim()) {
        Stop-WithFailure $check "line $lineNumber, $name, starts or ends with a space. Docker keeps it as part of the value: remove it"
    }
    # A line copied from .env.example and not filled in
    if ($value -match '<[^<>]+>') {
        Stop-WithFailure $check "line $lineNumber, $name, still has a placeholder: replace the <...> with the real value"
    }
    # The queue name is not a secret: checks 11 to 13 read the queue the container sends to (design, decision 2)
    if ($name -eq 'Enqueuer__Queue__Name') {
        if ($value -ne 'scan-nightly') {
            Stop-WithFailure $check "line $lineNumber, $name, must be scan-nightly, the enqueuer's queue"
        }
        $script:queueName = $value
    }
    # In the container 127.0.0.1 is the container itself, so Azurite must be named as host.docker.internal
    if ($name -eq 'Enqueuer__Queue__ConnectionString' -and $value -match '^UseDevelopmentStorage=true;?$') {
        Stop-WithFailure $check "line $lineNumber, $name, is UseDevelopmentStorage=true, which in the container points at the container itself: use the host.docker.internal form in .env.example (design, decision 13)"
    }
    # Kept, never printed, for checks 12 and 13 to search the enqueuer's output for
    if ($name -eq 'Enqueuer__AzureDevOps__Pat') {
        $script:pat = $value
    }
    $found[$name] = $true
}
foreach ($name in $requiredNames) {
    if (-not $found.ContainsKey($name)) {
        Stop-WithFailure $check "$name is missing (see .env.example)"
    }
}
Write-Pass $check

# 4
$check = 'Image builds'
Invoke-Shown 'docker' @('build', '-t', $image, $root)
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check "docker build exited ${LASTEXITCODE}: see its output above"
}
Write-Pass "$check ($image)"

# 5
$check = 'Runs as a non-root user'
$userId = Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'id', $image, '-u')
if ($LASTEXITCODE -ne 0 -or $userId -eq '0') {
    Stop-WithFailure $check 'the image runs as root: check USER $APP_UID in the Dockerfile'
}
Write-Pass "$check (user $userId)"

# 6
$check = 'No .NET SDK in the image'
$sdks = Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'dotnet', $image, '--list-sdks')
if ($LASTEXITCODE -ne 0 -or $sdks.Length -gt 0) {
    Stop-WithFailure $check "found '$sdks': the final stage must be runtime:10.0"
}
Write-Pass $check

# 7. The answer is printed, not taken from the exit code, which would also be non-zero if docker itself failed.
$check = 'No git in the image'
$git = Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'sh', $image, '-c', 'if command -v git > /dev/null; then echo found; else echo absent; fi')
if ($LASTEXITCODE -ne 0 -or $git -ne 'absent') {
    Stop-WithFailure $check 'git is in the image: the final stage must not install it (design, decision 6)'
}
Write-Pass $check

# 8
$check = 'No appsettings.Development.json in the image'
Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'sh', $image, '-c', 'test ! -e /app/appsettings.Development.json') | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'the file is in /app: check CopyToPublishDirectory="Never" in Enqueuer.csproj'
}
Write-Pass $check

# 9. A FROM naming an earlier stage, such as FROM build, is not an image and needs no tag.
$check = 'Base images are tagged'
$stages = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $root 'Dockerfile')) {
    if ($line -notmatch '^\s*FROM\s+(?:--\S+\s+)*(\S+)(?:\s+AS\s+(\S+))?') {
        continue
    }
    $reference = $Matches[1]
    if ($Matches[2]) {
        $stages[$Matches[2]] = $true
    }
    if ($stages.ContainsKey($reference) -and $reference -ne $Matches[2]) {
        continue
    }
    # The tag is after the last colon, as long as no / follows it (a registry's port is not a tag)
    if ($reference -notmatch ':([^/:]+)$' -or $Matches[1] -eq 'latest') {
        Stop-WithFailure $check "'$($line.Trim())' must name a tag other than latest (RepoScanner ADR-005)"
    }
}
Write-Pass $check

# 10. busybox is pinned, as every image here is.
$check = 'Azurite reachable from a container'
Invoke-Quiet 'docker' @('run', '--rm', 'busybox:1.37', 'nc', '-z', '-w', '5', 'host.docker.internal', '10001') | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'start Azurite: azurite --location c:\azurite_data (README: Run)'
}
Write-Pass "$check (host.docker.internal:10001)"

# 11. The enqueuer never creates its queue (Issue 3 design, decision 9). Messages already there are not this run's,
# and would spoil check 13's count, so they are left for the user to deal with (Issue 5 design, decision 2).
$check = 'The queue exists and is empty'
$exists = Invoke-Quiet 'az' @('storage', 'queue', 'exists', '--name', $script:queueName, '--connection-string', $hostConnectionString, '--query', 'exists', '--output', 'tsv')
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'az could not reach Azurite on 127.0.0.1:10001'
}
if ($exists -ne 'true') {
    Stop-WithFailure $check "$script:queueName does not exist: create it (README: Run)"
}
$waiting = Get-PeekedMessages $check 1
if ($waiting.Count -ne 0) {
    Stop-WithFailure $check "$script:queueName has messages that are not this run's: let the scanner take them, or remove them, and run again (design, decision 2)"
}
Write-Pass "$check ($script:queueName)"

# 12. -e wins over --env-file, so only the queue name changes. The settings check fails before any network call.
$check = 'A bad setting exits 2'
$logLines = Invoke-Captured 'docker' @('run', '--rm', '--env-file', $EnvFile, '-e', 'Enqueuer__Queue__Name=Bad', $image)
$exitCode = $LASTEXITCODE
Assert-PatAbsent $check $logLines
$logLines | Out-Host
if ($exitCode -ne 2) {
    Stop-WithFailure $check "the enqueuer exited ${exitCode}, not 2: see its log above (README: Exit codes)"
}
$entries = ConvertFrom-LogLines $check $logLines
$fatal = @($entries | Where-Object { $_.'@l' -eq 'Fatal' })
if ($fatal.Count -ne 1 -or -not ([string]$fatal[0].'@m').StartsWith('Invalid configuration')) {
    Stop-WithFailure $check "expected one Fatal line starting 'Invalid configuration', found $($fatal.Count) Fatal lines"
}
if ((Get-PeekedMessages $check 1).Count -ne 0) {
    Stop-WithFailure $check "a message reached $script:queueName from a run that should not have started"
}
Write-Pass $check

# 13. Nothing is forced: the container's own default writes JSON (Issue 5 design, decision 18).
$check = 'The container queues and exits 0'
$logLines = Invoke-Captured 'docker' @('run', '--rm', '--env-file', $EnvFile, $image)
$exitCode = $LASTEXITCODE
Assert-PatAbsent $check $logLines
$logLines | Out-Host
if ($exitCode -ne 0) {
    Stop-WithFailure $check "the enqueuer exited ${exitCode}: see its log above (README: Exit codes)"
}
$entries = ConvertFrom-LogLines $check $logLines

$summaries = @($entries | Where-Object { $null -ne $_.EnqueueRunStatus })
if ($summaries.Count -eq 0) {
    Stop-WithFailure $check "no summary line in the enqueuer's log (README: What it logs)"
}
$summary = $summaries[-1]
if ($summary.EnqueueRunStatus -ne 'Completed' -or [int]$summary.NotSentCount -ne 0) {
    Stop-WithFailure $check "the summary says $($summary.EnqueueRunStatus) with $($summary.NotSentCount) not sent"
}
$sentCount = [int]$summary.SentCount
# A run that sent nothing would make every count below agree at 0, and prove nothing about the container reaching the
# queue, which is what this check is for (Issue 5 design, success criterion 1)
if ($sentCount -eq 0) {
    Stop-WithFailure $check "the run sent nothing (see its summary above), so sending to $script:queueName was not proved: check the PAT's owner can see the organisation's projects, and that not every repository was skipped (README: What it logs)"
}

# The Queued lines, the summary and the queue must agree, and each message must be one the log names, so the
# correlation ID that joins the two apps' logs survives the container (Issue 5 design, decision 19)
$queued = @($entries | Where-Object { ([string]$_.'@m').StartsWith('Queued ') -and $null -ne $_.CorrelationId })
if ($sentCount -gt 32) {
    Stop-WithFailure $check "$sentCount requests were sent, but az peeks at most 32 messages: check $script:queueName by hand (design, Known limitations)"
}
$messages = Get-PeekedMessages $check 32
if ($queued.Count -ne $sentCount -or $messages.Count -ne $sentCount) {
    Stop-WithFailure $check "$($queued.Count) Queued lines, SentCount $sentCount and $($messages.Count) messages on $script:queueName should be equal"
}
$queuedIds = @{}
foreach ($entry in $queued) {
    $queuedIds[([guid]$entry.CorrelationId).ToString()] = $true
}
$seenIds = @{}
foreach ($message in $messages) {
    try {
        $body = $message.content | ConvertFrom-Json
    }
    catch {
        Stop-WithFailure $check "a message on $script:queueName is not JSON"
    }
    if ($body.schemaVersion -ne 1) {
        Stop-WithFailure $check "a message on $script:queueName has schemaVersion '$($body.schemaVersion)', not 1"
    }
    $correlationId = [guid]::Empty
    if (-not [guid]::TryParse([string]$body.correlationId, [ref] $correlationId)) {
        Stop-WithFailure $check "a message on $script:queueName has no correlation ID"
    }
    $key = $correlationId.ToString()
    if (-not $queuedIds.ContainsKey($key)) {
        Stop-WithFailure $check "the message with correlation ID $key is on no Queued line"
    }
    if ($seenIds.ContainsKey($key)) {
        Stop-WithFailure $check "two messages have correlation ID $key"
    }
    $seenIds[$key] = $true
}
Write-Pass "$check ($sentCount sent)"

Write-Host "All $checkCount checks passed: $image queued $sentCount scan requests to $script:queueName and exited 0. They are left there for the scanner (README: Scan what it queued)" -ForegroundColor Green
exit 0
