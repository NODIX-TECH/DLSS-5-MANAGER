# Obfuscates the built "DLSS 5 MANAGER.dll" in a given output directory.
# Called from the .fsproj after a Release build (and therefore before the
# publish pipeline copies files out), so it works for plain builds, any
# RuntimeIdentifier (win-x86 / win-x64), and folder/self-contained publishes.
param(
    [Parameter(Mandatory = $true)] [string] $Dir,
    [Parameter(Mandatory = $true)] [string] $Tool
)

$ErrorActionPreference = 'Stop'
$asmName = 'DLSS 5 MANAGER.dll'
$dll = Join-Path $Dir $asmName

if (-not (Test-Path $dll)) {
    Write-Host "obfuscate: no assembly at '$dll' - skipping."
    exit 0
}
if (-not (Test-Path $Tool)) {
    Write-Warning "obfuscate: Obfuscar not found at '$Tool'. Assembly was NOT obfuscated. Run: dotnet tool install --tool-path .obfuscar-tool Obfuscar.GlobalTool --version 2.2.40"
    exit 0
}

$out = Join-Path $Dir 'obf'
$cfgPath = Join-Path $Dir 'obfuscar.gen.xml'

# Public API stays readable: Avalonia XAML, ViewLocator (Type.GetType) and
# System.Text.Json all resolve by name. Only the internal/private surface is renamed.
$cfg = @"
<?xml version="1.0" encoding="utf-8"?>
<Obfuscator>
  <Var name="InPath" value="$Dir" />
  <Var name="OutPath" value="$out" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="RenameProperties" value="false" />
  <Var name="RenameEvents" value="false" />
  <Var name="RenameFields" value="true" />
  <Var name="ReuseNames" value="true" />
  <Var name="UseUnicodeNames" value="false" />
  <Var name="HideStrings" value="false" />
  <Var name="OptimizeMethods" value="true" />
  <Var name="SuppressIldasm" value="true" />
  <Module file="$dll">
    <SkipNamespace name="DLSS_5_MANAGER.Views" />
    <SkipNamespace name="DLSS_5_MANAGER.ViewModels" />
    <SkipType name="DLSS_5_MANAGER.App" />
    <SkipType name="DLSS_5_MANAGER.ViewLocator" />
    <SkipType name="DLSS_5_MANAGER.Program" />
  </Module>
</Obfuscator>
"@

Set-Content -Path $cfgPath -Value $cfg -Encoding UTF8

& $Tool $cfgPath
if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed with exit code $LASTEXITCODE" }

$obfDll = Join-Path $out $asmName
if (-not (Test-Path $obfDll)) { throw "Obfuscar produced no output at '$obfDll'" }

Copy-Item $obfDll $dll -Force
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $cfgPath -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $Dir 'Mapping.txt') -Force -ErrorAction SilentlyContinue
Write-Host "obfuscate: '$asmName' obfuscated in '$Dir'."
