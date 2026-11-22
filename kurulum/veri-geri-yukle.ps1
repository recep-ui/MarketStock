<#
.SYNOPSIS
    YENİ bilgisayarda: veri-yedekle.ps1 ile alınan .bak dosyasını PazaryeriHub veritabanı olarak geri yükler (ADR-046).

.DESCRIPTION
    Normalde doğrudan çalıştırılmaz; kur.ps1 -VeriYedegi <dosya> ilk kurulumda bunu çağırır. Elle kullanım:
        powershell -ExecutionPolicy Bypass -File .\veri-geri-yukle.ps1 -YedekDosyasi .\PazaryeriHub-veri.bak

    Kurallar:
      - Hedefte aynı adlı veritabanı VARSA hiçbir şey yapılmaz (üzerine yazılmaz).
      - SQL Server bu bilgisayarda olmalı (.bak sunucunun yedek klasörüne kopyalanır).
      - Geri yüklemeden sonra: eski bilgisayarın SQL kullanıcıları (burada karşılığı yok) veritabanından çıkarılır,
        kanal API bilgileri temizlenir (eski bilgisayarın anahtarıyla şifreli, burada çözülemez), eski Hangfire sunucu kayıtları silinir.
        Ürünler, stok defteri, siparişler, eşleştirmeler, panel kullanıcıları ve ayarlar olduğu gibi kalır.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$YedekDosyasi,
    [string]$BaglantiDizesi = 'Server=localhost\SQLEXPRESS;Database=PazaryeriHub;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$YedekDosyasi = (Resolve-Path $YedekDosyasi).Path
$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($BaglantiDizesi)
$db = $builder['Initial Catalog']
if (-not $db) { throw 'Bağlantı dizesinde veritabanı adı yok.' }
$builder['Initial Catalog'] = 'master'

$conn = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
$conn.Open()
$serverFile = $null
try {
    function New-Cmd([string]$sql, [hashtable]$p = @{}) {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 0
        foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue($k, $p[$k]) }
        return $cmd
    }
    function Invoke-Rows([string]$sql, [hashtable]$p = @{}) {
        $t = New-Object Data.DataTable
        $t.Load((New-Cmd $sql $p).ExecuteReader())
        return , $t
    }

    if (-not ((New-Cmd 'SELECT DB_ID(@db)' @{ '@db' = $db }).ExecuteScalar() -is [DBNull])) {
        throw "Bu SQL Server'da '$db' veritabanı zaten var; üzerine yazılmaz. Taşımak istiyorsanız önce o veritabanını kaldırın (SSMS) ya da kur.ps1'e -Veritabani ile başka ad verin."
    }

    $info = (Invoke-Rows @"
SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(400)) AS Yedek,
       CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400)) AS Veri,
       CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(400)) AS Log,
       CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) AS Surum
