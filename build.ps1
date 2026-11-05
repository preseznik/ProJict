# Builds the self-contained app and the installer: artifacts\ProJict-Setup-<version>.exe
# Requires the .NET 10 SDK, Inno Setup 6, Visual Studio's C++ build tools (for the bridge), and Go with MinGW-w64's gcc
# (for built-in Tailscale, native\tailscale).
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    [xml]$project = Get-Content 'src/ProJict.App/ProJict.App.csproj'
    $Version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}

# Built-in Tailscale (#a0d11f05): the bridge DLL, which the publish below puts next to ProJict.exe.
& ./native/tailscale/build.ps1
Set-Location $PSScriptRoot

$publish = 'artifacts/publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/ProJict.App -c Release -r win-x64 --self-contained -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The bridge agents start (ProJictBridge.exe, one native file) goes next to ProJict.exe; ProJict keeps a copy outside
# its install folder so updates don't cut agents off. Native AOT needs the C++ build tools, and finds them with vswhere.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
$bridge = 'artifacts/bridge'
if (Test-Path $bridge) { Remove-Item $bridge -Recurse -Force }
dotnet publish src/ProJict.Bridge -c Release -r win-x64 -p:DebugType=none -p:Version=$Version -o $bridge
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (bridge) failed' }
Copy-Item "$bridge/ProJictBridge.exe" $publish

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found: https://jrsoftware.org/isinfo.php' }

& $iscc "/DAppVersion=$Version" 'installer/ProJict.iss'
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Write-Host "Installer: artifacts/ProJict-Setup-$Version.exe"
