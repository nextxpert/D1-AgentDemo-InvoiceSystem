#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
    [Parameter(Mandatory)][string]$SponsorUserPrincipalName,
    [Parameter(Mandatory)][string]$OwnerUserPrincipalName,
    [Parameter(Mandatory)][string]$InvoiceSubmitterUserPrincipalName,
    [string]$Prefix = 'D1',
    [string]$AutonomousBlueprintName = 'D1-Invoice-Workers-Autonomous',
    [string]$InteractiveBlueprintName = 'D1-Invoice-Assistant-Interactive',
    [string]$ApiDisplayName = 'D1-Synthetic-Invoice-API',
    [string]$WebClientDisplayName = 'D1-Invoice-Web',
    [string]$WebProductionRedirectUri,
    [string]$FoundationEvidencePath = (Join-Path $PSScriptRoot '..' 'azure' 'evidence' 'azure-foundation.json'),
    [string]$EvidencePath = (Join-Path $PSScriptRoot 'evidence'),
    [string]$EntryAgentClientId,
    [string]$EntryAgentObjectId,
    [string]$ApprovalAgentClientId,
    [string]$ApprovalAgentObjectId,
    [string]$InteractiveAgentClientId,
    [string]$InteractiveAgentObjectId,
    [switch]$ContinueAfterManualCheckpoint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$graphScopes = @(
    'AgentIdentityBlueprint.Create', 'AgentIdentityBlueprint.AddRemoveCreds.All',
    'AgentIdentityBlueprint.UpdateAuthProperties.All',
    'AgentIdentityBlueprintPrincipal.Create', 'Application.ReadWrite.All',
    'AppRoleAssignment.ReadWrite.All', 'DelegatedPermissionGrant.ReadWrite.All',
    'User.Read.All'
)

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
    throw 'Install Microsoft.Graph.Authentication with Install-Module Microsoft.Graph.Authentication -Scope CurrentUser.'
}
Import-Module Microsoft.Graph.Authentication
$context = Get-MgContext
if (-not $context -or $context.TenantId -ne $TenantId -or @($graphScopes | Where-Object { $_ -notin $context.Scopes }).Count -gt 0) {
    if ($context) { Disconnect-MgGraph | Out-Null }
    Connect-MgGraph -TenantId $TenantId -Scopes $graphScopes | Out-Null
}
$context = Get-MgContext
if ($context.TenantId -ne $TenantId) { throw 'Microsoft Graph tenant does not match TenantId.' }

function Invoke-Graph {
    param([Parameter(Mandatory)][ValidateSet('GET','POST','PATCH')][string]$Method,
        [Parameter(Mandatory)][string]$Uri, $Body)
    $parameters = @{ Method=$Method; Uri=$Uri; Headers=@{'OData-Version'='4.0'} }
    if ($null -ne $Body) {
        $parameters.Body = $Body | ConvertTo-Json -Depth 20
        $parameters.ContentType = 'application/json'
    }
    return Invoke-MgGraphRequest @parameters
}

function Get-GraphCollectionValue($Result, [string]$Uri) {
    if ($null -eq $Result) { throw "Microsoft Graph collection request '$Uri' returned no response." }
    if ($Result -is [string]) { $Result = $Result | ConvertFrom-Json }

    if ($Result -is [System.Collections.IDictionary]) {
        if ($Result.Contains('value')) { return @($Result['value']) }
        $propertyNames = @($Result.Keys)
    }
    else {
        $valueProperty = $Result.PSObject.Properties['value']
        if ($valueProperty) { return @($valueProperty.Value) }
        $propertyNames = @($Result.PSObject.Properties | ForEach-Object Name)
    }

    if ($Result -is [System.Collections.IList] -and $Result -isnot [string]) { return @($Result) }
    $properties = if ($propertyNames.Count -gt 0) { $propertyNames -join ', ' } else { '<none>' }
    throw "Microsoft Graph collection request '$Uri' returned '$($Result.GetType().FullName)' without a 'value' collection. Response properties: $properties."
}

