.NET Code Quality Pack for Visual Studio 2022
=============================================

Works with Visual Studio 2022 (Community, Professional or Enterprise).

INSTALL
  Option 1: run DotNetCodeQualityPack-Setup-<version>.exe and follow the steps.
  Option 2: unzip DotNetCodeQualityPack-<version>.zip and double-click Install.cmd.

  Close Visual Studio first. The installer finds every Visual Studio 2022 on the computer and installs the
  extension into each one. No administrator rights are needed.

  The first time you start Visual Studio afterwards, it installs the bundled extensions from the Marketplace
  (internet access needed): Roslynator 2022, SonarQube for IDE, CodeMaid VS2022, Fine Code Coverage 2022.

GET STARTED
  1. Open a solution.
  2. Tools > .NET Code Quality > Apply Code Quality Standards, then reload the solution and rebuild.
  3. Tools > .NET Code Quality > Open Code Quality Fixer, to review and fix warnings page by page.
  4. Copy your team's .github folder and CodeQualityRules.md next to the .sln, then
     Tools > .NET Code Quality > Open Team Rules.

UNINSTALL
  Settings > Apps > ".NET Code Quality Pack for Visual Studio" > Uninstall (if you used Setup.exe),
  or double-click Uninstall.cmd. The bundled extensions stay installed; remove them in
  Extensions > Manage Extensions if you don't want them.

LOGS
  %TEMP%\DotNetCodeQualityPack-install.log
  %TEMP%\DotNetCodeQualityPack-uninstall.log
