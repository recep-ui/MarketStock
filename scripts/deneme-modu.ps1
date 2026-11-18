<#
.SYNOPSIS
    PazaryeriHub'ı DENEME MODUNDA çalıştırır: panel + API, TEST veritabanı (PazaryeriHub_Test), sahte (mock) kanallar.

.DESCRIPTION
    - Gerçek pazaryerlerine hiçbir çağrı yapılmaz (tüm kanallar Mock modda).
    - Ana veritabanına (PazaryeriHub) dokunulmaz; test veritabanı kullanılır.
    - Her çalıştırmada rastgele bir parola üretilir ve yalnızca bu pencerede gösterilir (hiçbir dosyaya yazılmaz).
    - Varsayılan olarak etiketli demo veri yüklenir (ürünler, stok, siparişler, sapma, hatalı senkron).

    Test veritabanı bağlantısı sırasıyla: PAZARYERIHUB_TEST_DB ortam değişkeni, test projesinin user-secrets'ı
    (ConnectionStrings:PazaryeriHubTest).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\deneme-modu.ps1
    powershell -ExecutionPolicy Bypass -File scripts\deneme-modu.ps1 -Port 5095 -DemoVeriYok
    powershell -ExecutionPolicy Bypass -File scripts\deneme-modu.ps1 -DemoVeriYok -GercekKanal TRENDYOL
#>
param(
    [int]$Port = 5090,
    [switch]$DemoVeriYok,
    # Gerçek API ile çalışacak kanallar (ör. -GercekKanal TRENDYOL). Zamanlanmış job'lar yine kapalıdır:
    # yalnızca panelden elle tetiklenen okuma işleri (Kanaldan ürün çek, mutabakat) kanala gider.
    [ValidateSet('TRENDYOL', 'HEPSIBURADA', 'BEYMEN', 'SOFTCOTTON')]
    [string[]]$GercekKanal = @()
)
$GercekKanal = @($GercekKanal | ForEach-Object { $_.ToUpperInvariant() })

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$baseUrl = "http://localhost:$Port"

# 1) Panel derlenmemişse derle
if (-not (Test-Path (Join-Path $root 'panel\dist\index.html'))) {
    Write-Host 'Panel derleniyor (ilk çalıştırma)...' -ForegroundColor Cyan
    Push-Location (Join-Path $root 'panel')
    try {
        if (-not (Test-Path 'node_modules')) { npm install --no-audit --no-fund }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Panel derlenemedi.' }
    }
    finally { Pop-Location }
}

# 2) Test veritabanı bağlantısı
$connection = $env:PAZARYERIHUB_TEST_DB
if (-not $connection) {
    $line = dotnet user-secrets list --project (Join-Path $root 'tests\PazaryeriHub.Tests.Integration') |
        Where-Object { $_ -like 'ConnectionStrings:PazaryeriHubTest = *' } | Select-Object -First 1
    if ($line) { $connection = $line.Substring($line.IndexOf(' = ') + 3) }
}
if (-not $connection) {
    # Yedek: user-secrets dosyasını kullanıcı profilinden doğrudan oku (bazı terminallerde APPDATA farklı olabilir)
    foreach ($base in @($env:APPDATA, (Join-Path $env:USERPROFILE 'AppData\Roaming'))) {
        if (-not $base) { continue }
        $file = Join-Path $base 'Microsoft\UserSecrets\pazaryerihub-tests-integration-4b1e9a2c\secrets.json'
        if (Test-Path $file) {
            $secrets = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
            $connection = $secrets.'ConnectionStrings:PazaryeriHubTest'
            if ($connection) { break }
        }
    }
}
if (-not $connection) {
    # İlk çalıştırma: bağlantı dizesi gizli girişle istenir (ekranda/komut geçmişinde görünmez) ve user-secrets'a kaydedilir.
    Write-Host 'Test veritabanı (PazaryeriHub_Test) bağlantı dizesi bulunamadı; bir kez girmeniz yeterli.' -ForegroundColor Yellow
    Write-Host 'Örnek: Data Source=SUNUCU,PORT;Initial Catalog=PazaryeriHub_Test;User ID=KULLANICI;Password=PAROLA;Encrypt=True;TrustServerCertificate=True'
    $secure = Read-Host 'Bağlantı dizesi (yazılanlar gizlenir)' -AsSecureString
    $connection = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if ($connection -and $connection -like '*PazaryeriHub_Test*') {
        dotnet user-secrets set 'ConnectionStrings:PazaryeriHubTest' $connection --project (Join-Path $root 'tests\PazaryeriHub.Tests.Integration') | Out-Null
        Write-Host 'Kaydedildi (test projesinin user-secrets''ı). Sonraki çalıştırmalarda sorulmayacak.' -ForegroundColor DarkGray
    }
    elseif ($connection) {
        throw 'Güvenlik: deneme modu yalnızca adında PazaryeriHub_Test geçen veritabanıyla çalışır.'
    }
}
if (-not $connection) {
    throw 'Test veritabanı bağlantısı bulunamadı. PAZARYERIHUB_TEST_DB ayarlayın veya: dotnet user-secrets set "ConnectionStrings:PazaryeriHubTest" "<baglanti>" --project tests\PazaryeriHub.Tests.Integration'
}

# 3) Rastgele parola (yalnızca bu pencerede gösterilir)
$bytes = New-Object byte[] 12
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = 'Demo-' + (([Convert]::ToBase64String($bytes)) -replace '[+/=]', 'x')