function Get-ApplicationByName([string]$Name) {
    $escaped = $Name.Replace("'", "''")
    $uri = "https://graph.microsoft.com/v1.0/applications?`$filter=displayName eq '$escaped'&`$select=id,appId,displayName,identifierUris,api,appRoles,web,requiredResourceAccess,signInAudience"
    $items = @(Get-GraphCollectionValue (Invoke-Graph GET $uri) $uri)
    if ($items.Count -gt 1) { throw "Multiple app registrations named '$Name' exist." }
    $application = $items | Select-Object -First 1
    $hasAppId = if ($application -is [System.Collections.IDictionary]) {
        $application.Contains('appId')
    }
    else {
        $null -ne $application.PSObject.Properties['appId']
    }
    if ($application -and -not $hasAppId) {
        $properties = if ($application -is [System.Collections.IDictionary]) {
            @($application.Keys) -join ', '
        }
        else {
            @($application.PSObject.Properties | ForEach-Object Name) -join ', '
        }
        throw "Microsoft Graph application lookup for '$Name' omitted appId. Response properties: $properties."
    }
    return $application
}

function Get-ServicePrincipalByAppId([string]$AppId) {
    $uri = "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId%20eq%20'$AppId'&`$select=id,appId,displayName,servicePrincipalType,accountEnabled,appRoles"
    $items = @(Get-GraphCollectionValue (Invoke-Graph GET $uri) $uri)
    return $items | Select-Object -First 1
}

function Ensure-AgentBlueprint([string]$Name, [string]$SponsorId, [string]$OwnerId) {
    $app = Get-ApplicationByName $Name
    if (-not $app) {
        Invoke-Graph POST 'https://graph.microsoft.com/v1.0/applications/microsoft.graph.agentIdentityBlueprint' @{
            displayName = $Name
            'sponsors@odata.bind' = @("https://graph.microsoft.com/v1.0/users/$SponsorId")
            'owners@odata.bind' = @("https://graph.microsoft.com/v1.0/users/$OwnerId")
        } | Out-Null
        $app = Get-ApplicationByName $Name
        if (-not $app) { throw "Microsoft Graph created blueprint '$Name' but it was not returned by the follow-up application lookup." }
    }
    $principal = Get-ServicePrincipalByAppId $app.appId
    if (-not $principal) {
        $principal = Invoke-Graph POST 'https://graph.microsoft.com/v1.0/servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal' @{ appId=$app.appId }
    }
    return [pscustomobject]@{ DisplayName=$Name; AppId=$app.appId; ObjectId=$app.id; PrincipalObjectId=$principal.id }
}

function Ensure-FederatedCredential([string]$BlueprintAppId, [string]$Name, [string]$Subject) {
    if ([string]::IsNullOrWhiteSpace($Subject)) { throw "Missing managed identity principal ID for $Name." }
    $uri = "https://graph.microsoft.com/v1.0/applications/$BlueprintAppId/federatedIdentityCredentials"
    $items = @(Get-GraphCollectionValue (Invoke-Graph GET $uri) $uri)
    $existing = $items | Where-Object name -eq $Name | Select-Object -First 1
    $expectedIssuer = "https://login.microsoftonline.com/$TenantId/v2.0"
    if ($existing) {
        if ($existing.issuer -ne $expectedIssuer -or $existing.subject -ne $Subject -or 'api://AzureADTokenExchange' -notin $existing.audiences) {
            throw "FIC '$Name' exists but does not match the expected D1 issuer, UAMI principal ID, and audience. Review manually; the script will not overwrite it."
        }
        return $existing
    }
    return Invoke-Graph POST $uri @{ name=$Name; issuer=$expectedIssuer; subject=$Subject; audiences=@('api://AzureADTokenExchange') }
}

