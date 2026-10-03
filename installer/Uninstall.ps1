<#
.SYNOPSIS
    Removes the .NET Code Quality Pack from every Visual Studio 2022 on this computer.

.DESCRIPTION
    The bundled extensions (Roslynator, SonarQube for IDE, CodeMaid, Fine Code Coverage) are separate extensions and
    are left installed; remove them from Extensions > Manage Extensions if you no longer want them.
    Files the extension wrote into your solutions are not touched; use Tools > .NET Code Quality > Remove Code Quality
    Standards in a solution first if you want those removed too.
#>
[CmdletBinding()]
param(
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'DotNetCodeQualityPack-uninstall.log'
Set-Content -Path $log -Value ("Uninstall started " + (Get-Date))

function Write-Log([string] $message) {
    Add-Content -Path $log -Value $message
    Write-Host $message
}

# Must match the Identity Id in source.extension.vsixmanifest.
$vsixId = 'DotNetCodeQualityPack.01c81d63-d8c4-41e2-863b-20e85b30b3b1'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    Write-Log 'Visual Studio was not found; nothing to remove.'
    exit 0
}

while (Get-Process -Name devenv -ErrorAction SilentlyContinue) {
    if ($Quiet) {
        Write-Log 'Visual Studio is running. Close it and run the uninstaller again.'
        exit 2
    }
    Write-Host 'Visual Studio is running. Close every Visual Studio window, then press Enter.' -ForegroundColor Yellow
    [void] (Read-Host)
}

$instances = & $vswhere -all -prerelease -products * -format json | ConvertFrom-Json
foreach ($vs in $instances) {
    if (([version] $vs.installationVersion).Major -ne 17) { continue }

    $installer = Join-Path $vs.installationPath 'Common7\IDE\VSIXInstaller.exe'
    if (-not (Test-Path $installer)) { continue }

    $name = "$($vs.displayName) $($vs.installationVersion)"
    $process = Start-Process -FilePath $installer `
        -ArgumentList @('/quiet', "/instanceIds:$($vs.instanceId)", "/u:$vsixId") `
        -Wait -PassThru
    if ($process.ExitCode -eq 0) {
        Write-Log "Removed from $name."
    }
    else {
        Write-Log "Not removed from $name (exit code $($process.ExitCode); it may not have been installed there)."
    }
}

Write-Log "Log: $log"
exit 0
