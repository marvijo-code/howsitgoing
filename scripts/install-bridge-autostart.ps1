#Requires -Version 7
<#
.SYNOPSIS
Publishes the bridge to a stable folder and registers a logon scheduled task so it
always runs. Without a running bridge, the app's sessions never update and the
shared-store mirror goes stale. Re-run after bridge code changes to redeploy.
#>
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\HowsItGoing\bridge",
    [string]$TaskName = "HowsItGoing Bridge"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$bridgeProject = Join-Path $repoRoot 'HowsItGoing.Bridge\HowsItGoing.Bridge.csproj'

# Stop the existing task/process so the publish can overwrite the binaries.
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}
Get-Process -Name 'HowsItGoing.Bridge' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

dotnet publish $bridgeProject -c Release -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Local (gitignored) config with the shared-store connection string, plus an absolute
# monitored-repo path. The default is the relative "..", which resolves against the
# install folder once published outside the repo - that silently breaks repository
# status, push monitoring, the update check, and the issue board's default repo.
$localConfig = Join-Path $repoRoot 'HowsItGoing.Bridge\appsettings.Local.json'
$installedConfig = Join-Path $InstallDir 'appsettings.Local.json'

$config = if (Test-Path $localConfig) {
    Get-Content $localConfig -Raw | ConvertFrom-Json
}
else {
    [pscustomobject]@{}
}

if ($null -eq $config.GitHub) {
    $config | Add-Member -NotePropertyName 'GitHub' -NotePropertyValue ([pscustomobject]@{}) -Force
}
$config.GitHub | Add-Member -NotePropertyName 'MonitoredRepositoryPath' -NotePropertyValue $repoRoot -Force

$config | ConvertTo-Json -Depth 10 | Set-Content -Path $installedConfig -Encoding UTF8

$bridgeExe = Join-Path $InstallDir 'HowsItGoing.Bridge.exe'
$startedViaTask = $false
try {
    $action = New-ScheduledTaskAction -Execute $bridgeExe -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit (New-TimeSpan -Seconds 0)

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    $startedViaTask = $true
    Write-Host "Registered scheduled task '$TaskName' (runs at logon)."
}
catch {
    # Registering logon tasks can require elevation; fall back to a Startup-folder launcher.
    Write-Warning "Scheduled task registration failed ($($_.Exception.Message.Trim())); using the Startup folder instead."
    $startupDir = [Environment]::GetFolderPath('Startup')
    $launcher = Join-Path $startupDir 'HowsItGoingBridge.vbs'
    Set-Content -Path $launcher -Value ("CreateObject(""Wscript.Shell"").Run """"""$bridgeExe"""""", 0, False") -Encoding ASCII
    Write-Host "Created startup launcher: $launcher"
    Start-Process -FilePath $bridgeExe -WorkingDirectory $InstallDir -WindowStyle Hidden
}

Start-Sleep -Seconds 3
try {
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5217/healthz' -TimeoutSec 10
    Write-Host "Bridge is running: $($health.status). Task '$TaskName' starts it at every logon."
}
catch {
    Write-Warning "Bridge did not answer /healthz yet: $_"
}
