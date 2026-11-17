# Builds SkyLinkBridge.wasm with the MSFS 2024 SDK's own clang (no Visual Studio needed) and
# lays out the community package in package/skylink-bridge.
#
#   powershell -ExecutionPolicy Bypass -File wasm\build.ps1
#
# Needs the MSFS 2024 SDK (in MSFS: Options > General > Developers > Developer Mode, then
# Help > SDK Installer). Set MSFS2024_SDK if it is not in C:\MSFS 2024 SDK.
$ErrorActionPreference = 'Stop'

$sdk = $env:MSFS2024_SDK
if (-not $sdk) { $sdk = 'C:\MSFS 2024 SDK' }
$clang = Join-Path $sdk 'WASM\llvm\bin\clang++.exe'
if (-not (Test-Path $clang)) { throw "MSFS 2024 SDK WASM toolchain not found at $clang (set MSFS2024_SDK)." }

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $PSScriptRoot 'SkyLinkBridge'
$pkg = Join-Path $root 'package\skylink-bridge'
$out = Join-Path $pkg 'modules\SkyLinkBridge.wasm'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$flags = @(
  '--target=wasm32-unknown-wasi',
  "--sysroot=$(Join-Path $sdk 'WASM\wasi-sysroot')",
  "-I$(Join-Path $sdk 'WASM\include')",
  "-I$(Join-Path $sdk 'SimConnect SDK\include')",
  '-std=c++17', '-O2', '-fms-extensions', '-fno-exceptions', '-fno-rtti',
  '-fvisibility=hidden', '-ffunction-sections', '-fdata-sections', '-fno-stack-protector',
  '-mthread-model', 'single', '-mbulk-memory',
  '-D_MSFS_WASM=1', '-D__wasi__', '-D_LIBCPP_HAS_NO_THREADS', '-D_WINDLL', '-D_MBCS',
  '-Werror=return-type',
  '-Wl,--no-entry', '-Wl,--allow-undefined', '-Wl,--export-dynamic', '-Wl,--export-table',
  '-Wl,--gc-sections', '-Wl,--strip-debug',
  '-Wl,--export=malloc', '-Wl,--export=free', '-Wl,--export=__wasm_call_ctors',
  '-Wl,--export=module_init', '-Wl,--export=module_deinit',
  '-o', $out, (Join-Path $src 'Module.cpp')
)
& $clang @flags
if ($LASTEXITCODE -ne 0) { throw "clang failed ($LASTEXITCODE)" }
Write-Host "built $out ($((Get-Item $out).Length) bytes)"

# layout.json lists every file in the package with size and FILETIME date.
$files = Get-ChildItem $pkg -Recurse -File | Where-Object { $_.Name -notin @('layout.json', 'manifest.json') }
$content = @($files | ForEach-Object {
  [ordered]@{
    path = $_.FullName.Substring($pkg.Length + 1).Replace('\', '/')
    size = $_.Length
    date = $_.LastWriteTimeUtc.ToFileTimeUtc()
  }
})
@{ content = $content } | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $pkg 'layout.json')
Write-Host "wrote layout.json ($($content.Count) files). Install with: skylink install-bridge"
