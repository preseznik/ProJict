# Builds ProJict's Tailscale bridge (bridge.go) into bin\projict-tailscale.dll, which ProJict loads for built-in
# Tailscale (Settings › API, #a0d11f05). Needs Go (go.dev; the version in go.mod or newer) and a C compiler for cgo
# (MinGW-w64's gcc). ProJict's build copies the DLL next to ProJict.exe when it's there; without it, built-in
# Tailscale says it isn't in this build.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$go = @(
    (Get-Command go.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\go\bin\go.exe",
    "$env:ProgramFiles\Go\bin\go.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $go) { throw 'Go not found: install it from https://go.dev/dl/ (built-in Tailscale needs it).' }
if (-not (Get-Command gcc.exe -ErrorAction SilentlyContinue)) { throw 'gcc not found: built-in Tailscale needs a C compiler for cgo (MinGW-w64).' }

$env:CGO_ENABLED = '1'
$env:CC = 'gcc'
New-Item -ItemType Directory -Force bin | Out-Null
& $go build -buildmode=c-shared -trimpath -ldflags '-s -w' -o bin/projict-tailscale.dll .
if ($LASTEXITCODE -ne 0) { throw 'go build (Tailscale bridge) failed' }
Remove-Item bin/projict-tailscale.h -ErrorAction SilentlyContinue
Write-Host ("Tailscale bridge: native/tailscale/bin/projict-tailscale.dll ({0:N1} MB)" -f ((Get-Item bin/projict-tailscale.dll).Length / 1MB))
