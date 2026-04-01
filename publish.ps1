#!/usr/bin/env pwsh
# Build DataTableForge and copy to Minervha Studio

$ErrorActionPreference = "Stop"
$StudioBin = "F:\Minervha\Studio\Minervha Studio\resources\forge\DataTableExtractor_bin"

Write-Host "Building DataTableForge..." -ForegroundColor Cyan
dotnet publish -c Release --no-self-contained -o ./publish/

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Cleaning target directory..." -ForegroundColor Cyan
# Preserve extractor.config.json if it exists
$preserveConfig = $null
$configPath = Join-Path $StudioBin "extractor.config.json"
if (Test-Path $configPath) { $preserveConfig = Get-Content $configPath -Raw }

Get-ChildItem $StudioBin -Exclude "extractor.config.json" | Remove-Item -Force -Recurse

if ($preserveConfig) { Set-Content $configPath $preserveConfig -NoNewline }

Write-Host "Copying to Minervha Studio..." -ForegroundColor Cyan
Copy-Item ./publish/* $StudioBin -Force -Recurse

$exe = Join-Path $StudioBin "DataTableForge.exe"
$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "Published: $exe ($size KB)" -ForegroundColor Green
