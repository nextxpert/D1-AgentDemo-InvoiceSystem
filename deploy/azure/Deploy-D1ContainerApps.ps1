#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
    [Parameter(Mandatory)][ValidatePattern('^(?:\d{1,3}\.){3}\d{1,3}$')][string]$AdminClientIp,
    [string]$ResourceGroup = 'rg-d1-invoice-demo',
    [string]$Location = 'westeurope',
    [string]$ContainerAppsEnvironment = 'cae-d1-invoice-demo',
    [string[]]$ContainerAppsEgressIps = @(),
    [string]$PostgresServerName = 'pg-d1-invoice-demo',
    [ValidatePattern('^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$')][string]$WebPublicHost,
    [ValidatePattern('^[a-z][a-z0-9]{2,23}$')][string]$KeyVaultName,
    [string]$EntryAgentClientId,
    [string]$ApprovalAgentClientId,
    [securestring]$WebClientSecret,
    [string]$FoundationEvidencePath = (Join-Path $PSScriptRoot 'evidence/azure-foundation.json'),
    [string]$EntraEvidencePath = (Join-Path $PSScriptRoot '..' 'entra/evidence/entra-authorization.json'),
    [string]$ImageManifestPath = (Join-Path $PSScriptRoot '..' 'image-manifest.json'),
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
    $diagnostics = ($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() }) -join "`n"
    if ($diagnostics) { Write-Warning $diagnostics }
    $text = ($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() }) -join "`n"
    if ($Json -and $text.Trim()) { return $text | ConvertFrom-Json }
    return $text.Trim()
}

function Get-Secret([string]$Vault, [string]$Name) {
    try { return Invoke-Az @('keyvault','secret','show','--vault-name',$Vault,'--name',$Name,'--query','value','--output','tsv') }
    catch { return $null }
}

