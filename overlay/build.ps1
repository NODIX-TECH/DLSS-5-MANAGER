param(
    [string]$Out = ""
)

# Builds the DLSS 5 overlay as a 64-bit ReShade add-on.
#
# MSVC is not on PATH and every path here contains spaces, so the whole compile
# is written out as one batch file - vcvars64 then cl - and handed to cmd. That
# is the only quoting that survives intact.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -latest -format value -property installationPath
if (-not $vs) { throw "No Visual Studio C++ toolset found." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

$name = "dlss5-overlay"

$sources = Get-ChildItem (Join-Path $root "src") -Filter "*.cpp" |
    Sort-Object Name |
    ForEach-Object { $_.FullName }

if ($sources.Count -eq 0) { throw "no sources found under src\" }

$objDir = Join-Path $root "build\obj\$name"
$binDir = Join-Path $root "build"
New-Item -ItemType Directory -Force -Path $objDir | Out-Null
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$outDll = Join-Path $binDir "$name.dll"

# /GR- : no RTTI, /MT : no CRT dependency in the game's process,
# /Zc:preprocessor : the SDK headers rely on a conforming preprocessor.
$flags = @(
    '/nologo', '/std:c++20', '/O2', '/MT', '/W4', '/EHsc', '/GR-',
    '/DWIN32_LEAN_AND_MEAN', '/DNOMINMAX', '/D_CRT_SECURE_NO_WARNINGS',
    '/permissive-', '/Zc:preprocessor', '/utf-8'
)
$libs = @('user32.lib', 'dxgi.lib', 'advapi32.lib', 'shell32.lib', 'ole32.lib', 'version.lib')

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('@echo off')
# vcvars shells out to its own vswhere and complains on stderr when it is not
# on PATH; it sets the environment correctly regardless, so silence both streams.
$lines.Add("call `"$vcvars`" >nul 2>&1")
$lines.Add('if errorlevel 1 exit /b 1')

$cl = 'cl.exe ' + ($flags -join ' ')
$cl += " /I`"$(Join-Path $root 'external\reshade\include')`""
$cl += " /I`"$(Join-Path $root 'external\imgui')`""
$cl += " /I`"$(Join-Path $root 'src')`""
# The trailing separator has to be doubled: MSVC reads a lone backslash before
# the closing quote as an escape and then swallows the rest of the line.
$cl += " /Fo:`"$objDir\\`""
foreach ($s in $sources) { $cl += " `"$s`"" }
$cl += " /link /DLL /OUT:`"$outDll`" " + ($libs -join ' ')
$lines.Add($cl)
$lines.Add('exit /b %errorlevel%')

$bat = Join-Path $env:TEMP ("dlss5-overlay-build-" + [guid]::NewGuid().ToString('N') + ".bat")
[System.IO.File]::WriteAllLines($bat, $lines, (New-Object System.Text.UTF8Encoding($false)))

Write-Output "building $name from $($sources.Count) source file(s)..."
& cmd.exe /c "`"$bat`""
$code = $LASTEXITCODE
Remove-Item $bat -Force -ErrorAction SilentlyContinue

if ($code -ne 0) { throw "build failed with exit code $code" }

# ReShade discovers add-ons by extension, not by name.
$addon = Join-Path $binDir "$name.addon64"
Copy-Item $outDll $addon -Force

if ($Out -ne "") {
    $outParent = Split-Path -Parent $Out
    if ($outParent) { New-Item -ItemType Directory -Force -Path $outParent | Out-Null }
    Copy-Item $outDll $Out -Force
    Write-Output "deployed -> $Out"
}

Write-Output ("built  " + $addon + "  " + [math]::Round((Get-Item $addon).Length / 1KB, 1) + " KB")
