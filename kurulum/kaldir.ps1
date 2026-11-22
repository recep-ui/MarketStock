<#
.SYNOPSIS
    PazaryeriHub servisini ve program dosyalarını kaldırır. Veriler (veritabanı, ayarlar, anahtarlar, loglar) varsayılan olarak KORUNUR.

.DESCRIPTION
    YÖNETİCİ olarak çalıştırın:
        powershell -ExecutionPolicy Bypass -File .\kaldir.ps1
    -AyarlariSil: C:\ProgramData\PazaryeriHub de silinir (ayarlar, şifreleme anahtarları, loglar). Anahtarlar silinirse
    veritabanındaki kanal API bilgileri bir daha çözülemez; panelden yeniden girilmesi gerekir. Veritabanı hiçbir durumda silinmez.
#>
[CmdletBinding()]
param(
    [string]$KurulumKlasoru = (Join-Path $env:ProgramFiles 'PazaryeriHub'),
    [switch]$AyarlariSil
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Bu betik YÖNETİCİ olarak çalıştırılmalı.'
}

if (Get-Service -Name 'PazaryeriHub' -ErrorAction SilentlyContinue) {
    Stop-Service -Name 'PazaryeriHub' -Force -ErrorAction SilentlyContinue
    & sc.exe delete PazaryeriHub | Out-Null
    Write-Host 'Servis kaldırıldı.'
}
Get-NetFirewallRule -DisplayName 'PazaryeriHub Panel' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'PazaryeriHub Panel.url') -ErrorAction SilentlyContinue
if (Test-Path $KurulumKlasoru) { Remove-Item $KurulumKlasoru -Recurse -Force; Write-Host "Program dosyaları silindi: $KurulumKlasoru" }

$dataDir = Join-Path $env:ProgramData 'PazaryeriHub'
if ($AyarlariSil) {
    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force; Write-Host "Ayarlar, anahtarlar ve loglar silindi: $dataDir" }
} else {
    Write-Host "Korunan: $dataDir (ayarlar, anahtarlar, loglar). Veritabanı SQL Server'da olduğu gibi duruyor."
}
