<#
.SYNOPSIS
    Builds the .vsix and packages it as an installer.

.DESCRIPTION
    Run on a Windows computer with Visual Studio 2022 and the "Visual Studio extension development" workload.
    Output in the dist folder next to the solution:
      DotNetCodeQualityPack-Setup-<version>.exe   Setup wizard for VS 2022 (needs Inno Setup 6, free)
      DotNetCodeQualityPack-<version>.zip         the same installer without Setup.exe: unzip and run Install.cmd
      stage\                                      the files inside both

.EXAMPLE
    .\installer\Build-Installers.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$installerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $installerDir
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'

# ---------------------------------------------------------------- find MSBuild (VS 2022 with the VSSDK workload)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw 'Visual Studio 2022 is required to build the installers (vswhere.exe not found).'
}

$msbuild = & $vswhere -latest -prerelease -products * -version '[17.0,' `
    -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Workload.VisualStudioExtension `
    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'Visual Studio 2022 with the "Visual Studio extension development" workload is required. Add it in the Visual Studio Installer.'
}

# ---------------------------------------------------------------- version (from Vsix.cs)
$vsixCs = Get-Content (Join-Path $root 'src\DotNetCodeQualityPack\Vsix.cs') -Raw
$version = [regex]::Match($vsixCs, 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Could not read the version from src\DotNetCodeQualityPack\Vsix.cs.' }
Write-Host "Building .NET Code Quality Pack $version ($Configuration)..." -ForegroundColor Cyan

# ---------------------------------------------------------------- build
& $msbuild (Join-Path $root 'DotNetCodeQualityPack.sln') /restore /t:Rebuild "/p:Configuration=$Configuration" /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "The build failed (exit code $LASTEXITCODE). Fix the errors above and run this script again." }

$vsixBuilt = Join-Path $root "src\DotNetCodeQualityPack\bin\$Configuration\DotNetCodeQualityPack.vsix"
if (-not (Test-Path $vsixBuilt)) { throw "Expected build output not found: $vsixBuilt" }

# ---------------------------------------------------------------- stage
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item $vsixBuilt (Join-Path $stage "DotNetCodeQualityPack-$version.vsix")
foreach ($name in 'Install.ps1', 'Install.cmd', 'Uninstall.ps1', 'Uninstall.cmd', 'README.txt') {
    Copy-Item (Join-Path $installerDir $name) $stage
}

# ---------------------------------------------------------------- zip (works without Setup.exe)
$zip = Join-Path $dist "DotNetCodeQualityPack-$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Created $zip" -ForegroundColor Green

# ---------------------------------------------------------------- Setup.exe (Inno Setup 6)
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($command) { $iscc = $command.Source }
}

if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DStage=$stage" (Join-Path $installerDir 'Setup.iss') /Q
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }
    Write-Host "Created $(Join-Path $dist "DotNetCodeQualityPack-Setup-$version.exe")" -ForegroundColor Green
}
else {
    Write-Warning 'Inno Setup 6 was not found, so Setup.exe was not built. The zip works on its own (unzip, run Install.cmd).'
    Write-Warning 'To also get Setup.exe: install Inno Setup 6 (free, https://jrsoftware.org/isdl.php) and run this script again.'
}

Write-Host ''
Write-Host "Done. Installers are in $dist" -ForegroundColor Cyan
