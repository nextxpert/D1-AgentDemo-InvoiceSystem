#requires -Version 7.2
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [int]$StartupTimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path $PSScriptRoot).Path
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('d1-invoice-offline-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$databasePath = Join-Path $runDirectory 'invoice-demo.db'
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$serviceLogs = [Collections.Generic.List[string]]::new()
$completed = $false

function Start-D1Service {
    param([string]$Name, [string]$Project, [int]$Port, [hashtable]$Settings)
    $stdout = Join-Path $runDirectory "$Name.stdout.log"
    $stderr = Join-Path $runDirectory "$Name.stderr.log"
    $previous = @{}
    try {
        foreach ($key in $Settings.Keys + 'ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS','DEMO_MODE','DATABASE_PROVIDER','DATABASE_CONNECTION_STRING') {
            if (-not $previous.ContainsKey($key)) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
        }
        [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Development', 'Process')
        [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', "http://127.0.0.1:$Port", 'Process')
        [Environment]::SetEnvironmentVariable('DEMO_MODE', 'offline', 'Process')
        [Environment]::SetEnvironmentVariable('DATABASE_PROVIDER', 'Sqlite', 'Process')
        [Environment]::SetEnvironmentVariable('DATABASE_CONNECTION_STRING', "Data Source=$databasePath", 'Process')
        foreach ($key in $Settings.Keys) { [Environment]::SetEnvironmentVariable($key, [string]$Settings[$key], 'Process') }
        $process = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--configuration','Release','--project',(Join-Path $repoRoot $Project)) `
            -WorkingDirectory $repoRoot -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        $processes.Add($process)
        $serviceLogs.Add($stdout)
        $serviceLogs.Add($stderr)
        return $process
    }
    finally {
        foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    }
}

function Wait-D1Health {
    param([Diagnostics.Process]$Process, [string]$Name, [string]$Url)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
        if ($Process.HasExited) {
            $log = Get-Content -LiteralPath ($serviceLogs | Select-Object -Last 2) -Tail 40 -ErrorAction SilentlyContinue
            throw "$Name exited before becoming healthy.`n$($log -join "`n")"
        }
        try {
            $response = Invoke-WebRequest -Uri $Url -TimeoutSec 3 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 400
    }
    throw "$Name did not become healthy at $Url within $StartupTimeoutSeconds seconds."
}

try {
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $repoRoot 'D1.Invoice.sln') --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    }

    $api = Start-D1Service 'api' 'src/Invoice.Api/Invoice.Api.csproj' 5080 @{}
    Wait-D1Health $api 'Invoice API' 'http://127.0.0.1:5080/readyz'
    $worker = Start-D1Service 'worker' 'src/Invoice.Worker/Invoice.Worker.csproj' 5081 @{
        INVOICE_API_BASE_URL='http://127.0.0.1:5080/'
    }
    Wait-D1Health $worker 'Invoice Worker' 'http://127.0.0.1:5081/healthz'
    $interactive = Start-D1Service 'interactive' 'src/Invoice.Interactive/Invoice.Interactive.csproj' 5082 @{
        INVOICE_API_BASE_URL='http://127.0.0.1:5080/'
    }
    Wait-D1Health $interactive 'Interactive API' 'http://127.0.0.1:5082/healthz'
    $web = Start-D1Service 'web' 'src/Invoice.Web/Invoice.Web.csproj' 5083 @{
        INVOICE_INTERACTIVE_API_BASE_URL='http://127.0.0.1:5082/'
        INVOICE_WORKER_BASE_URL='http://127.0.0.1:5081/'
    }
    Wait-D1Health $web 'Web UI' 'http://127.0.0.1:5083/healthz'

    $autonomous = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5081/api/demo/runs/autonomous' -TimeoutSec 120
    if ($autonomous.status -ne 'Succeeded' -or $autonomous.invoices.Count -lt 1) {
        throw "Autonomous offline run did not succeed: status=$($autonomous.status), invoices=$($autonomous.invoices.Count), error=$($autonomous.error)"
    }

    $negative = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5081/api/demo/runs/negative-tests' -TimeoutSec 60
    if ($negative.status -ne 'Succeeded' -or @($negative.executions | Where-Object outcome -ne 'Denied').Count -gt 0) {
        throw 'Wrong-role negative authorization checks did not all return Denied.'
    }

    $noRole = Invoke-WebRequest -Uri 'http://127.0.0.1:5082/interactive/invoices/mine' -Headers @{ 'X-Demo-Actor'='interactive-no-role' } -TimeoutSec 30 -SkipHttpErrorCheck
    if ($noRole.StatusCode -ne 403) { throw "Interactive user without InvoiceSubmitter expected 403, got $($noRole.StatusCode)." }

    $completed = $true
    [pscustomobject]@{
        ApiHealth='healthy'; WorkerHealth='healthy'; InteractiveHealth='healthy'; WebHealth='healthy'
        AutonomousRun=$autonomous.status; GeneratedInvoiceCount=$autonomous.invoices.Count
        WrongRoleRun=$negative.status; WrongRoleDeniedCount=@($negative.executions | Where-Object outcome -eq 'Denied').Count
        InteractiveNoRoleStatus=$noRole.StatusCode
        WebUrl='http://127.0.0.1:5083/'
    } | Format-List
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(10000) | Out-Null
        }
        $process.Dispose()
    }
    if ($completed) { Remove-Item -LiteralPath $runDirectory -Recurse -Force }
    else { Write-Warning "Offline run artifacts and service logs retained at $runDirectory" }
}
