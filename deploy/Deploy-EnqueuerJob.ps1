<#
.SYNOPSIS
    Creates the enqueuer's Container Apps job if it is missing, or updates it to a new image and its settings (Issue 6, #117).

.DESCRIPTION
    Run by the pipeline's Deploy stage, signed in as id-pipeline through sc-reposcanner-dev. Never run it by hand
    without -WhatIf: the job is changed only by the pipeline (#84).

    Looks up id-scanner, then looks for the job:

        missing   creates it with every setting in the Issue 6 design's Job settings
        exists    assigns its identity, registry and Key Vault secret reference again, then updates its image,
                  schedule, timeout, retries, CPU, memory and environment variables, so running it twice changes
                  nothing but the image (decision 10)

    Any other failure to look the job up stops the script rather than trying to create a job that may exist. Holds no
    secret: the PAT reaches the job as a Key Vault reference read by id-scanner. Exits 0 after a create or an update;
    any failed az command stops it with that command's error.

.PARAMETER Image
    The image to deploy, such as enqreposcannerdev.azurecr.io/enqueuer:42. The pipeline passes the build's own tag.

.PARAMETER ResourceGroup
    The dev environment's resource group.

.PARAMETER Environment
    The Container Apps environment the job runs in.

.PARAMETER JobName
    The job's name, job-enqueuer-dev (Muhammad, 2026-10-19).

.PARAMETER Registry
    The container registry the image is pulled from, as id-scanner.

.PARAMETER Identity
    The user-assigned identity the job runs as, pulls its image as, and reads the PAT as (RepoScanner ADR-014).

.PARAMETER KeyVault
    The Key Vault holding the PAT.

.PARAMETER SecretName
    The PAT's secret name, in Key Vault and in the job.

.PARAMETER StorageAccount
    The storage account holding the queue.

.PARAMETER QueueName
    The queue the enqueuer sends to.

.PARAMETER OrganizationUrl
    The Azure DevOps organisation whose repositories are listed.

.EXAMPLE
    .\deploy\Deploy-EnqueuerJob.ps1 -Image enqreposcannerdev.azurecr.io/enqueuer:0 -WhatIf

.EXAMPLE
    .\deploy\Deploy-EnqueuerJob.ps1 -Image enqreposcannerdev.azurecr.io/enqueuer:$(Build.BuildId)
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Image,

    [string] $ResourceGroup = 'RepoAnalysis-dev',
    [string] $Environment = 'code-reposcanner-dev',
    [string] $JobName = 'job-enqueuer-dev',
    [string] $Registry = 'enqreposcannerdev',
    [string] $Identity = 'id-scanner',
    [string] $KeyVault = 'kv-reposcanner-dev',
    [string] $SecretName = 'azure-devops-pat',
    [string] $StorageAccount = 'strreposcannerdev',
    [string] $QueueName = 'scan-nightly',
    [string] $OrganizationUrl = 'https://dev.azure.com/BlacklightInternalsMo'
)

$ErrorActionPreference = 'Stop'

# The job's settings (Issue 6 design, Job settings). 02:00 UTC is 03:00 in British Summer Time. The listing retries
# for at most 2 minutes, so 600 seconds is ample; one retry, as the scanner drops the duplicates a retry sends (#76).
$cronExpression = '0 2 * * *'
$replicaTimeoutSeconds = '600'
$replicaRetryLimit = '1'
$cpu = '0.25'
$memory = '0.5Gi'

