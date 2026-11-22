<#
.SYNOPSIS
    PazaryeriHub'ı bu bilgisayara Windows servisi olarak kurar veya günceller (ADR-045).

.DESCRIPTION
    Paketin içinden (paket-olustur.ps1 ile hazırlanan klasör) YÖNETİCİ olarak çalıştırın:
        powershell -ExecutionPolicy Bypass -File .\kur.ps1

    Yaptıkları:
      1. Programı "C:\Program Files\PazaryeriHub" klasörüne kopyalar (güncellemede eskisinin yerine).
      2. Ayarları, şifreleme anahtarlarını ve logları "C:\ProgramData\PazaryeriHub" altında tutar (yalnızca Yöneticiler ve servis erişir).
      3. Veritabanını yoksa oluşturur, şemayı günceller; ilk kurulumda ilk Yönetici kullanıcısını oluşturur.
      4. "PazaryeriHub" Windows servisini kurar: bilgisayar açılınca kendiliğinden başlar, çökerse yeniden başlar.
      5. Güvenlik duvarında yalnızca özel/etki alanı ağları için panel portunu açar (aynı ağdaki cihazlar erişsin).

    Güvenlik varsayılanları: kanala YAZMA KİLİTLİ, tüm kanallar Mock. Gerçek kanala geçiş ve yazma kilidi kanal-ayari.ps1 ile,
    adım adım yapılır (KURULUM.md).

.PARAMETER SqlSunucu
    SQL Server adresi. Bu bilgisayardaki SQL Server Express için varsayılan "localhost\SQLEXPRESS".

.PARAMETER SqlKullanici
    Verilirse SQL kimlik doğrulaması kullanılır (parola sorulur). Verilmezse Windows kimlik doğrulaması: servis kendi hesabıyla
    ("NT SERVICE\PazaryeriHub") bağlanır, parola saklanmaz. Windows kimlik doğrulaması yalnızca SQL Server AYNI bilgisayardaysa çalışır.

.PARAMETER Port
    Panelin portu. Diğer cihazlardan http://<bilgisayar-adı>:<port> ile açılır.

.PARAMETER VeriYedegi
    Yalnızca İLK kurulumda: eski bilgisayarda veri-yedekle.ps1 ile alınan .bak dosyası. Veritabanı boş oluşturulmak yerine
    bu yedekten geri yüklenir (ürünler, stok defteri, siparişler, eşleştirmeler, kullanıcılar), sonra şema güncellenir.
    Hedefte aynı adlı veritabanı varsa kurulum durur (üzerine yazılmaz). Kanal API bilgileri yeniden girilir (ADR-046).
