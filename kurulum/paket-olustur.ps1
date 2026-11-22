<#
.SYNOPSIS
    Kurulum paketini hazırlar (geliştirme bilgisayarında): panel derlenir, API kendi .NET çalışma zamanıyla (self-contained, win-x64)
    yayınlanır, kur/kaldır betikleri ve kılavuz eklenir, tek bir zip oluşturulur (ADR-045).

.DESCRIPTION
        powershell -ExecutionPolicy Bypass -File .\kurulum\paket-olustur.ps1
    Çıktı: dagitim\PazaryeriHub-<tarih>\ ve dagitim\PazaryeriHub-<tarih>.zip
    Hedef bilgisayara .NET kurmak GEREKMEZ. Pakette hiçbir parola, bağlantı bilgisi veya API anahtarı yoktur.
#>
[CmdletBinding()]
param([string]$Cikti = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Cikti) { $Cikti = Join-Path $root 'dagitim' }
$version = Get-Date -Format 'yyyyMMdd-HHmm'
$package = Join-Path $Cikti "PazaryeriHub-$version"
$app = Join-Path $package 'uygulama'

Write-Host '==> Panel derleniyor' -ForegroundColor Cyan
Push-Location (Join-Path $root 'panel')
try {
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm ci başarısız.' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Panel derlenemedi.' }
} finally { Pop-Location }

Write-Host '==> API yayınlanıyor (win-x64, self-contained)' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\PazaryeriHub.Api\PazaryeriHub.Api.csproj') -c Release -r win-x64 --self-contained true -o $app -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish başarısız.' }

# Geliştirme ayar dosyası ve yerel loglar pakete girmesin
Remove-Item (Join-Path $app 'appsettings.Development.json') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $app 'logs') -Recurse -Force -ErrorAction SilentlyContinue

Copy-Item (Join-Path $root 'panel\dist') (Join-Path $app 'panel') -Recurse
foreach ($f in 'kur.ps1', 'kaldir.ps1', 'kanal-ayari.ps1', 'KURULUM.md') {
    Copy-Item (Join-Path $PSScriptRoot $f) $package
}

$zip = "$package.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -Force
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "Paket hazır: $zip ($size MB)" -ForegroundColor Green
Write-Host 'Hedef bilgisayarda: zip''i bir klasöre açın, KURULUM.md''deki adımları izleyin.'