# Runs az and returns its exit code, standard output and standard error separately, so a caller can tell a missing
# job from any other failure. 'Continue' because Windows PowerShell 5.1 turns standard-error lines into terminating
# errors under 'Stop'.
function Invoke-AzCommand {
    param([string[]] $Arguments)
    $ErrorActionPreference = 'Continue'
    $lines = @(& az @Arguments --only-show-errors 2>&1)
    $exitCode = $LASTEXITCODE
    $errorLines = @($lines | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    $outputLines = @($lines | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = ($outputLines -join "`n").Trim()
        Error    = ($errorLines -join "`n").Trim()
    }
}

# Runs az and returns its standard output, or stops the script with the command's name and az's error
function Invoke-Az {
    param([string[]] $Arguments)
    $result = Invoke-AzCommand $Arguments
    if ($result.ExitCode -ne 0) {
        # The command's own words, up to its first option, such as "containerapp job create"
        $words = @()
        foreach ($argument in $Arguments) {
            if ($argument.StartsWith('-')) {
                break
            }
            $words += $argument
        }
        throw "az $($words -join ' ') failed: $($result.Error)"
    }
    return $result.Output
}

# The containerapp commands come from an Azure CLI extension; adding it here means the agent's own copy, or the lack
# of one, does not matter (design, Left to the builder)
if ($PSCmdlet.ShouldProcess('the Azure CLI', 'Add or upgrade the containerapp extension')) {
    Invoke-Az @('extension', 'add', '--name', 'containerapp', '--upgrade', '--yes') | Out-Null
}

# id-scanner runs the job, pulls its image and reads the PAT (RepoScanner ADR-014); its client ID tells
# DefaultAzureCredential which identity to use
$identityJson = Invoke-Az @('identity', 'show', '--name', $Identity, '--resource-group', $ResourceGroup, '--query', '{id: id, clientId: clientId}', '--output', 'json')
$identityInfo = $identityJson | ConvertFrom-Json
$identityId = $identityInfo.id

$registryServer = "$Registry.azurecr.io"

# The PAT as a Key Vault reference naming the secret without a version, so a renewed PAT is picked up without a
# deployment. The job never holds its value (design, Job settings).
$secret = "$SecretName=keyvaultref:https://$KeyVault.vault.azure.net/secrets/$SecretName,identityref:$identityId"

$environmentVariables = @(
    "AZURE_CLIENT_ID=$($identityInfo.clientId)",
    "Enqueuer__Queue__Name=$QueueName",
    "Enqueuer__Queue__ServiceUri=https://$StorageAccount.queue.core.windows.net",
    "Enqueuer__AzureDevOps__OrganizationUrl=$OrganizationUrl",
    "Enqueuer__AzureDevOps__Pat=secretref:$SecretName"
)

# Only ARM's ResourceNotFound means the job is missing. A missing resource group (ResourceGroupNotFound), an expired
# sign-in or a missing role stops the script instead of trying to create a job that may exist (plan, Review Focus 1).
$lookup = Invoke-AzCommand @('containerapp', 'job', 'show', '--name', $JobName, '--resource-group', $ResourceGroup, '--query', 'id', '--output', 'tsv')
if ($lookup.ExitCode -eq 0) {
    $exists = $true
}
elseif ($lookup.Error -match '\(ResourceNotFound\)') {
    $exists = $false
}
else {
    throw "az containerapp job show failed: $($lookup.Error)"
}

$jobArguments = @('--name', $JobName, '--resource-group', $ResourceGroup)
$deployed = $false

if (-not $exists) {
    if ($PSCmdlet.ShouldProcess($JobName, 'Create')) {
        Invoke-Az (@('containerapp', 'job', 'create') + $jobArguments + @(
                '--environment', $Environment,
                '--trigger-type', 'Schedule',
                '--cron-expression', $cronExpression,
                '--replica-timeout', $replicaTimeoutSeconds,
                '--replica-retry-limit', $replicaRetryLimit,
                '--parallelism', '1',
                '--replica-completion-count', '1',
                '--image', $Image,
                '--cpu', $cpu,
                '--memory', $memory,
                '--mi-user-assigned', $identityId,
                '--registry-server', $registryServer,
                '--registry-identity', $identityId,
                '--secrets', $secret,
                '--env-vars') + $environmentVariables) | Out-Null
        Write-Host "Created $JobName"
        $deployed = $true
    }
}
else {
    if ($PSCmdlet.ShouldProcess($JobName, 'Update')) {
        # Identity, registry and secret first, so the new image is pulled and started with them in place
        Invoke-Az (@('containerapp', 'job', 'identity', 'assign') + $jobArguments + @('--user-assigned', $identityId)) | Out-Null
        Invoke-Az (@('containerapp', 'job', 'registry', 'set') + $jobArguments + @('--server', $registryServer, '--identity', $identityId)) | Out-Null
        Invoke-Az (@('containerapp', 'job', 'secret', 'set') + $jobArguments + @('--secrets', $secret)) | Out-Null
        Invoke-Az (@('containerapp', 'job', 'update') + $jobArguments + @(
                '--image', $Image,
                '--cron-expression', $cronExpression,
                '--replica-timeout', $replicaTimeoutSeconds,
                '--replica-retry-limit', $replicaRetryLimit,
                '--cpu', $cpu,
                '--memory', $memory,
                '--replace-env-vars') + $environmentVariables) | Out-Null
        Write-Host "Updated $JobName"
        $deployed = $true
    }
}

if ($deployed) {
    $summaryQuery = '{image: properties.template.containers[0].image, cron: properties.configuration.scheduleTriggerConfig.cronExpression, timeout: properties.configuration.replicaTimeout, retries: properties.configuration.replicaRetryLimit}'
    $summary = Invoke-Az (@('containerapp', 'job', 'show') + $jobArguments + @('--query', $summaryQuery, '--output', 'json')) | ConvertFrom-Json
    Write-Host "${JobName}: image $($summary.image), schedule $($summary.cron) UTC, timeout $($summary.timeout) s, retries $($summary.retries), identity $Identity"
}