function Set-AgentActorClaim([string]$ApplicationObjectId) {
    $uri = "https://graph.microsoft.com/v1.0/applications/${ApplicationObjectId}"
    $app = Invoke-Graph GET "${uri}?`$select=optionalClaims"
    $optionalClaims = @{ accessToken=@() }
    if ($app.optionalClaims) {
        foreach ($property in $app.optionalClaims.PSObject.Properties) {
            $optionalClaims[$property.Name] = $property.Value
        }
    }
    $accessTokenClaims = @($optionalClaims.accessToken | Where-Object { $null -ne $_ })
    if ('xms_act_fct' -in @($accessTokenClaims | ForEach-Object { $_.name })) { return }
    $optionalClaims.accessToken = $accessTokenClaims + @(@{ name='xms_act_fct'; essential=$false })
    Invoke-Graph PATCH $uri @{ optionalClaims=$optionalClaims } | Out-Null
}

function Ensure-ApplicationWithApi([string]$DisplayName, [string]$IdentifierUri) {
    $roleDefinitions = @(
        @{ value='Invoice.Create.All'; description='Create and submit fictional invoices autonomously.'; displayName='Create invoices'; members=@('Application') },
        @{ value='Invoice.Read.All'; description='Read fictional invoices for approval.'; displayName='Read all invoices'; members=@('Application') },
        @{ value='Invoice.Approve.All'; description='Approve or reject pending fictional invoices.'; displayName='Approve invoices'; members=@('Application') },
        @{ value='InvoiceSubmitter'; description='Business role permitting a signed-in user to submit invoices.'; displayName='Invoice submitter'; members=@('User') }
    )
    $scopeDefinition = @{ value='Invoice.Create.Delegated'; id=[guid]::NewGuid().Guid; type='User'; isEnabled=$true;
        adminConsentDisplayName='Submit fictional invoices'; adminConsentDescription='Submit a fictional invoice on behalf of the signed-in user.';
        userConsentDisplayName='Submit fictional invoices'; userConsentDescription='Submit a fictional invoice on your behalf.' }

    $app = Get-ApplicationByName $DisplayName
    if (-not $app) {
        $appRoles = foreach ($role in $roleDefinitions) {
            @{ id=[guid]::NewGuid().Guid; value=$role.value; description=$role.description; displayName=$role.displayName; allowedMemberTypes=$role.members; isEnabled=$true }
        }
        Invoke-Graph POST 'https://graph.microsoft.com/v1.0/applications' @{
            displayName=$DisplayName; signInAudience='AzureADMyOrg'; identifierUris=@($IdentifierUri)
            api=@{ requestedAccessTokenVersion=2; oauth2PermissionScopes=@($scopeDefinition) }
            appRoles=@($appRoles)
        } | Out-Null
        $app = Get-ApplicationByName $DisplayName
        if (-not $app) { throw "Microsoft Graph created application '$DisplayName' but it was not returned by the follow-up lookup." }
    }
    else {
        $roles = @($app.appRoles)
        $roleValues = @($roles | ForEach-Object { $_.value })
        foreach ($role in $roleDefinitions) {
            if ($role.value -notin $roleValues) {
                $roles += @{ id=[guid]::NewGuid().Guid; value=$role.value; description=$role.description; displayName=$role.displayName; allowedMemberTypes=$role.members; isEnabled=$true }
            }
        }
        $scopes = @($app.api.oauth2PermissionScopes)
        $scopeValues = @($scopes | ForEach-Object { $_.value })
        if ('Invoice.Create.Delegated' -notin $scopeValues) { $scopes += $scopeDefinition }
        if ($IdentifierUri -notin @($app.identifierUris)) { $app.identifierUris += $IdentifierUri }
        Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$($app.id)" @{
            identifierUris=@($app.identifierUris); appRoles=$roles
            api=@{ requestedAccessTokenVersion=2; oauth2PermissionScopes=$scopes }
        } | Out-Null
        $app = Get-ApplicationByName $DisplayName
    }
    if ($IdentifierUri -notin @($app.identifierUris)) {
        Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$($app.id)" @{ identifierUris=@($IdentifierUri) } | Out-Null
        $app = Get-ApplicationByName $DisplayName
    }
    Set-AgentActorClaim $app.id
    $sp = Get-ServicePrincipalByAppId $app.appId
    if (-not $sp) { $sp = Invoke-Graph POST 'https://graph.microsoft.com/v1.0/servicePrincipals' @{ appId=$app.appId } }
    return [pscustomobject]@{ Application=$app; ServicePrincipal=$sp }
}