#>
[CmdletBinding()]
param(
    [string]$SqlSunucu = 'localhost\SQLEXPRESS',
    [string]$Veritabani = 'PazaryeriHub',
    [string]$SqlKullanici = '',
    [ValidateRange(1024, 65535)][int]$Port = 5090,
    [string]$YoneticiKullanici = 'yonetici',
    [string]$KurulumKlasoru = (Join-Path $env:ProgramFiles 'PazaryeriHub'),
    [string]$VeriYedegi = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'PazaryeriHub'
$ServiceAccount = "NT SERVICE\$ServiceName"
$DataDir = Join-Path $env:ProgramData 'PazaryeriHub'
$SettingsFile = Join-Path $DataDir 'ayarlar.json'
$Source = Join-Path $PSScriptRoot 'uygulama'
$Exe = Join-Path $KurulumKlasoru 'PazaryeriHub.Api.exe'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- Ön kontroller
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Bu betik YÖNETİCİ olarak çalıştırılmalı (PowerShell → sağ tık → Yönetici olarak çalıştır).'
}
if (-not (Test-Path (Join-Path $Source 'PazaryeriHub.Api.exe'))) {
    throw "Program dosyaları bulunamadı: $Source. Betiği paket klasörünün içinden çalıştırın."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$firstInstall = -not (Test-Path $SettingsFile)
if ($firstInstall) { Write-Step 'İlk kurulum' } else { Write-Step 'Güncelleme (mevcut ayarlar ve veriler korunur)' }
if ($VeriYedegi) {
    if (-not $firstInstall) { throw '-VeriYedegi yalnızca ilk kurulumda kullanılır; kurulu sistemin verisinin üzerine yazılmaz.' }
    if (-not (Test-Path $VeriYedegi -PathType Leaf)) { throw "Yedek dosyası bulunamadı: $VeriYedegi" }
    $VeriYedegi = (Resolve-Path $VeriYedegi).Path
}

# ---------------------------------------------------------------- Ayarlar (yalnızca ilk kurulumda yazılır)
$sqlPassword = $null
if ($firstInstall) {
    if ($SqlKullanici) {
        $secure = Read-Host "SQL kullanıcısı '$SqlKullanici' parolası" -AsSecureString
        $sqlPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
    $adminSecure = Read-Host "İlk Yönetici kullanıcısı '$YoneticiKullanici' için parola (en az 10 karakter)" -AsSecureString
    $adminPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($adminSecure))
    if ($adminPassword.Length -lt 10) { throw 'Parola en az 10 karakter olmalı.' }
}

# ---------------------------------------------------------------- Servisi durdur, dosyaları kopyala
if ($service -and $service.Status -ne 'Stopped') {
    Write-Step 'Servis durduruluyor'
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

Write-Step "Program dosyaları kopyalanıyor: $KurulumKlasoru"
New-Item -ItemType Directory -Force -Path $KurulumKlasoru | Out-Null
# /MIR: hedefi paketle birebir aynı yapar (eski sürümden kalan dosyalar silinir). Veriler bu klasörde DEĞİL.
robocopy $Source $KurulumKlasoru /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Dosyalar kopyalanamadı (robocopy kodu $LASTEXITCODE)." }

# ---------------------------------------------------------------- Veri klasörü ve izinler
Write-Step "Veri klasörü: $DataDir"
foreach ($d in @($DataDir, (Join-Path $DataDir 'anahtarlar'), (Join-Path $DataDir 'loglar'))) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

if ($firstInstall) {
    if ($SqlKullanici) {
        $connection = "Server=$SqlSunucu;Database=$Veritabani;User ID=$SqlKullanici;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=True"
    } else {
        $connection = "Server=$SqlSunucu;Database=$Veritabani;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
    }
    $settings = [ordered]@{
        ConnectionStrings = [ordered]@{ PazaryeriHub = $connection }
        Urls = "http://*:$Port"
        Database = [ordered]@{ AutoCreate = $false; MigrateOnStartup = $true }
        Panel = [ordered]@{ DistPath = (Join-Path $KurulumKlasoru 'panel') }
        DataProtection = [ordered]@{ KeysPath = (Join-Path $DataDir 'anahtarlar'); DpapiMachine = $true }
        Serilog = [ordered]@{ WriteTo = [ordered]@{ '1' = [ordered]@{ Args = [ordered]@{ path = (Join-Path $DataDir 'loglar\pazaryerihub-.json') } } } }
        # Güvenli başlangıç: yazma kilitli, tüm kanallar Mock. Değiştirmek için kanal-ayari.ps1.
        Kanallar = [ordered]@{
            YazmaKapali = $true
            TRENDYOL = [ordered]@{ Mod = 'Mock' }
            HEPSIBURADA = [ordered]@{ Mod = 'Mock' }
            BEYMEN = [ordered]@{ Mod = 'Mock' }
            SOFTCOTTON = [ordered]@{ Mod = 'Mock' }
        }
    }
    # Eski bilgisayardan taşınan veri: veritabanı boş oluşturulmadan ÖNCE yedekten geri yüklenir (şema sonra güncellenir)
    if ($VeriYedegi) {
        Write-Step "Eski veriler geri yükleniyor: $VeriYedegi"
        & (Join-Path $PSScriptRoot 'veri-geri-yukle.ps1') -YedekDosyasi $VeriYedegi -BaglantiDizesi $connection
    }
    [IO.File]::WriteAllText($SettingsFile, ($settings | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
    $sqlPassword = $null
}

# Servis hesabı tanımlı değilse oluştur (izin vermek için hesabın var olması gerekir)
if (-not $service) {
    Write-Step 'Windows servisi oluşturuluyor'
    & sc.exe create $ServiceName binPath= "`"$Exe`"" start= delayed-auto obj= $ServiceAccount DisplayName= 'PazaryeriHub Stok ve Sipariş' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Servis oluşturulamadı (sc.exe kodu $LASTEXITCODE)." }
    & sc.exe description $ServiceName 'PazaryeriHub: pazaryeri stok ve sipariş yönetimi (panel + arka plan işleri).' | Out-Null
    # Çökerse 1 dk sonra yeniden başlat (3 deneme), sayaç 1 günde sıfırlanır
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    $service = Get-Service -Name $ServiceName
}

# Veri klasörü: yalnızca SYSTEM, Yöneticiler ve servis hesabı (ayarlar.json'da bağlantı bilgisi, anahtarlar klasöründe şifreleme anahtarı var)
& icacls $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${ServiceAccount}:(OI)(CI)M" /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Veri klasörü izinleri ayarlanamadı (icacls kodu $LASTEXITCODE)." }

# ---------------------------------------------------------------- Veritabanı: oluştur, şema, ilk yönetici
Write-Step 'Veritabanı hazırlanıyor (yoksa oluşturulur, şema güncellenir)'
if ($firstInstall) {
    $env:Kimlik__SeedUsers__0__UserName = $YoneticiKullanici
    $env:Kimlik__SeedUsers__0__FullName = 'Yönetici'
    $env:Kimlik__SeedUsers__0__Role = 'Yonetici'
    $env:Kimlik__SeedUsers__0__Password = $adminPassword
}
try {
    & $Exe --kurulum
    $setupExit = $LASTEXITCODE
} finally {
    Remove-Item env:Kimlik__SeedUsers__0__Password, env:Kimlik__SeedUsers__0__UserName, env:Kimlik__SeedUsers__0__FullName, env:Kimlik__SeedUsers__0__Role -ErrorAction SilentlyContinue
    $adminPassword = $null
}
if ($setupExit -ne 0) { throw "Veritabanı hazırlığı başarısız (kod $setupExit). Ayrıntı: $DataDir\loglar" }

# Windows kimlik doğrulaması: servis hesabına bu veritabanında yetki ver (SQL aynı bilgisayarda olmalı)
$settingsJson = Get-Content $SettingsFile -Raw | ConvertFrom-Json
$cs = $settingsJson.ConnectionStrings.PazaryeriHub
if ($cs -match 'Integrated Security=True') {
    Write-Step "SQL Server'da servis hesabına yetki veriliyor ($ServiceAccount)"
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($cs)
    $db = $builder['Database']
    $builder['Database'] = 'master'
    $sql = @"
IF SUSER_ID(N'$ServiceAccount') IS NULL CREATE LOGIN [$ServiceAccount] FROM WINDOWS;
DECLARE @s nvarchar(max) = N'USE ' + QUOTENAME(@db) + N';
IF USER_ID(N''$ServiceAccount'') IS NULL CREATE USER [$ServiceAccount] FOR LOGIN [$ServiceAccount];
ALTER ROLE db_owner ADD MEMBER [$ServiceAccount];';
EXEC (@s);
"@
    $conn = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        [void]$cmd.Parameters.AddWithValue('@db', $db)
        [void]$cmd.ExecuteNonQuery()
    } finally { $conn.Dispose() }
}

# ---------------------------------------------------------------- Güvenlik duvarı
$port = ([uri]($settingsJson.Urls -replace '\*', 'localhost')).Port
Write-Step "Güvenlik duvarı: TCP $port (yalnızca özel ve etki alanı ağları)"
Get-NetFirewallRule -DisplayName 'PazaryeriHub Panel' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'PazaryeriHub Panel' -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Private, Domain | Out-Null
$public = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.NetworkCategory -eq 'Public' }
if ($public) {
    Write-Warning ("Ağ bağlantısı 'Genel (Public)' olarak işaretli: $($public.InterfaceAlias -join ', '). Diğer cihazlar erişemez. " +
        "Ayarlar → Ağ ve İnternet → bağlantı → Ağ profili: Özel yapın.")
}

# ---------------------------------------------------------------- Başlat ve doğrula
Write-Step 'Servis başlatılıyor'
Start-Service -Name $ServiceName
$healthy = $false
for ($i = 0; $i -lt 60 -and -not $healthy; $i++) {
    Start-Sleep -Seconds 2
    try { $healthy = (Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$port/health" -TimeoutSec 5).StatusCode -eq 200 } catch { }
}
if (-not $healthy) { throw "Servis çalışıyor ama sağlık kontrolü yanıt vermedi. Loglar: $DataDir\loglar" }

# Masaüstü kısayolu (tüm kullanıcılar)
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'PazaryeriHub Panel.url'
Set-Content -Path $shortcut -Value "[InternetShortcut]`r`nURL=http://localhost:$port/" -Encoding ASCII

$addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress
Write-Host ''
Write-Host '==================== PAZARYERIHUB KURULDU ====================' -ForegroundColor Green
Write-Host "  Bu bilgisayardan : http://localhost:$port"
Write-Host "  Ağdaki cihazlar  : http://$($env:COMPUTERNAME):$port"
foreach ($a in $addresses) { Write-Host "                     http://${a}:$port" }
if ($firstInstall) { Write-Host "  İlk giriş        : $YoneticiKullanici (az önce girdiğiniz parola)" }
Write-Host "  Ayarlar / loglar : $DataDir"
Write-Host '  Kanallar         : yazma KİLİTLİ, tümü Mock → gerçek kanala geçiş için kanal-ayari.ps1 (KURULUM.md)'
Write-Host '==============================================================' -ForegroundColor Green
