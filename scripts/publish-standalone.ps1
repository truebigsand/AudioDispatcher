# 自包含单文件发布(免运行时,绿色便携)
# 用法: powershell -ExecutionPolicy Bypass -File scripts/publish-standalone.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/AudioDispatcher/AudioDispatcher.csproj"
$out = Join-Path $root "dist/standalone"

Write-Host "发布自包含单文件版本(首次需下载运行时包,耗时约 1-2 分钟)..."
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $out

$dest = "C:\Users\tzf\Desktop\Claude Outputs\AudioDispatcher\standalone"
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Copy-Item $out $dest -Recurse
Write-Host "完成 -> $dest"