function Ensure-InteractiveBlueprintApi([string]$BlueprintObjectId, [string]$BlueprintAppId, [string]$WebClientAppId) {
    $app = Invoke-Graph GET "https://graph.microsoft.com/v1.0/applications/${BlueprintObjectId}?`$select=id,appId,identifierUris,api,optionalClaims"
    $scopes = @($app.api.oauth2PermissionScopes)
    $access = $scopes | Where-Object value -eq 'access_as_user' | Select-Object -First 1
    if (-not $access) {
        $access = @{ id=[guid]::NewGuid().Guid; value='access_as_user'; type='User'; isEnabled=$true;
            adminConsentDisplayName='Access invoice assistant'; adminConsentDescription='Allow the app to access the invoice assistant on behalf of the user.';
            userConsentDisplayName='Access invoice assistant'; userConsentDescription='Allow the app to access the invoice assistant on your behalf.' }
        $scopes += $access
    }
    $preAuthorized = @($app.api.preAuthorizedApplications)
    $preAuthorizedForUpdate = @()
    $preAuthorizationChanged = $false
    $hasWebPreAuthorization = $false
    if ($WebClientAppId) {
        foreach ($preAuthorizedApplication in $preAuthorized) {
            if ($preAuthorizedApplication.appId -eq $WebClientAppId) {
                $hasWebPreAuthorization = $true
                $delegatedPermissionIds = @($preAuthorizedApplication.delegatedPermissionIds)
                if ($access.id -notin $delegatedPermissionIds) {
                    $delegatedPermissionIds += $access.id
                    $preAuthorizationChanged = $true
                }
                $preAuthorizedForUpdate += @{ appId=$WebClientAppId; delegatedPermissionIds=$delegatedPermissionIds }
            }
            else {
                $preAuthorizedForUpdate += $preAuthorizedApplication
            }
        }
        if (-not $hasWebPreAuthorization) {
            $preAuthorizedForUpdate += @{ appId=$WebClientAppId; delegatedPermissionIds=@($access.id) }
            $preAuthorizationChanged = $true
        }
    }
    else {
        $preAuthorizedForUpdate = $preAuthorized
    }
    $identifier = "api://$BlueprintAppId"
    Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$BlueprintObjectId" @{
        identifierUris=@($identifier)
        api=@{ requestedAccessTokenVersion=2; oauth2PermissionScopes=$scopes; preAuthorizedApplications=$preAuthorized }
        optionalClaims=@{ accessToken=@(@{ name='idtyp'; essential=$false; additionalProperties=@('include_user_token') }) }
    } | Out-Null
    if ($preAuthorizationChanged) {
        Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$BlueprintObjectId" @{
            api=@{ requestedAccessTokenVersion=2; oauth2PermissionScopes=$scopes; preAuthorizedApplications=$preAuthorizedForUpdate }
        } | Out-Null
    }
    return $access
}

function Ensure-AppRoleAssignment([string]$PrincipalObjectId, [string]$ResourceSpId, [string]$RoleId, [ValidateSet('servicePrincipals','users')][string]$PrincipalType='servicePrincipals') {
    $uri = "https://graph.microsoft.com/v1.0/$PrincipalType/$PrincipalObjectId/appRoleAssignments"
    $existing = @(Get-GraphCollectionValue (Invoke-Graph GET $uri) $uri)
    if ($existing | Where-Object { $_.resourceId -eq $ResourceSpId -and $_.appRoleId -eq $RoleId }) { return }
    Invoke-Graph POST $uri @{ principalId=$PrincipalObjectId; resourceId=$ResourceSpId; appRoleId=$RoleId } | Out-Null
}

