# Script local para verificar linteo, formato y pruebas unitarias antes de hacer commit o push.
param(
    [switch]$FixFormat
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   LanScreenShare - Quality & Test Runner  " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

if ($FixFormat) {
    Write-Host "`n[1/3] Auto-formateando código con dotnet format..." -ForegroundColor Yellow
    dotnet format
} else {
    Write-Host "`n[1/3] Verificando formato y linteo de código (.editorconfig)..." -ForegroundColor Yellow
    dotnet format --verify-no-changes
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Error de formato detectado. Ejecuta 'dotnet format' o '.\scripts\verify-all.ps1 -FixFormat' para corregirlo automáticamente." -ForegroundColor Red
        exit 1
    }
    Write-Host "✅ Formato y linteo limpios." -ForegroundColor Green
}

Write-Host "`n[2/3] Compilando solución (Debug)..." -ForegroundColor Yellow
dotnet build
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Error de compilación." -ForegroundColor Red
    exit 1
}
Write-Host "✅ Compilación exitosa." -ForegroundColor Green

Write-Host "`n[3/3] Ejecutando pruebas unitarias..." -ForegroundColor Yellow
dotnet test --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Fallaron pruebas unitarias." -ForegroundColor Red
    exit 1
}

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "  ¡TODO LIMPIO Y APROBADO AL 100%! 🎉     " -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green

