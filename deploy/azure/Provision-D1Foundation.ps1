#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
    [string]$Location = 'westeurope',
    [string]$ResourceGroup = 'rg-d1-invoice-demo',
    [string]$LogAnalyticsWorkspace = 'log-d1-invoice-demo',
    [string]$ContainerAppsEnvironment = 'cae-d1-invoice-demo',
    [ValidatePattern('^[a-zA-Z0-9]{5,50}$')][string]$AcrName = 'acrd1invoicedemo',
    [string]$AutonomousManagedIdentityName = 'id-d1-invoice-workers',
    [string]$InteractiveManagedIdentityName = 'id-d1-invoice-interactive',
    [string]$WebManagedIdentityName = 'id-d1-invoice-web-runtime',
    [string]$ApiManagedIdentityName = 'id-d1-invoice-api-runtime',
    [string]$ReuseResourceGroup,
    [string]$ReuseLogAnalyticsWorkspace,
    [string]$ReuseContainerAppsEnvironment,
    [string]$ReuseAcrName,
    [switch]$SkipAcrCreate,
    [string]$EvidencePath = (Join-Path $PSScriptRoot 'evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "Required command '$Name' is missing." }
}

function Invoke-Az([string[]]$Arguments, [switch]$Json) {
    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)" }
    $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    if ($Json -and $text.Trim()) { return $text | ConvertFrom-Json }
    return $text.Trim()
}

Assert-Command 'az'
Invoke-Az @('login','--tenant',$TenantId,'--output','none') | Out-Null
Invoke-Az @('account','set','--subscription',$SubscriptionId) | Out-Null
$account = Invoke-Az @('account','show','--output','json') -Json
if ($account.id -ne $SubscriptionId -or $account.tenantId -ne $TenantId) { throw 'Selected Azure subscription/tenant does not match the requested values.' }

foreach ($provider in @('Microsoft.App','Microsoft.OperationalInsights','Microsoft.ManagedIdentity','Microsoft.ContainerRegistry','Microsoft.DBforPostgreSQL','Microsoft.KeyVault')) {
    Invoke-Az @('provider','register','--namespace',$provider,'--wait','--output','none') | Out-Null
    $state = Invoke-Az @('provider','show','--namespace',$provider,'--query','registrationState','--output','tsv')
    if ($state -ne 'Registered') { throw "Resource provider $provider is not Registered." }
}
Invoke-Az @('extension','add','--name','containerapp','--upgrade','--output','none') | Out-Null

$targetResourceGroup = if ($ReuseResourceGroup) { $ReuseResourceGroup } else { $ResourceGroup }
Invoke-Az @('group','create','--name',$targetResourceGroup,'--location',$Location,'--output','none') | Out-Null

$targetWorkspace = if ($ReuseLogAnalyticsWorkspace) { $ReuseLogAnalyticsWorkspace } else { $LogAnalyticsWorkspace }
try {
    $workspace = Invoke-Az @('monitor','log-analytics','workspace','show','--resource-group',$targetResourceGroup,'--workspace-name',$targetWorkspace,'--output','json') -Json
}
catch {
    $workspace = Invoke-Az @('monitor','log-analytics','workspace','create','--resource-group',$targetResourceGroup,'--workspace-name',$targetWorkspace,'--location',$Location,'--output','json') -Json
}
$workspaceKeys = Invoke-Az @('monitor','log-analytics','workspace','get-shared-keys','--resource-group',$targetResourceGroup,'--workspace-name',$targetWorkspace,'--output','json') -Json

$targetEnvironment = if ($ReuseContainerAppsEnvironment) { $ReuseContainerAppsEnvironment } else { $ContainerAppsEnvironment }
try {
    $environment = Invoke-Az @('containerapp','env','show','--resource-group',$targetResourceGroup,'--name',$targetEnvironment,'--output','json') -Json
}
catch {
    $environment = Invoke-Az @('containerapp','env','create','--resource-group',$targetResourceGroup,'--name',$targetEnvironment,'--location',$Location,'--logs-workspace-id',$workspace.customerId,'--logs-workspace-key',$workspaceKeys.primarySharedKey,'--output','json') -Json
}

