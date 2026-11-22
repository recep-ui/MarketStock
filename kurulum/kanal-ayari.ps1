<#
.SYNOPSIS
    Kurulu PazaryeriHub'da kanal modlarını ve kanala yazma kilidini değiştirir, servisi yeniden başlatır (ADR-045).

.DESCRIPTION
    YÖNETİCİ olarak çalıştırın. Örnekler:
      # 1. adım — gerçek kanallardan yalnızca OKUMA (sipariş çekme, mutabakat); kanala hiçbir şey yazılmaz
      powershell -ExecutionPolicy Bypass -File .\kanal-ayari.ps1 -GercekKanal TRENDYOL,BEYMEN -Yazma Kilitli

      # 2. adım — yazma kilidini kaldır. Hangi işin (stok, fiyat, sipariş durumu…) kanala gideceği panelde
      #           Ayarlar → Kanallar → yetenekler ile ayrıca açılır (önce DENEME önerilir).
      powershell -ExecutionPolicy Bypass -File .\kanal-ayari.ps1 -GercekKanal TRENDYOL,BEYMEN -Yazma Acik

      # Acil durdurma — kanala yazmayı hemen kes
      powershell -ExecutionPolicy Bypass -File .\kanal-ayari.ps1 -Yazma Kilitli

    -GercekKanal verilmezse kanal modları değişmez. Listede olmayan kanallar Mock'a döner.
#>
[CmdletBinding()]
param(
    [string[]]$GercekKanal,
    [ValidateSet('Kilitli', 'Acik', '')][string]$Yazma = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Bu betik YÖNETİCİ olarak çalıştırılmalı.'
}

$codes = @('TRENDYOL', 'HEPSIBURADA', 'BEYMEN', 'SOFTCOTTON')
$settingsFile = Join-Path $env:ProgramData 'PazaryeriHub\ayarlar.json'
if (-not (Test-Path $settingsFile)) { throw "Kurulum bulunamadı: $settingsFile" }
if (-not $PSBoundParameters.ContainsKey('GercekKanal') -and -not $Yazma) { throw 'En az biri gerekli: -GercekKanal veya -Yazma.' }

$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json

if ($PSBoundParameters.ContainsKey('GercekKanal')) {
    # "TRENDYOL,BEYMEN" tek metin olarak da gelebilir
    $real = @($GercekKanal | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })
    foreach ($r in $real) { if ($codes -notcontains $r) { throw "Bilinmeyen kanal: $r (geçerli: $($codes -join ', '))" } }
    if ($real -contains 'SOFTCOTTON') { throw 'softcotton.com.tr için gerçek adaptör yok (altyapı teyit edilmedi); Mock kalmalı.' }
    foreach ($c in $codes) {
        $mode = 'Mock'
        if ($real -contains $c) { $mode = 'Gercek' }
        $settings.Kanallar.$c.Mod = $mode
    }
}
if ($Yazma) { $settings.Kanallar.YazmaKapali = ($Yazma -eq 'Kilitli') }

[IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))

Restart-Service -Name 'PazaryeriHub' -Force
Write-Host 'Ayar kaydedildi, servis yeniden başlatıldı:' -ForegroundColor Green
foreach ($c in $codes) { Write-Host ("  {0,-12} {1}" -f $c, $settings.Kanallar.$c.Mod) }
$lock = 'AÇIK (yetenekleri panelde açın)'
if ($settings.Kanallar.YazmaKapali) { $lock = 'KİLİTLİ (kanala hiçbir şey yazılmaz)' }
Write-Host "  Kanala yazma  $lock"