function Set-SecretFromMemory([string]$Vault, [string]$Name, [string]$Value) {
    $tempPath = Join-Path ([IO.Path]::GetTempPath()) ("d1-secret-" + [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($tempPath, $Value, [Text.UTF8Encoding]::new($false))
        if ($IsLinux -or $IsMacOS) { & chmod 600 $tempPath; if ($LASTEXITCODE -ne 0) { throw 'Could not restrict permissions on temporary secret file.' } }
        Invoke-Az @('keyvault','secret','set','--vault-name',$Vault,'--name',$Name,'--file',$tempPath,'--encoding','utf-8','--output','none') | Out-Null
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
    }
}

function New-DbPassword {
    $alphabet = ('abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#%+=').ToCharArray()
    $bytes = [byte[]]::new(48)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $builder = [Text.StringBuilder]::new($bytes.Length)
    foreach ($byte in $bytes) { [void]$builder.Append($alphabet[[int]$byte % $alphabet.Length]) }
    return $builder.ToString()
}

function Ensure-RoleAssignment([string]$PrincipalId, [string]$Scope, [string]$Role, [ValidateSet('ServicePrincipal','User')][string]$PrincipalType='ServicePrincipal') {
    $assignment = Invoke-Az @('role','assignment','list','--assignee-object-id',$PrincipalId,'--scope',$Scope,'--query',"[?roleDefinitionName=='$Role'].id | [0]",'--output','tsv')
    if ([string]::IsNullOrWhiteSpace($assignment)) {
        Invoke-Az @('role','assignment','create','--assignee-object-id',$PrincipalId,'--assignee-principal-type',$PrincipalType,'--role',$Role,'--scope',$Scope,'--output','none') | Out-Null
    }
}

function Ensure-SecretReader([string]$PrincipalId, [string]$Vault, [string]$SecretName) {
    $vaultResourceId = Invoke-Az @('keyvault','show','--name',$Vault,'--resource-group',$ResourceGroup,'--query','id','--output','tsv')
    $scope = "$vaultResourceId/secrets/$SecretName"
    Ensure-RoleAssignment $PrincipalId $scope 'Key Vault Secrets User'
}

function Get-ManifestImage([object[]]$Images, [string]$Name) {
    $image = $Images | Where-Object Name -eq $Name | Select-Object -First 1
    if (-not $image -or [string]::IsNullOrWhiteSpace($image.DigestReference)) { throw "Image '$Name' is missing from the image manifest." }
    return $image.DigestReference
}

function Assert-ContainerAppEnvironment($App, [string]$ExpectedEnvironmentId) {
    $environmentProperty = $App.properties.PSObject.Properties['environmentId']
    if (-not $environmentProperty) { $environmentProperty = $App.properties.PSObject.Properties['managedEnvironmentId'] }
    if (-not $environmentProperty -or $environmentProperty.Value -ne $ExpectedEnvironmentId) {
        throw "Container App '$($App.name)' is not in the target environment. Apps cannot be moved between environments in place. Plan an explicit migration before deploying; this script will not delete or move existing apps."
    }
}

function Deploy-ContainerApp {
    param(
        [string]$Name, [string]$Image, [string]$IdentityResourceId, [string]$Registry,
        [string[]]$EnvironmentVariables, [bool]$ExternalIngress, [string[]]$SecretDefinitions = @()
    )
    $existing = $null
    try { $existing = Invoke-Az @('containerapp','show','--resource-group',$ResourceGroup,'--name',$Name,'--output','json') -Json } catch { }
    if ($existing) {
        Assert-ContainerAppEnvironment $existing $targetEnvironmentId
        if ($existing.tags.'d1-demo' -ne 'invoice-system') { throw "Refusing to update Container App '$Name' because it is not tagged as D1-owned." }
        Invoke-Az @('containerapp','identity','assign','--resource-group',$ResourceGroup,'--name',$Name,'--user-assigned',$IdentityResourceId,'--output','none') | Out-Null
        if ($SecretDefinitions.Count -gt 0) {
            $secretArguments = @('containerapp','secret','set','--resource-group',$ResourceGroup,'--name',$Name,'--secrets') + $SecretDefinitions + @('--output','none')
            Invoke-Az $secretArguments | Out-Null
        }
        $ingress = if ($ExternalIngress) { 'external' } else { 'internal' }
        Invoke-Az (@('containerapp','update','--resource-group',$ResourceGroup,'--name',$Name,'--image',$Image,'--set-env-vars') + $EnvironmentVariables + @('--output','none')) | Out-Null
        Invoke-Az @('containerapp','ingress','enable','--resource-group',$ResourceGroup,'--name',$Name,'--type',$ingress,'--target-port','8080','--transport','auto','--output','none') | Out-Null
    }
    else {
        $ingress = if ($ExternalIngress) { 'external' } else { 'internal' }
        $arguments = @('containerapp','create','--resource-group',$ResourceGroup,'--name',$Name,'--environment',$ContainerAppsEnvironment,
            '--image',$Image,'--user-assigned',$IdentityResourceId,'--registry-server',$Registry,'--registry-identity',$IdentityResourceId,
            '--ingress',$ingress,'--target-port','8080','--transport','auto','--min-replicas','1','--max-replicas','2',
            '--cpu','0.5','--memory','1Gi','--tags','d1-demo=invoice-system','--env-vars') + $EnvironmentVariables
        if ($SecretDefinitions.Count -gt 0) { $arguments += @('--secrets') + $SecretDefinitions }
        $arguments += @('--output','none')
        Invoke-Az $arguments | Out-Null
    }
}

Assert-Command 'az'
Invoke-Az @('account','set','--subscription',$SubscriptionId) | Out-Null
$account = Invoke-Az @('account','show','--output','json') -Json
if ($account.tenantId -ne $TenantId) { throw 'Selected Azure account is not in the requested tenant.' }
$foundation = Get-Content -LiteralPath $FoundationEvidencePath -Raw | ConvertFrom-Json
$entra = Get-Content -LiteralPath $EntraEvidencePath -Raw | ConvertFrom-Json
$images = @(Get-Content -LiteralPath $ImageManifestPath -Raw | ConvertFrom-Json)
if (-not $PSBoundParameters.ContainsKey('ContainerAppsEnvironment')) { $ContainerAppsEnvironment = $foundation.ContainerAppsEnvironment }
if ($foundation.SubscriptionId -ne $SubscriptionId -or $foundation.TenantId -ne $TenantId) { throw 'Foundation evidence does not match the selected subscription and tenant.' }
if ($foundation.ResourceGroup -ne $ResourceGroup) { throw 'Resource group differs from foundation evidence. Pass the same D1 resource group.' }
if ($foundation.ContainerAppsEnvironment -ne $ContainerAppsEnvironment) { throw 'Container Apps environment differs from foundation evidence. Pass the same D1 environment.' }
if ($entra.TenantId -ne $TenantId) { throw 'Entra evidence does not match the requested tenant.' }
$targetEnvironmentId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.App/managedEnvironments/$ContainerAppsEnvironment"
$existingApps = @(Invoke-Az @('containerapp','list','--resource-group',$ResourceGroup,'--output','json') -Json)
foreach ($app in $existingApps) {
    if ($app.name -in @('invoice-api','invoice-worker','invoice-interactive','invoice-web')) {
        Assert-ContainerAppEnvironment $app $targetEnvironmentId
    }
}
foreach ($image in $images) {
    if (-not $image.DigestReference.StartsWith($foundation.AcrLoginServer + '/', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Image '$($image.Name)' was not built in the foundation ACR '$($foundation.AcrLoginServer)'."
    }
}
if (-not $EntryAgentClientId -or -not $ApprovalAgentClientId) {
    throw 'Pass the child Agent Identity client IDs from the Entra manual checkpoint.'
}
$parsedAdminIp = $null
if (-not [Net.IPAddress]::TryParse($AdminClientIp, [ref]$parsedAdminIp) -or $parsedAdminIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'AdminClientIp must be a valid IPv4 address.'
}

if (-not $KeyVaultName) {
    $KeyVaultName = 'kvd1invoice' + $SubscriptionId.Replace('-','').Substring(0,12).ToLowerInvariant()
}
try { $vault = Invoke-Az @('keyvault','show','--name',$KeyVaultName,'--resource-group',$ResourceGroup,'--output','json') -Json }
catch { $vault = Invoke-Az @('keyvault','create','--name',$KeyVaultName,'--resource-group',$ResourceGroup,'--location',$Location,'--enable-rbac-authorization','true','--enable-purge-protection','true','--tags','d1-demo=invoice-system','--output','json') -Json }
if ($vault.tags.'d1-demo' -ne 'invoice-system') { throw "Refusing to use Key Vault '$KeyVaultName' because it is not tagged as D1-owned." }
$operator = Invoke-Az @('ad','signed-in-user','show','--query','id','--output','tsv')
Ensure-RoleAssignment $operator $vault.id 'Key Vault Secrets Officer' 'User'
$webSecret = Get-Secret $KeyVaultName 'web-client-secret'
if (-not $webSecret) {
    if (-not $WebClientSecret) { $WebClientSecret = Read-Host 'Enter the D1 Web OIDC client secret (input is hidden)' -AsSecureString }
    $webSecret = [System.Net.NetworkCredential]::new('', $WebClientSecret).Password
    if ([string]::IsNullOrWhiteSpace($webSecret)) { throw 'A Web client secret is required.' }
    Set-SecretFromMemory $KeyVaultName 'web-client-secret' $webSecret
}

$dbPassword = Get-Secret $KeyVaultName 'postgres-admin-password'
if (-not $dbPassword) {
    $dbPassword = New-DbPassword
    Set-SecretFromMemory $KeyVaultName 'postgres-admin-password' $dbPassword
}
$dbExists = $false
try { $postgres = Invoke-Az @('postgres','flexible-server','show','--resource-group',$ResourceGroup,'--name',$PostgresServerName,'--output','json') -Json; $dbExists = $true } catch { }
if ($dbExists -and $postgres.tags.'d1-demo' -ne 'invoice-system') { throw "Refusing to use PostgreSQL server '$PostgresServerName' because it is not tagged as D1-owned." }
if (-not $dbExists) {
    Invoke-Az @('postgres','flexible-server','create','--resource-group',$ResourceGroup,'--name',$PostgresServerName,'--location',$Location,
        '--admin-user','d1invoiceadmin','--admin-password',$dbPassword,'--sku-name','Standard_B1ms','--tier','Burstable',
    '--storage-size','32','--version','16','--backup-retention','7','--public-access',$AdminClientIp,'--tags','d1-demo=invoice-system','--yes','--output','none') | Out-Null
}
$existingDatabase = Invoke-Az @('postgres','flexible-server','db','list','--resource-group',$ResourceGroup,'--server-name',$PostgresServerName,
    '--query',"[?name=='invoice_demo'].name | [0]",'--output','tsv')
if ([string]::IsNullOrWhiteSpace($existingDatabase)) {
    Invoke-Az @('postgres','flexible-server','db','create','--resource-group',$ResourceGroup,'--server-name',$PostgresServerName,'--database-name','invoice_demo','--output','none') | Out-Null
}
$postgres = Invoke-Az @('postgres','flexible-server','show','--resource-group',$ResourceGroup,'--name',$PostgresServerName,'--output','json') -Json
$dbConnection = "Host=$($postgres.fullyQualifiedDomainName);Port=5432;Database=invoice_demo;Username=d1invoiceadmin;Password=$dbPassword;SSL Mode=Require;Trust Server Certificate=true"
Set-SecretFromMemory $KeyVaultName 'database-connection-string' $dbConnection

$vault = Invoke-Az @('keyvault','show','--name',$KeyVaultName,'--resource-group',$ResourceGroup,'--output','json') -Json
Ensure-SecretReader $foundation.ApiManagedIdentity.PrincipalId $KeyVaultName 'database-connection-string'
Ensure-SecretReader $foundation.AutonomousManagedIdentity.PrincipalId $KeyVaultName 'database-connection-string'
Ensure-SecretReader $foundation.InteractiveManagedIdentity.PrincipalId $KeyVaultName 'database-connection-string'
Ensure-SecretReader $foundation.WebManagedIdentity.PrincipalId $KeyVaultName 'web-client-secret'
$environment = Invoke-Az @('containerapp','env','show','--resource-group',$ResourceGroup,'--name',$ContainerAppsEnvironment,'--output','json') -Json
$egressAddresses = $ContainerAppsEgressIps
if ($egressAddresses.Count -eq 0) {
    $foundationEgressProperty = $foundation.PSObject.Properties['ContainerAppsEgressIps']
    if ($foundationEgressProperty) { $egressAddresses = @($foundationEgressProperty.Value) }
}
if ($egressAddresses.Count -eq 0) {
    $outboundProperty = $environment.properties.PSObject.Properties['outboundIpAddresses']
    if ($outboundProperty) { $egressAddresses = @($outboundProperty.Value) }
}
$egressIps = @($egressAddresses | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
if ($egressIps.Count -eq 0) {
    throw 'Container Apps environment did not report outbound IP addresses. Pass -ContainerAppsEgressIps with verified outbound IPv4 addresses (for example, from a configured NAT gateway). The environment staticIp is an inbound address, not a verified egress address. Database firewall rules will not be changed.'
}
foreach ($ip in $egressIps) {
    $parsedEgressIp = $null
    if ($ip -notmatch '^(?:\d{1,3}\.){3}\d{1,3}$' -or -not [Net.IPAddress]::TryParse($ip, [ref]$parsedEgressIp) -or $parsedEgressIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw "Unexpected Container Apps outbound IPv4 address '$ip'. Database firewall rules will not be changed."
    }
}
foreach ($ip in $egressIps) {
    $ruleName = 'd1ca-' + ($ip -replace '\.','-')
    $existingRule = Invoke-Az @('postgres','flexible-server','firewall-rule','list','--resource-group',$ResourceGroup,'--name',$PostgresServerName,'--query',"[?name=='$ruleName'].name | [0]",'--output','tsv')
    if ([string]::IsNullOrWhiteSpace($existingRule)) {
        Invoke-Az @('postgres','flexible-server','firewall-rule','create','--resource-group',$ResourceGroup,'--name',$PostgresServerName,
            '--rule-name',$ruleName,'--start-ip-address',$ip,'--end-ip-address',$ip,'--output','none') | Out-Null
    }
}
$firewallRules = Invoke-Az @('postgres','flexible-server','firewall-rule','list','--resource-group',$ResourceGroup,'--name',$PostgresServerName,'--output','json') -Json
foreach ($rule in $firewallRules) {
    $isContainerAppsEgress = $rule.startIpAddress -eq $rule.endIpAddress -and $rule.startIpAddress -in $egressIps
    if (-not $isContainerAppsEgress) {
        Invoke-Az @('postgres','flexible-server','firewall-rule','delete','--resource-group',$ResourceGroup,'--name',$PostgresServerName,'--rule-name',$rule.name,'--yes','--output','none') | Out-Null
    }
}

$identity = $foundation.AutonomousManagedIdentity
$apiImage = Get-ManifestImage $images 'api'
$workerImage = Get-ManifestImage $images 'worker'
$interactiveImage = Get-ManifestImage $images 'interactive'
$webImage = Get-ManifestImage $images 'web'
$apiDatabaseSecret = 'database-connection=keyvaultref:' + $vault.properties.vaultUri + 'secrets/database-connection-string,identityref:' + $foundation.ApiManagedIdentity.ResourceId
$workerDatabaseSecret = 'database-connection=keyvaultref:' + $vault.properties.vaultUri + 'secrets/database-connection-string,identityref:' + $foundation.AutonomousManagedIdentity.ResourceId
$interactiveDatabaseSecret = 'database-connection=keyvaultref:' + $vault.properties.vaultUri + 'secrets/database-connection-string,identityref:' + $foundation.InteractiveManagedIdentity.ResourceId
$webSecretReference = 'web-client-secret=keyvaultref:' + $vault.properties.vaultUri + 'secrets/web-client-secret,identityref:' + $foundation.WebManagedIdentity.ResourceId
$apiBaseUrl = "https://invoice-api.internal.$($foundation.ContainerAppsEnvironmentDomain)/"
$interactiveApiBaseUrl = "https://invoice-interactive.internal.$($foundation.ContainerAppsEnvironmentDomain)/"
$workerBaseUrl = "https://invoice-worker.internal.$($foundation.ContainerAppsEnvironmentDomain)/"
$common = @('DEMO_MODE=live','DATABASE_PROVIDER=PostgreSQL','DATABASE_CONNECTION_STRING=secretref:database-connection',"TENANT_ID=$TenantId")
$apiEnv = $common + @("INVOICE_API_CLIENT_ID=$($entra.ApiApp.AppId)","INVOICE_API_AUDIENCE=$($entra.ApiApp.IdentifierUri)",'ASPNETCORE_ENVIRONMENT=Production')
$workerEnv = $common + @("AUTONOMOUS_BLUEPRINT_CLIENT_ID=$($entra.AutonomousBlueprint.AppId)",
    "AZURE_CLIENT_ID=$($identity.ClientId)","ENTRY_AGENT_CLIENT_ID=$EntryAgentClientId","APPROVAL_AGENT_CLIENT_ID=$ApprovalAgentClientId",
    "INVOICE_API_CLIENT_ID=$($entra.ApiApp.AppId)","INVOICE_API_AUDIENCE=$($entra.ApiApp.IdentifierUri)",
    "INVOICE_API_BASE_URL=$apiBaseUrl",'ASPNETCORE_ENVIRONMENT=Production')
$interactiveEnv = $common + @("INTERACTIVE_BLUEPRINT_CLIENT_ID=$($entra.InteractiveBlueprint.AppId)",
    "INTERACTIVE_BLUEPRINT_AUDIENCE=api://$($entra.InteractiveBlueprint.AppId)",
    "INTERACTIVE_AZURE_CLIENT_ID=$($foundation.InteractiveManagedIdentity.ClientId)",
    "INTERACTIVE_AGENT_CLIENT_ID=$($entra.InteractiveAgent.ClientId)",
    "INVOICE_CREATE_DELEGATED_SCOPE=$($entra.ApiApp.IdentifierUri)/Invoice.Create.Delegated",
    "INVOICE_API_CLIENT_ID=$($entra.ApiApp.AppId)","INVOICE_API_AUDIENCE=$($entra.ApiApp.IdentifierUri)",
    "INVOICE_API_BASE_URL=$apiBaseUrl",'ASPNETCORE_ENVIRONMENT=Production')
$publicHost = if ($WebPublicHost) { $WebPublicHost } else { 'invoice-web.' + $foundation.ContainerAppsEnvironmentDomain }
$webEnv = @('DEMO_MODE=live',"WEB_CLIENT_ID=$($entra.WebClient.AppId)",'WEB_CLIENT_SECRET=secretref:web-client-secret',
    "TENANT_ID=$TenantId","INTERACTIVE_BLUEPRINT_ACCESS_SCOPE=api://$($entra.InteractiveBlueprint.AppId)/access_as_user",
    "WEB_PUBLIC_HOST=$publicHost",
    "INVOICE_CREATE_DELEGATED_SCOPE=$($entra.ApiApp.IdentifierUri)/Invoice.Create.Delegated",
    "INVOICE_INTERACTIVE_API_BASE_URL=$interactiveApiBaseUrl","INVOICE_WORKER_BASE_URL=$workerBaseUrl",
    'ASPNETCORE_ENVIRONMENT=Production')
$acrLoginServer = $foundation.AcrLoginServer
Deploy-ContainerApp 'invoice-api' $apiImage $foundation.ApiManagedIdentity.ResourceId $acrLoginServer $apiEnv $false @($apiDatabaseSecret)
Deploy-ContainerApp 'invoice-worker' $workerImage $identity.ResourceId $acrLoginServer $workerEnv $false @($workerDatabaseSecret)
Deploy-ContainerApp 'invoice-interactive' $interactiveImage $foundation.InteractiveManagedIdentity.ResourceId $acrLoginServer $interactiveEnv $false @($interactiveDatabaseSecret)
Deploy-ContainerApp 'invoice-web' $webImage $foundation.WebManagedIdentity.ResourceId $acrLoginServer $webEnv $true @($webSecretReference)

$web = Invoke-Az @('containerapp','show','--resource-group',$ResourceGroup,'--name','invoice-web','--output','json') -Json
$webUrl = 'https://' + $publicHost
New-Item -ItemType Directory -Path $EvidencePath -Force | Out-Null
[pscustomobject]@{
    ResourceGroup=$ResourceGroup; Location=$Location; ContainerAppsEnvironment=$ContainerAppsEnvironment
    PostgresServer=$postgres.fullyQualifiedDomainName; DatabaseName='invoice_demo'; KeyVault=$KeyVaultName
    ContainerApps=@('invoice-api','invoice-worker','invoice-interactive','invoice-web')
    WebUrl=$webUrl; ImageDigests=@($images | ForEach-Object { $_.DigestReference });
    AutonomousManagedIdentity=$foundation.AutonomousManagedIdentity.Name
    InteractiveManagedIdentity=$foundation.InteractiveManagedIdentity.Name
    WebRuntimeManagedIdentity=$foundation.WebManagedIdentity.Name
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidencePath 'container-app-deployment.json') -Encoding utf8
Write-Host "D1 Container Apps deployment complete: $webUrl" -ForegroundColor Green
Write-Host 'Verify /healthz and /readyz for each app and run the live delegated/autonomous demo checks before presenting.' -ForegroundColor Yellow