# 4) Ortam (yalnızca bu süreç ve alt süreçleri için)
$env:ASPNETCORE_ENVIRONMENT = 'Testing'
$env:ASPNETCORE_URLS = $baseUrl
$env:ConnectionStrings__PazaryeriHub = $connection
$env:Database__MigrateOnStartup = 'true'
$env:Panel__DistPath = Join-Path $root 'panel\dist'
$env:Kimlik__SeedUsers__0__UserName = 'demo-yonetici'
$env:Kimlik__SeedUsers__0__FullName = 'Demo Yönetici'
$env:Kimlik__SeedUsers__0__Role = 'Yonetici'
$env:Kimlik__SeedUsers__0__Password = $password
# Yalnızca tek yönetici; başka rolleri denemek için panelden (Ayarlar → Kullanıcılar) kullanıcı ekleyin.
# Tüm kanallar mock; arka plan job'ları kapalı (veriler siz işlem yapana kadar sabit kalsın)
foreach ($code in 'TRENDYOL', 'HEPSIBURADA', 'BEYMEN', 'SOFTCOTTON') {
    $mode = if ($GercekKanal -contains $code) { 'Gercek' } else { 'Mock' }
    Set-Item -Path "env:Kanallar__$($code)__Mod" -Value $mode
}
# Zamanlanmış job'lar kapalı (cron boş); elle tetiklenen işler (Kanaldan Ürün Çek, mutabakat) varsayılan kuyrukta çalışır.
$env:Hangfire__Queues__0 = 'default'
# DİKKAT: Windows PowerShell'de $env:X = '' değişkeni SİLER (varsayılan cron devreye girer). Bu yüzden tek boşluk
# verilir; uygulama boşluk/boş cron'u "job kapalı" sayar (RecurringJobRegistrar).
foreach ($name in 'Senkron__OutboxCron', 'Senkron__PushCron', 'Senkron__BatchCheckCron', 'Senkron__ReconciliationCron', 'Senkron__ReferenceCron', 'Senkron__CommandCron', 'Senkron__PricePushCron', 'Siparis__FetchCron', 'Siparis__ReturnFetchCron') {
    Set-Item -Path "env:$name" -Value ' '
    if (-not (Test-Path "env:$name")) { throw "Güvenlik: $name kapatılamadı; deneme modu başlatılmadı." }
}
# Global yazma kilidi (ADR-026): gerçek modlu kanallara HİÇBİR yazma (stok, sipariş durumu, iade kararı, fiyat, soru cevabı) gitmez.
$env:Kanallar__YazmaKapali = 'true'
if ($env:Kanallar__YazmaKapali -ne 'true') { throw 'Güvenlik: yazma kilidi açılamadı; deneme modu başlatılmadı.' }
# Konsolu SQL komut loglarıyla doldurma (banner görünür kalsın)
Set-Item -Path 'env:Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore' -Value 'Warning'
$env:Serilog__WriteTo__1__Args__path = Join-Path $env:TEMP 'pazaryerihub-deneme\log-.json'

Write-Host 'API başlatılıyor (ilk derleme bir-iki dakika sürebilir)...' -ForegroundColor Cyan
$api = Start-Process dotnet -ArgumentList @('run', '--project', (Join-Path $root 'src\PazaryeriHub.Api'), '--no-launch-profile') -NoNewWindow -PassThru

try {
    # 5) Hazır olmasını bekle
    $ready = $false
    for ($i = 0; $i -lt 120 -and -not $api.HasExited; $i++) {
        try { Invoke-WebRequest "$baseUrl/health/live" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready = $true; break } catch { Start-Sleep -Seconds 2 }
    }
    if (-not $ready) { throw 'API başlatılamadı (yukarıdaki çıktıya bakın).' }

    # 6) Demo veri
    if (-not $DemoVeriYok) {
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $login = @{ userName = 'demo-yonetici'; password = $password } | ConvertTo-Json
        Invoke-RestMethod "$baseUrl/api/auth/login" -Method Post -Body $login -ContentType 'application/json' -WebSession $session | Out-Null
        $demo = Invoke-RestMethod "$baseUrl/api/dev/demo-data" -Method Post -WebSession $session
        Write-Host "Demo veri yüklendi (etiket $($demo.tag)): SKU'lar $($demo.skuCodes -join ', ')" -ForegroundColor DarkGray
    }

    Write-Host ''
    Write-Host '==================== DENEME MODU HAZIR ====================' -ForegroundColor Green
    Write-Host "  Adres     : $baseUrl"
    Write-Host '  Kullanıcı : demo-yonetici (tüm yetkiler)'
    Write-Host "  Parola    : $password   (yalnızca bu çalıştırma için)"
    if ($GercekKanal.Count -gt 0) {
        Write-Host "  Kanallar  : GERÇEK: $($GercekKanal -join ', ') (yalnızca elle tetiklenen OKUMA işleri; kanala yazma kilitli), diğerleri MOCK" -ForegroundColor Yellow
    } else {
        Write-Host '  Kanallar  : MOCK (gerçek pazaryerine çağrı yok)'
    }
    Write-Host '  Veritabanı: PazaryeriHub_Test'
    Write-Host '  Durdurmak : Ctrl+C'
    Write-Host '===========================================================' -ForegroundColor Green
    Write-Host ''

    Wait-Process -Id $api.Id
}
finally {
    if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue }
}