$targetAcr = if ($ReuseAcrName) { $ReuseAcrName } else { $AcrName }
if (-not $SkipAcrCreate -and -not $ReuseAcrName) {
    try { $acr = Invoke-Az @('acr','show','--name',$targetAcr,'--resource-group',$targetResourceGroup,'--output','json') -Json }
    catch { $acr = Invoke-Az @('acr','create','--name',$targetAcr,'--resource-group',$targetResourceGroup,'--location',$Location,'--sku','Basic','--admin-enabled','false','--output','json') -Json }
}
else {
    $acr = Invoke-Az @('acr','show','--name',$targetAcr,'--output','json') -Json
}

$autonomousIdentity = Invoke-Az @('identity','create','--resource-group',$targetResourceGroup,'--name',$AutonomousManagedIdentityName,'--location',$Location,'--output','json') -Json
$interactiveIdentity = Invoke-Az @('identity','create','--resource-group',$targetResourceGroup,'--name',$InteractiveManagedIdentityName,'--location',$Location,'--output','json') -Json
$webIdentity = Invoke-Az @('identity','create','--resource-group',$targetResourceGroup,'--name',$WebManagedIdentityName,'--location',$Location,'--output','json') -Json
$apiIdentity = Invoke-Az @('identity','create','--resource-group',$targetResourceGroup,'--name',$ApiManagedIdentityName,'--location',$Location,'--output','json') -Json

foreach ($identity in @($autonomousIdentity,$interactiveIdentity,$webIdentity,$apiIdentity)) {
    $existing = Invoke-Az @('role','assignment','list','--assignee-object-id',$identity.principalId,'--scope',$acr.id,'--query',"[?roleDefinitionName=='AcrPull'].id | [0]",'--output','tsv')
    if ([string]::IsNullOrWhiteSpace($existing)) {
        Invoke-Az @('role','assignment','create','--assignee-object-id',$identity.principalId,'--assignee-principal-type','ServicePrincipal','--role','AcrPull','--scope',$acr.id,'--output','none') | Out-Null
    }
}

$evidence = [pscustomobject]@{
    SubscriptionId = $account.id
    TenantId = $account.tenantId
    ResourceGroup = $targetResourceGroup
    Location = $Location
    LogAnalyticsWorkspace = $workspace.name
    ContainerAppsEnvironment = $environment.name
    ContainerAppsEnvironmentDomain = $environment.properties.defaultDomain
    AcrName = $acr.name
    AcrLoginServer = $acr.loginServer
    AutonomousManagedIdentity = [pscustomobject]@{ Name=$autonomousIdentity.name; ClientId=$autonomousIdentity.clientId; PrincipalId=$autonomousIdentity.principalId; ResourceId=$autonomousIdentity.id }
    InteractiveManagedIdentity = [pscustomobject]@{ Name=$interactiveIdentity.name; ClientId=$interactiveIdentity.clientId; PrincipalId=$interactiveIdentity.principalId; ResourceId=$interactiveIdentity.id }
    WebManagedIdentity = [pscustomobject]@{ Name=$webIdentity.name; ClientId=$webIdentity.clientId; PrincipalId=$webIdentity.principalId; ResourceId=$webIdentity.id }
    ApiManagedIdentity = [pscustomobject]@{ Name=$apiIdentity.name; ClientId=$apiIdentity.clientId; PrincipalId=$apiIdentity.principalId; ResourceId=$apiIdentity.id }
}
New-Item -ItemType Directory -Path $EvidencePath -Force | Out-Null
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidencePath 'azure-foundation.json') -Encoding utf8
$evidence | ConvertTo-Json -Depth 8
Write-Host 'Foundation ready. Configure PostgreSQL and secure its connection string before deploying. No PostgreSQL password is emitted or stored by this script.' -ForegroundColor Green