function Ensure-DelegatedGrant([string]$ClientSpId, [string]$ResourceSpId, [string]$Scope) {
    $uri = "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?`$filter=clientId eq '$ClientSpId' and resourceId eq '$ResourceSpId'"
    $grants = @(Get-GraphCollectionValue (Invoke-Graph GET $uri) $uri)
    $grant = $grants | Select-Object -First 1
    if ($grant) {
        $scopes = @($grant.scope -split ' ' | Where-Object { $_ })
        if ($Scope -notin $scopes) {
            Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$($grant.id)" @{ scope=(($scopes + $Scope) -join ' ') } | Out-Null
        }
        return
    }
    Invoke-Graph POST 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' @{
        clientId=$ClientSpId; consentType='AllPrincipals'; resourceId=$ResourceSpId;
        scope=$Scope; expiryTime=(Get-Date).ToUniversalTime().AddYears(5).ToString('yyyy-MM-ddTHH:mm:ssZ')
    } | Out-Null
}

function Ensure-RequiredDelegatedPermission([string]$ClientAppObjectId, [string]$ResourceAppId, [string]$ScopeId) {
    $uri = "https://graph.microsoft.com/v1.0/applications/${ClientAppObjectId}?`$select=id,requiredResourceAccess"
    $client = Invoke-Graph GET $uri
    $resources = @($client.requiredResourceAccess)
    $resource = $resources | Where-Object resourceAppId -eq $ResourceAppId | Select-Object -First 1
    if (-not $resource) {
        $resource = @{ resourceAppId=$ResourceAppId; resourceAccess=@() }
        $resources += $resource
    }
    if ($ScopeId -notin @($resource.resourceAccess | Where-Object type -eq 'Scope' | ForEach-Object id)) {
        $resource.resourceAccess = @($resource.resourceAccess) + @{ id=$ScopeId; type='Scope' }
        Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$ClientAppObjectId" @{ requiredResourceAccess=$resources } | Out-Null
    }
}

function Resolve-Agent([string]$Name, [string]$ClientId, [string]$ObjectId) {
    if ([string]::IsNullOrWhiteSpace($ClientId) -or [string]::IsNullOrWhiteSpace($ObjectId)) { return $null }
    $sp = Invoke-Graph GET "https://graph.microsoft.com/v1.0/servicePrincipals/${ObjectId}?`$select=id,appId,displayName,accountEnabled"
    if ($sp.id -ne $ObjectId -or $sp.appId -ne $ClientId) { throw "$Name object/client ID combination does not match Graph." }
    if ($sp.accountEnabled -eq $false) { throw "$Name is disabled." }
    return $sp
}

$foundation = Get-Content -LiteralPath $FoundationEvidencePath -Raw | ConvertFrom-Json
$sponsor = Invoke-Graph GET "https://graph.microsoft.com/v1.0/users/$([uri]::EscapeDataString($SponsorUserPrincipalName))?`$select=id,userPrincipalName"
$owner = Invoke-Graph GET "https://graph.microsoft.com/v1.0/users/$([uri]::EscapeDataString($OwnerUserPrincipalName))?`$select=id,userPrincipalName"
$submitter = Invoke-Graph GET "https://graph.microsoft.com/v1.0/users/$([uri]::EscapeDataString($InvoiceSubmitterUserPrincipalName))?`$select=id,userPrincipalName"

