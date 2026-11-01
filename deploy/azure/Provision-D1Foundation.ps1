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
    [switch]$EnableStableEgress,
    [switch]$ConfirmStableEgressDeployment,
    [string]$StableEgressEnvironmentName = 'cae-d1-invoice-demo-nat',
    [string]$VirtualNetworkName = 'vnet-d1-invoice-demo',
    [string]$NatGatewayName = 'nat-d1-invoice-demo',
    [string]$EgressPublicIpName = 'pip-d1-invoice-egress',
    [string]$EvidencePath = (Join-Path $PSScriptRoot 'evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "Required command '$Name' is missing." }
}

function Invoke-Az([string[]]$Arguments, [switch]$Json) {
    $stderrPath = [IO.Path]::GetTempFileName()
    try {
        $output = & az @Arguments 2> $stderrPath
        $exitCode = $LASTEXITCODE
        $stderr = [IO.File]::ReadAllText($stderrPath).Trim()
        $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
        if ($exitCode -ne 0) {
            $details = @($stderr, $text | Where-Object { $_ }) -join [Environment]::NewLine
            throw "az $($Arguments -join ' ') failed: $details"
        }
        if ($stderr) { Write-Warning $stderr }
        if ($Json -and $text.Trim()) { return $text | ConvertFrom-Json }
        return $text.Trim()
    }
    finally {
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-AdditiveNetworkPreview($Preview) {
    $changesProperty = $Preview.PSObject.Properties['changes']
    if (-not $changesProperty) { throw 'Networking what-if did not return a changes collection.' }
    foreach ($change in @($changesProperty.Value)) {
        if ($change.changeType -notin @('Create', 'NoChange', 'Ignore')) {
            throw "Refusing networking deployment: what-if reports $($change.changeType) for $($change.resourceId). Existing resources must not be modified or deleted."
        }
    }
}

function Invoke-StableEgressFoundation {
    $foundationPath = Join-Path $EvidencePath 'azure-foundation.json'
    $foundation = Get-Content -LiteralPath $foundationPath -Raw | ConvertFrom-Json
    if ($foundation.SubscriptionId -ne $SubscriptionId -or $foundation.TenantId -ne $TenantId -or $foundation.ResourceGroup -ne $ResourceGroup -or $foundation.Location -ne $Location) {
        throw 'Stable egress must use the subscription, tenant, resource group and region from existing foundation evidence.'
    }
    if ($StableEgressEnvironmentName -eq $ContainerAppsEnvironment -or $ReuseContainerAppsEnvironment) {
        throw 'Use a separately named StableEgressEnvironmentName, not the original default-network environment or ReuseContainerAppsEnvironment.'
    }
    if ($ConfirmStableEgressDeployment) {
        Invoke-Az @('provider','register','--namespace','Microsoft.Network','--wait','--output','none') | Out-Null
    }
    $resources = @(Invoke-Az @('resource','list','--resource-group',$ResourceGroup,'--output','json') -Json)
    foreach ($resource in $resources) {
        if ($resource.name -in @($StableEgressEnvironmentName, $VirtualNetworkName, $NatGatewayName, $EgressPublicIpName)) {
            $tagsProperty = $resource.PSObject.Properties['tags']
            $demoTag = if ($tagsProperty -and $tagsProperty.Value) { $tagsProperty.Value.PSObject.Properties['d1-demo'] } else { $null }
            if (-not $demoTag -or $demoTag.Value -ne 'invoice-system') {
                throw "Refusing to use existing resource '$($resource.name)' without the D1 ownership tag."
            }
        }
    }
    $templatePath = (Resolve-Path (Join-Path $PSScriptRoot '../..' 'infra/main.bicep')).Path
    $parameters = @(
        "location=$Location", "environmentName=$StableEgressEnvironmentName",
        "logAnalyticsWorkspaceName=$($foundation.LogAnalyticsWorkspace)",
        "virtualNetworkName=$VirtualNetworkName", "natGatewayName=$NatGatewayName", "publicIpName=$EgressPublicIpName"
    )
    $deploymentArguments = @('--resource-group',$ResourceGroup,'--template-file',$templatePath,'--parameters') + $parameters
    Invoke-Az (@('deployment','group','validate') + $deploymentArguments + @('--output','none')) | Out-Null
    $preview = Invoke-Az (@('deployment','group','what-if') + $deploymentArguments + @('--result-format','FullResourcePayloads','--no-pretty-print','--output','json')) -Json
    Assert-AdditiveNetworkPreview $preview
    $preview.changes | Select-Object changeType, resourceId | Format-Table -AutoSize | Out-Host
    if (-not $ConfirmStableEgressDeployment) {
        Write-Host 'Preview only. Review NAT/public IPv4 costs and network address overlap, then rerun with -ConfirmStableEgressDeployment to provision. Foundation evidence is unchanged.' -ForegroundColor Yellow
        return
    }
    $deployment = Invoke-Az (@('deployment','group','create','--name','d1-stable-egress','--mode','Incremental') + $deploymentArguments + @('--output','json')) -Json
    $outputs = $deployment.properties.outputs
    $environment = Invoke-Az @('containerapp','env','show','--resource-group',$ResourceGroup,'--name',$StableEgressEnvironmentName,'--output','json') -Json
    if ($environment.properties.vnetConfiguration.infrastructureSubnetId -ne $outputs.infrastructureSubnetId.value) {
        throw 'New environment does not use the expected NAT-connected infrastructure subnet. Foundation evidence is unchanged.'
    }
    $subnet = Invoke-Az @('network','vnet','subnet','show','--ids',$outputs.infrastructureSubnetId.value,'--output','json') -Json
    if ($subnet.natGateway.id -ne $outputs.natGatewayResourceId.value) { throw 'Infrastructure subnet does not use the expected NAT gateway.' }
    $nat = Invoke-Az @('network','nat','gateway','show','--ids',$outputs.natGatewayResourceId.value,'--output','json') -Json
    if ($outputs.publicIpResourceId.value -notin @($nat.publicIpAddresses.id)) { throw 'NAT gateway does not use the expected egress public IP.' }
    $publicIp = Invoke-Az @('network','public-ip','show','--ids',$outputs.publicIpResourceId.value,'--output','json') -Json
    $parsedIp = $null
    if ($publicIp.publicIPAllocationMethod -ne 'Static' -or $publicIp.sku.name -ne 'Standard' -or -not [Net.IPAddress]::TryParse($publicIp.ipAddress, [ref]$parsedIp) -or $parsedIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'NAT gateway public IP is not a valid static Standard IPv4 address.'
    }
    if ($foundation.ContainerAppsEnvironment -ne $StableEgressEnvironmentName) {
        $foundation | Add-Member -NotePropertyName PreviousContainerAppsEnvironment -NotePropertyValue $foundation.ContainerAppsEnvironment -Force
        $foundation | Add-Member -NotePropertyName PreviousContainerAppsEnvironmentDomain -NotePropertyValue $foundation.ContainerAppsEnvironmentDomain -Force
    }
    $foundation.ContainerAppsEnvironment = $StableEgressEnvironmentName
    $foundation.ContainerAppsEnvironmentDomain = $environment.properties.defaultDomain
    $foundation | Add-Member -NotePropertyName ContainerAppsEgressIps -NotePropertyValue @($publicIp.ipAddress) -Force
    $foundation | Add-Member -NotePropertyName InfrastructureSubnetId -NotePropertyValue $subnet.id -Force
    $foundation | Add-Member -NotePropertyName NatGatewayResourceId -NotePropertyValue $nat.id -Force
    $foundation | Add-Member -NotePropertyName EgressPublicIpResourceId -NotePropertyValue $publicIp.id -Force
    $foundation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $foundationPath -Encoding utf8
    Write-Host 'Stable egress foundation ready. Rerun Entra provisioning to add the new Web redirect URI, then deploy apps to the new environment. The original environment was not deleted.' -ForegroundColor Green
}

Assert-Command 'az'
if ($ConfirmStableEgressDeployment -and -not $EnableStableEgress) { throw 'ConfirmStableEgressDeployment requires EnableStableEgress.' }
Invoke-Az @('login','--tenant',$TenantId,'--output','none') | Out-Null
Invoke-Az @('account','set','--subscription',$SubscriptionId) | Out-Null
$account = Invoke-Az @('account','show','--output','json') -Json
if ($account.id -ne $SubscriptionId -or $account.tenantId -ne $TenantId) { throw 'Selected Azure subscription/tenant does not match the requested values.' }

if ($EnableStableEgress) {
    Invoke-StableEgressFoundation
    return
}

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
