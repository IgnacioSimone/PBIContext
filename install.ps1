#Requires -Version 5.1
<#
.SYNOPSIS
    Instala PBI Context como herramienta externa de Power BI Desktop.

.DESCRIPTION
    Copia la publicación self-contained a la carpeta local del usuario y registra
    el manifiesto .pbitool.json en la carpeta de Herramientas Externas de Power BI
    Desktop (requiere permisos de administrador, porque esa carpeta vive dentro de
    "Program Files (x86)\Common Files").

    No modifica nada de Power BI Desktop en sí, ni instala servicios, ni escribe en
    el registro: solo copia archivos.
#>

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $scriptDir "publish"
$exeName = "PBIContext.exe"

if (-not (Test-Path (Join-Path $publishDir $exeName))) {
    Write-Error "No se encontró '$exeName' en '$publishDir'. Compilá primero con:`n  dotnet publish -c Release -r win-x64 --self-contained true -o publish"
    exit 1
}

# La carpeta de Herramientas Externas está en Program Files (x86) -> hace falta admin.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Se necesitan permisos de administrador para registrar la herramienta externa. Reabriendo la ventana como administrador..."
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    exit 0
}

$installDir = Join-Path $env:LOCALAPPDATA "PBIContext"
Write-Host "Instalando en: $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $installDir -Recurse -Force

$exePath = Join-Path $installDir $exeName
$manifest = @{
    version       = "1.0"
    name          = "PBI Context"
    description   = "Medidas DAX, tablas, columnas, relaciones, Power Query y visuales del modelo abierto — solo metadata, nunca datos. No afiliado a Microsoft."
    path          = $exePath
    arguments     = '"%server%" "%database%"'
    traceLogPath  = ""
} | ConvertTo-Json

$externalToolsDir = "C:\Program Files (x86)\Common Files\Microsoft Shared\Power BI Desktop\External Tools"
if (-not (Test-Path $externalToolsDir)) {
    New-Item -ItemType Directory -Force -Path $externalToolsDir | Out-Null
}

$manifestPath = Join-Path $externalToolsDir "PBIContext.pbitool.json"
Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8

Write-Host ""
Write-Host "Listo. Si Power BI Desktop está abierto, cerralo y volvé a abrirlo."
Write-Host "Después vas a ver 'PBI Context' en la pestaña Herramientas externas."
Write-Host ""
Write-Host "Presioná Enter para cerrar..."
Read-Host | Out-Null