$autonomous = Ensure-AgentBlueprint $AutonomousBlueprintName $sponsor.id $owner.id
$interactive = Ensure-AgentBlueprint $InteractiveBlueprintName $sponsor.id $owner.id
$autonomousFic = Ensure-FederatedCredential $autonomous.AppId 'fic-d1-invoice-workers' $foundation.AutonomousManagedIdentity.PrincipalId
$interactiveFic = Ensure-FederatedCredential $interactive.AppId 'fic-d1-invoice-interactive' $foundation.InteractiveManagedIdentity.PrincipalId
$apiRegistration = Get-ApplicationByName $ApiDisplayName
if (-not $apiRegistration) {
    Invoke-Graph POST 'https://graph.microsoft.com/v1.0/applications' @{
        displayName=$ApiDisplayName; signInAudience='AzureADMyOrg'
    } | Out-Null
    $apiRegistration = Get-ApplicationByName $ApiDisplayName
    if (-not $apiRegistration) { throw "Microsoft Graph created application '$ApiDisplayName' but it was not returned by the follow-up lookup." }
}
$apiUri = if (@($apiRegistration.identifierUris).Count -gt 0) { $apiRegistration.identifierUris[0] } else { 'api://{0}' -f $apiRegistration.appId }
$api = Ensure-ApplicationWithApi $ApiDisplayName $apiUri
$web = Get-ApplicationByName $WebClientDisplayName
$productionRedirectUri = $WebProductionRedirectUri
if (-not $productionRedirectUri -and $foundation.ContainerAppsEnvironmentDomain) {
    $productionRedirectUri = 'https://invoice-web.{0}/signin-oidc' -f $foundation.ContainerAppsEnvironmentDomain
}
$webRedirectUris = @('http://localhost:8083/signin-oidc')
if ($productionRedirectUri) { $webRedirectUris += $productionRedirectUri }
$webLogoutUrl = if ($productionRedirectUri) { $productionRedirectUri.Replace('/signin-oidc','/signout-callback-oidc') } else { 'http://localhost:8083/signout-callback-oidc' }
if (-not $web) {
    Invoke-Graph POST 'https://graph.microsoft.com/v1.0/applications' @{
        displayName=$WebClientDisplayName; signInAudience='AzureADMyOrg';
        web=@{ redirectUris=$webRedirectUris; logoutUrl=$webLogoutUrl }
    } | Out-Null
    $web = Get-ApplicationByName $WebClientDisplayName
    if (-not $web) { throw "Microsoft Graph created application '$WebClientDisplayName' but it was not returned by the follow-up lookup." }
}
else {
    $existingRedirectUris = @($web.web.redirectUris)
    foreach ($redirectUri in $webRedirectUris) {
        if ($redirectUri -notin $existingRedirectUris) { $existingRedirectUris += $redirectUri }
    }
    Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$($web.id)" @{
        web=@{ redirectUris=$existingRedirectUris; logoutUrl=$webLogoutUrl }
    } | Out-Null
    $web = Get-ApplicationByName $WebClientDisplayName
}
$webSp = Get-ServicePrincipalByAppId $web.appId
if (-not $webSp) { $webSp = Invoke-Graph POST 'https://graph.microsoft.com/v1.0/servicePrincipals' @{ appId=$web.appId } }
$interactiveScope = Ensure-InteractiveBlueprintApi $interactive.ObjectId $interactive.AppId $web.appId
$interactiveBlueprintSp = Get-ServicePrincipalByAppId $interactive.AppId
if (-not $interactiveBlueprintSp) { throw 'Interactive BlueprintPrincipal was not found after provisioning.' }

$apiApp = $api.Application
$apiSp = $api.ServicePrincipal
$apiRoles = @{}
foreach ($roleName in @('Invoice.Create.All','Invoice.Read.All','Invoice.Approve.All','InvoiceSubmitter')) {
    $role = $apiApp.appRoles | Where-Object value -eq $roleName | Select-Object -First 1
    if (-not $role) { throw "Required Invoice API role '$roleName' is missing." }
    $apiRoles[$roleName] = $role.id
}

