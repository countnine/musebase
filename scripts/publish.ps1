# Musebase for Windows 배포 패키지 생성
# 사용법: .\scripts\publish.ps1 [-Version 0.1.0]
param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$project = Join-Path $root "src\Musebase.Windows\Musebase.Windows.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$zipPath = Join-Path $root "artifacts\musebase-windows-v$Version-win-x64.zip"

Write-Host "== 빌드 및 게시 (win-x64, self-contained, single-file) =="
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패" }

Write-Host "== zip 패키징 =="
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath

$exe = Get-Item (Join-Path $publishDir "Musebase.exe")
$zip = Get-Item $zipPath
Write-Host ""
Write-Host "완료:"
Write-Host "  exe: $($exe.FullName) ($([Math]::Round($exe.Length / 1MB, 1)) MB)"
Write-Host "  zip: $($zip.FullName) ($([Math]::Round($zip.Length / 1MB, 1)) MB)"
