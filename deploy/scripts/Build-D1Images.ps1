#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9]{5,50}$')][string]$AcrName,
    [string]$RepositoryPrefix = 'd1-invoice',
    [string]$Tag = (git -C $PSScriptRoot rev-parse --short=12 HEAD 2>$null),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..' 'image-manifest.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = Get-Date -AsUTC -Format 'yyyyMMddHHmmss' }

function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)" }
    return ($output -join [Environment]::NewLine).Trim()
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$registry = Invoke-Az -Arguments @('acr','show','--name',$AcrName,'--query','loginServer','--output','tsv')
$images = @(
    @{ Name='api'; Repository="$RepositoryPrefix-api"; Dockerfile='src/Invoice.Api/Dockerfile' },
    @{ Name='worker'; Repository="$RepositoryPrefix-worker"; Dockerfile='src/Invoice.Worker/Dockerfile' },
    @{ Name='interactive'; Repository="$RepositoryPrefix-interactive"; Dockerfile='src/Invoice.Interactive/Dockerfile' },
    @{ Name='web'; Repository="$RepositoryPrefix-web"; Dockerfile='src/Invoice.Web/Dockerfile' }
)

$manifest = foreach ($image in $images) {
    $tagged = "$($image.Repository):$Tag"
    Write-Host "Building $tagged ..." -ForegroundColor Cyan
    Invoke-Az -Arguments @('acr','build','--registry',$AcrName,'--image',$tagged,'--file',$image.Dockerfile,'--platform','linux/amd64',$repoRoot,'--output','none') | Out-Null
    $digest = Invoke-Az -Arguments @('acr','repository','show','--name',$AcrName,'--image',$tagged,'--query','digest','--output','tsv')
    [pscustomobject]@{
        Name = $image.Name
        TagReference = "$registry/$($image.Repository):$Tag"
        DigestReference = "$registry/$($image.Repository)@$digest"
        Digest = $digest
    }
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ManifestPath -Encoding utf8
$manifest | Format-Table Name, TagReference, DigestReference -AutoSize
Write-Host "Non-secret image manifest written to $ManifestPath" -ForegroundColor Green
