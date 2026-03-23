#!/usr/bin/env pwsh
# Build DataTableForge and copy to Minervha Studio

$ErrorActionPreference = "Stop"
$StudioBin = "F:\Minervha\Studio\Minervha Studio\resources\forge\DataTableExtractor_bin"

Write-Host "Building DataTableForge..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Copying to Minervha Studio..." -ForegroundColor Cyan
Copy-Item ./publish/* $StudioBin -Force -Recurse

$exe = Join-Path $StudioBin "DataTableForge.exe"
$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "Published: $exe ($size KB)" -ForegroundColor Green
