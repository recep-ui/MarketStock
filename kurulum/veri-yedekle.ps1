<#
.SYNOPSIS
    ESKİ bilgisayarda çalıştırılır: PazaryeriHub veritabanının taşınabilir bir yedeğini (.bak) alır (ADR-046).

.DESCRIPTION
    Bu bilgisayarda deneme modunun verisi "PazaryeriHub_Test" veritabanındadır (ürünler, SKU'lar, eşleştirmeler, stok defteri,
    siparişler, kullanıcılar, ayarlar). Örnek:
        powershell -ExecutionPolicy Bypass -File .\kurulum\veri-yedekle.ps1
        powershell -ExecutionPolicy Bypass -File .\kurulum\veri-yedekle.ps1 -Veritabani PazaryeriHub -Cikti D:\yedek.bak

    Yedek COPY_ONLY alınır: kaynak veritabanının yedek zincirini bozmaz, kaynağa hiçbir şey yazmaz.
    Çıkan .bak dosyasını kurulum paketinin yanına kopyalayın ve hedef bilgisayarda:
        powershell -ExecutionPolicy Bypass -File .\kur.ps1 -VeriYedegi .\PazaryeriHub-veri-....bak

    Not: .bak yalnızca AYNI ya da DAHA YENİ SQL Server sürümüne geri yüklenebilir. Betik sonunda kaynak sürümü yazar;
    hedefe en az o sürümü (Express olabilir) kurun.

.PARAMETER SqlKullanici
    Verilmezse Windows kimlik doğrulaması kullanılır (bu bilgisayarda SQL Server'ı kuran kullanıcı genelde yetkilidir).
#>
[CmdletBinding()]
param(
    [string]$SqlSunucu = 'localhost\SQLEXPRESS',
    [string]$Veritabani = 'PazaryeriHub_Test',
    [string]$SqlKullanici = '',
    [string]$Cikti = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source'] = $SqlSunucu
$builder['Initial Catalog'] = 'master'
$builder['TrustServerCertificate'] = $true
$builder['Encrypt'] = $true
if ($SqlKullanici) {
    $secure = Read-Host "SQL kullanıcısı '$SqlKullanici' parolası" -AsSecureString
    $builder['User ID'] = $SqlKullanici
    $builder['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
} else {
    $builder['Integrated Security'] = $true
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
if (-not $Cikti) {
    $Cikti = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'dagitim') "PazaryeriHub-veri-$stamp.bak"
}
$Cikti = [IO.Path]::GetFullPath($Cikti)
New-Item -ItemType Directory -Force -Path (Split-Path $Cikti) | Out-Null

$conn = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
$conn.Open()
try {
    function Invoke-Scalar([string]$sql, [hashtable]$p = @{}) {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 0
        foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue($k, $p[$k]) }
        return $cmd.ExecuteScalar()
    }

    if ((Invoke-Scalar 'SELECT DB_ID(@db)' @{ '@db' = $Veritabani }) -is [DBNull]) { throw "Veritabanı bulunamadı: $Veritabani" }
    $version = Invoke-Scalar "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) + N' (' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100)) + N')'"
    $serverDir = Invoke-Scalar "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(400))"
    $serverFile = Join-Path $serverDir "PazaryeriHub-aktarim-$stamp.bak"

    Write-Host "==> Yedek alınıyor: $Veritabani (SQL Server $version)" -ForegroundColor Cyan
    # Yedek dosyasını SQL Server kendi hesabıyla yazar; bu yüzden önce sunucunun yedek klasörüne alınır, sonra kopyalanır.
    [void](Invoke-Scalar "DECLARE @s nvarchar(max) = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @f WITH COPY_ONLY, CHECKSUM, FORMAT, INIT, NAME = N''PazaryeriHub aktarim'''; EXEC sp_executesql @s, N'@f nvarchar(400)', @f = @f;" @{ '@db' = $Veritabani; '@f' = $serverFile })
    [void](Invoke-Scalar "RESTORE VERIFYONLY FROM DISK = @f WITH CHECKSUM" @{ '@f' = $serverFile })
}
finally { $conn.Dispose() }

if (-not (Test-Path $serverFile)) {
    Write-Host "Yedek SQL Server'ın bilgisayarında oluştu: $serverFile" -ForegroundColor Yellow
    Write-Host 'SQL Server başka bir bilgisayarda; dosyayı oradan elle kopyalayın.'
    return
}
Copy-Item $serverFile $Cikti -Force
Remove-Item $serverFile -ErrorAction SilentlyContinue  # yetki yoksa sunucu klasöründe kalır; zararı yok

$size = [math]::Round((Get-Item $Cikti).Length / 1MB, 1)
Write-Host ''
Write-Host "Yedek hazır ve doğrulandı: $Cikti ($size MB)" -ForegroundColor Green
Write-Host "Kaynak SQL Server sürümü: $version. Hedefe bu sürüm ya da daha yenisi kurulmalı."
Write-Host 'Kanal API bilgileri bu bilgisayarın anahtarıyla şifreli; hedefte çözülemez, kurulumda temizlenir ve panelden yeniden girilir.'
