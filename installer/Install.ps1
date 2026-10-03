<#
.SYNOPSIS
    Installs the .NET Code Quality Pack into every Visual Studio 2022 on this computer.

.DESCRIPTION
    Finds every Visual Studio 2022 with vswhere and runs its VSIXInstaller. The bundled extensions (Roslynator,
    SonarQube for IDE, CodeMaid, Fine Code Coverage) are installed from the Marketplace the next time Visual Studio starts.

    Exit codes: 0 = installed, 1 = no supported Visual Studio found, 2 = Visual Studio is running,
                3 = an install failed, 4 = installer files missing.

.PARAMETER Quiet
    No prompts (used by Setup.exe). If Visual Studio is running, exits with code 2 instead of waiting.
#>
[CmdletBinding()]
param(
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $env:TEMP 'DotNetCodeQualityPack-install.log'

function Write-Log([string] $message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $message
    Add-Content -Path $log -Value $line
    Write-Host $message
}

function Find-Vsix([string] $pattern) {
    $file = Get-ChildItem -Path $here -Filter $pattern -File -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ($file) { return $file.FullName }
    return $null
}

Set-Content -Path $log -Value ("Install started " + (Get-Date))

$vsix = Find-Vsix 'DotNetCodeQualityPack-*.vsix'
if (-not $vsix) {
    Write-Log "No installer files (.vsix) were found next to this script in $here."
    exit 4
}

# vswhere ships with the Visual Studio Installer (VS 2017 and later).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    Write-Log 'Visual Studio 2022 was not found on this computer (vswhere.exe is missing).'
    exit 1
}

$instances = & $vswhere -all -prerelease -products * -format json | ConvertFrom-Json
if (-not $instances) {
    Write-Log 'Visual Studio 2022 was not found on this computer.'
    exit 1
}

# VSIXInstaller can't update a running Visual Studio.
while (Get-Process -Name devenv -ErrorAction SilentlyContinue) {
    if ($Quiet) {
        Write-Log 'Visual Studio is running. Close it and run the installer again.'
        exit 2
    }
    Write-Host ''
    Write-Host 'Visual Studio is running. Save your work, close every Visual Studio window, then press Enter.' -ForegroundColor Yellow
    [void] (Read-Host)
}

$installed = 0
$failed = 0
$skipped = @()

foreach ($vs in $instances) {
    $version = [version] $vs.installationVersion
    $name = "$($vs.displayName) $($vs.installationVersion)"

    if ($version.Major -ne 17) {
        $skipped += "$name (only Visual Studio 2022 is supported)"
        continue
    }

    $installer = Join-Path $vs.installationPath 'Common7\IDE\VSIXInstaller.exe'
    if (-not (Test-Path $installer)) {
        $skipped += "$name (VSIXInstaller.exe not found)"
        continue
    }

    Write-Log "Installing $(Split-Path -Leaf $vsix) into $name..."
    $vsixLog = "DotNetCodeQualityPack-$($vs.instanceId).log"
    $process = Start-Process -FilePath $installer `
        -ArgumentList @('/quiet', "/instanceIds:$($vs.instanceId)", "/logFile:$vsixLog", "`"$vsix`"") `
        -Wait -PassThru

    # 1001 = this version is already installed.
    if ($process.ExitCode -eq 0 -or $process.ExitCode -eq 1001) {
        Write-Log "  Done (exit code $($process.ExitCode))."
        $installed++
    }
    else {
        Write-Log "  Failed with exit code $($process.ExitCode). Details: $(Join-Path $env:TEMP $vsixLog)"
        $failed++
    }
}

foreach ($item in $skipped) { Write-Log "Skipped: $item" }

if ($installed -eq 0 -and $failed -eq 0) {
    Write-Log 'No Visual Studio 2022 found. The extension needs Visual Studio 2022.'
    exit 1
}

Write-Log ''
Write-Log "Installed into $installed Visual Studio installation(s). Failed: $failed."
Write-Log 'Start Visual Studio. On the first start it installs the bundled extensions from the Marketplace (internet access needed).'
Write-Log 'Then open a solution and use Tools > .NET Code Quality.'
Write-Log "Log: $log"

if ($failed -gt 0) { exit 3 }
exit 0
