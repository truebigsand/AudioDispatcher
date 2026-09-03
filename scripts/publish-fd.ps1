# 框架依赖发布(体积小,目标机需 .NET 10 Desktop Runtime)
# 用法: powershell -ExecutionPolicy Bypass -File scripts/publish-fd.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/AudioDispatcher/AudioDispatcher.csproj"
$out = Join-Path $root "dist/framework-dependent"

Write-Host "发布框架依赖版本..."
dotnet publish $proj -c Release -r win-x64 --self-contained false -o $out

$dest = "C:\Users\tzf\Desktop\Claude Outputs\AudioDispatcher\framework-dependent"
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Copy-Item $out $dest -Recurse
Write-Host "完成 -> $dest"