$entry = Resolve-Agent 'D1-Invoice-Entry-Agent' $EntryAgentClientId $EntryAgentObjectId
$approval = Resolve-Agent 'D1-Invoice-Approval-Agent' $ApprovalAgentClientId $ApprovalAgentObjectId
$interactiveAgent = Resolve-Agent 'D1-Interactive-Invoice-Assistant' $InteractiveAgentClientId $InteractiveAgentObjectId
if (-not $ContinueAfterManualCheckpoint -or -not $entry -or -not $approval -or -not $interactiveAgent) {
    New-Item -ItemType Directory -Path $EvidencePath -Force | Out-Null
    [pscustomobject]@{
        TenantId=$TenantId; AutonomousBlueprint=$autonomous; InteractiveBlueprint=$interactive
        AutonomousFic=$autonomousFic; InteractiveFic=$interactiveFic
        ApiApp=[pscustomobject]@{ AppId=$apiApp.appId; ObjectId=$apiApp.id; ServicePrincipalId=$apiSp.id; IdentifierUri=$apiUri; Roles=$apiRoles }
        WebClient=[pscustomobject]@{ AppId=$web.appId; ObjectId=$web.id; ServicePrincipalId=$webSp.id }
        InteractiveBlueprintScope=[pscustomobject]@{ Id=$interactiveScope.id; Value=$interactiveScope.value }
        SubmitterUserObjectId=$submitter.id
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $EvidencePath 'entra-foundation.json') -Encoding utf8
    Write-Host "Manual checkpoint: create/link the three child Agent Identities under their named blueprints. Do not create an Agent User." -ForegroundColor Yellow
    Write-Host "Rerun with -ContinueAfterManualCheckpoint and each agent's ClientId/ObjectId to assign least-privilege permissions." -ForegroundColor Yellow
    return
}

Ensure-AppRoleAssignment $entry.id $apiSp.id $apiRoles['Invoice.Create.All']
Ensure-AppRoleAssignment $approval.id $apiSp.id $apiRoles['Invoice.Read.All']
Ensure-AppRoleAssignment $approval.id $apiSp.id $apiRoles['Invoice.Approve.All']
Ensure-DelegatedGrant $interactiveAgent.id $apiSp.id 'Invoice.Create.Delegated'
Ensure-DelegatedGrant $webSp.id $apiSp.id 'Invoice.Create.Delegated'
Ensure-DelegatedGrant $webSp.id $interactiveBlueprintSp.id 'access_as_user'
Ensure-RequiredDelegatedPermission $web.id $apiApp.appId ($apiApp.api.oauth2PermissionScopes | Where-Object value -eq 'Invoice.Create.Delegated' | Select-Object -First 1).id
Ensure-RequiredDelegatedPermission $web.id $interactive.AppId $interactiveScope.id
Ensure-AppRoleAssignment $submitter.id $apiSp.id $apiRoles['InvoiceSubmitter'] 'users'

New-Item -ItemType Directory -Path $EvidencePath -Force | Out-Null
[pscustomobject]@{
    TenantId=$TenantId; AutonomousBlueprint=$autonomous; InteractiveBlueprint=$interactive
    AutonomousFic=$autonomousFic; InteractiveFic=$interactiveFic
    ApiApp=[pscustomobject]@{ AppId=$apiApp.appId; ObjectId=$apiApp.id; ServicePrincipalId=$apiSp.id; IdentifierUri=$apiUri; Roles=$apiRoles }
    WebClient=[pscustomobject]@{ AppId=$web.appId; ObjectId=$web.id; ServicePrincipalId=$webSp.id }
    EntryAgent=[pscustomobject]@{ Name='D1-Invoice-Entry-Agent'; ClientId=$entry.appId; ObjectId=$entry.id; Role='Invoice.Create.All' }
    ApprovalAgent=[pscustomobject]@{ Name='D1-Invoice-Approval-Agent'; ClientId=$approval.appId; ObjectId=$approval.id; Roles=@('Invoice.Read.All','Invoice.Approve.All') }
    InteractiveAgent=[pscustomobject]@{ Name='D1-Interactive-Invoice-Assistant'; ClientId=$interactiveAgent.appId; ObjectId=$interactiveAgent.id; Scope='Invoice.Create.Delegated' }
    SubmitterUserObjectId=$submitter.id
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $EvidencePath 'entra-authorization.json') -Encoding utf8
Write-Host 'D1 Entra objects and role/grant assignments are configured. Store Web client credentials outside this repository.' -ForegroundColor Green