"@).Rows[0]
    if (-not (Test-Path $info.Yedek)) {
        throw "SQL Server'ın yedek klasörü bu bilgisayarda bulunamadı ($($info.Yedek)). Geri yükleme yalnızca SQL Server aynı bilgisayardayken yapılabilir."
    }

    # SQL Server dosyayı kendi hesabıyla okur: sunucunun yedek klasörüne kopyala
    $serverFile = Join-Path $info.Yedek ("PazaryeriHub-geri-yukle-" + (Get-Date -Format 'yyyyMMddHHmmss') + '.bak')
    Copy-Item $YedekDosyasi $serverFile

    $header = (Invoke-Rows 'RESTORE HEADERONLY FROM DISK = @f' @{ '@f' = $serverFile }).Rows[0]
    $serverMajor = [int]($info.Surum.Split('.')[0])
    if ([int]$header.SoftwareVersionMajor -gt $serverMajor) {
        throw ("Yedek daha yeni bir SQL Server'dan (sürüm $($header.SoftwareVersionMajor)); bu sunucu $($info.Surum). " +
            'Bu bilgisayara en az yedeğin alındığı sürümü kurun (veri-yedekle.ps1 çıktısında yazar).')
    }
    Write-Host "==> Yedek: '$($header.DatabaseName)' ($($header.BackupFinishDate)) → '$db' olarak geri yükleniyor" -ForegroundColor Cyan

    # Veri ve log dosyalarını bu sunucunun varsayılan klasörlerine taşı
    $moves = @(); $i = 0
    foreach ($f in (Invoke-Rows 'RESTORE FILELISTONLY FROM DISK = @f' @{ '@f' = $serverFile }).Rows) {
        $i++
        if ($f.Type -eq 'L') { $target = Join-Path $info.Log "$db`_log$i.ldf" }
        elseif ($f.Type -eq 'D') { $target = Join-Path $info.Veri "$db$i.mdf" }
        else { $target = Join-Path $info.Veri "$db`_$i" }   # FILESTREAM/full-text klasörleri (PazaryeriHub kullanmaz)
        $moves += "MOVE N'" + $f.LogicalName.Replace("'", "''") + "' TO N'" + $target.Replace("'", "''") + "'"
    }
    $restore = "RESTORE DATABASE " + "[$($db.Replace(']', ']]'))]" + " FROM DISK = @f WITH RECOVERY, " + ($moves -join ', ')
    [void](New-Cmd $restore @{ '@f' = $serverFile }).ExecuteNonQuery()

    # Taşıma sonrası temizlik (yalnızca yeni kopya üzerinde; eski bilgisayardaki veritabanına dokunulmaz)
    $conn.ChangeDatabase($db)
    $orphans = Invoke-Rows @"
SELECT dp.name FROM sys.database_principals dp
 WHERE dp.type IN ('S','U','G') AND dp.principal_id > 4 AND dp.authentication_type <> 2
   AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid)
"@
    foreach ($o in $orphans.Rows) {
        try {
            [void](New-Cmd ("DROP USER [" + $o.name.Replace(']', ']]') + "]")).ExecuteNonQuery()
            Write-Host "  Eski bilgisayarın SQL kullanıcısı çıkarıldı: $($o.name)"
        } catch {
            Write-Warning "SQL kullanıcısı '$($o.name)' çıkarılamadı (şemaya sahip olabilir); zararsız, kalabilir."
        }
    }
    $cleared = (New-Cmd 'UPDATE dbo.Channel SET KimlikSifreli = NULL WHERE KimlikSifreli IS NOT NULL').ExecuteNonQuery()
    if ($cleared -gt 0) { Write-Host "  $cleared kanalın API bilgisi temizlendi (bu bilgisayarda çözülemez; panelden yeniden girin)." }
    [void](New-Cmd "IF OBJECT_ID(N'HangFire.Server') IS NOT NULL DELETE FROM HangFire.Server").ExecuteNonQuery()
    $conn.ChangeDatabase('master')
    # Veritabanı sahibi eski bilgisayarın hesabı olarak kalmasın
    try { [void](New-Cmd ("ALTER AUTHORIZATION ON DATABASE::[" + $db.Replace(']', ']]') + "] TO [sa]")).ExecuteNonQuery() }
    catch { Write-Warning 'Veritabanı sahibi değiştirilemedi (yetki yok); çalışmayı etkilemez.' }

    $counts = (New-Cmd ("SELECT CONCAT((SELECT COUNT(*) FROM [$db].dbo.Product), N' ürün, ', (SELECT COUNT(*) FROM [$db].dbo.Sku), N' SKU, ', " +
        "(SELECT COUNT(*) FROM [$db].dbo.[Order]), N' sipariş, ', (SELECT COUNT(*) FROM [$db].dbo.AppUser), N' panel kullanıcısı')")).ExecuteScalar()
    Write-Host "  Geri yüklendi: $counts" -ForegroundColor Green
}
finally {
    $conn.Dispose()
    if ($serverFile) { Remove-Item $serverFile -ErrorAction SilentlyContinue }
}
