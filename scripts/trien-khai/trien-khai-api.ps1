<#
.SYNOPSIS
  Triển khai backend DSVHVN lên máy demo: sao lưu CSDL, áp migration, thay bản chạy, kiểm sức khoẻ, tự trả bản trước khi hỏng.

.DESCRIPTION
  Workflow CD chỉ gọi script này; khi runner tắt vẫn triển khai tay được bằng đúng script:
    pwsh scripts/trien-khai/trien-khai-api.ps1 -ThuMucGoi <thư mục chứa api\ và migrations.sql>

  Các bước:
    1. Đọc chuỗi kết nối từ <GocMay>\cau-hinh\appsettings.Production.json (không in ra).
    2. Sao lưu CSDL (BACKUP ... COPY_ONLY) vào thư mục sao lưu, giữ 10 bản mới nhất.
    3. Đặt app_offline.htm để dừng ứng dụng.
    4. Áp migrations.sql (idempotent).
    5. Giữ bản trước ở api-truoc, chép bản mới vào api, chép lại cấu hình bí mật.
    6. Gỡ app_offline.htm, gọi địa chỉ sức khoẻ tới khi trả 200.
    7. Hỏng ở bước 4-6: trả bản trước, gỡ app_offline.htm, hướng dẫn khôi phục CSDL (không tự khôi phục), thoát mã 1.

.PARAMETER ThuMucGoi
  Thư mục gói (artifact), chứa api\ và migrations.sql (có thể có phien-ban.txt).
.PARAMETER GocMay
  Gốc triển khai, mặc định C:\dsvhvn (có api\, api-truoc\, cau-hinh\, sao-luu\...).
.PARAMETER KiemSucKhoeUrl
  Địa chỉ kiểm sức khoẻ sau khi thay bản, mặc định http://localhost:8080/health.
.PARAMETER ThoiGianChoGiay
  Số giây tối đa chờ địa chỉ sức khoẻ trả 200, mặc định 60.
.PARAMETER ThuMucSaoLuu
  Nơi ghi tệp .bak, mặc định <GocMay>\sao-luu. Dịch vụ SQL Server phải ghi được vào đây.
.PARAMETER BoQuaSaoLuu
  Bỏ bước sao lưu (chỉ dùng khi thử).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ThuMucGoi,
    [string]$GocMay = 'C:\dsvhvn',
    [string]$KiemSucKhoeUrl = 'http://localhost:8080/health',
    [int]$ThoiGianChoGiay = 60,
    [string]$ThuMucSaoLuu,
    [switch]$BoQuaSaoLuu
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

# Cho chữ tiếng Việt hiện đúng trong nhật ký của workflow và trên cửa sổ dòng lệnh.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$script:SoBuoc = 0
function Write-Buoc([string]$noiDung) {
    $script:SoBuoc++
    Write-Host ''
    Write-Host ("[{0}] {1}" -f $script:SoBuoc, $noiDung) -ForegroundColor Cyan
}

function Invoke-Sqlcmd2 {
    # Gọi sqlcmd với thông tin kết nối đã tách; mật khẩu đi qua biến môi trường, không qua dòng lệnh.
    param([hashtable]$KetNoi, [string[]]$ThamSo)
    $args2 = @('-S', $KetNoi.Server, '-d', $KetNoi.Database, '-C', '-b', '-I')
    if ($KetNoi.TinCay) { $args2 += '-E' } else { $args2 += @('-U', $KetNoi.User) }
    $args2 += $ThamSo
    & sqlcmd @args2
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd trả mã lỗi $LASTEXITCODE." }
}

function Invoke-Robocopy([string]$nguon, [string]$dich, [string[]]$them) {
    # Mã thoát 0-7 là thành công, từ 8 là lỗi.
    $a = @($nguon, $dich, '/MIR', '/R:5', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') + $them
    & robocopy @a | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy từ '$nguon' sang '$dich' lỗi (mã $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Test-SucKhoe([string]$url) {
    try {
        $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5 -MaximumRedirection 0 -ErrorAction Stop
        return ($r.StatusCode -eq 200)
    } catch {
        $script:LoiSucKhoe = $_.Exception.Message
        return $false
    }
}

# ---------------------------------------------------------------- kiểm đầu vào
$ThuMucGoi = (Resolve-Path -LiteralPath $ThuMucGoi).Path
$goiApi = Join-Path $ThuMucGoi 'api'
$goiSql = Join-Path $ThuMucGoi 'migrations.sql'
if (-not (Test-Path -LiteralPath (Join-Path $goiApi 'DSVHVN.Api.dll'))) { throw "Gói không có api\DSVHVN.Api.dll: $ThuMucGoi" }
if (-not (Test-Path -LiteralPath $goiSql)) { throw "Gói không có migrations.sql: $ThuMucGoi" }
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw 'Không thấy sqlcmd trong PATH.' }

$api = Join-Path $GocMay 'api'
$apiTruoc = Join-Path $GocMay 'api-truoc'
$cauHinh = Join-Path $GocMay 'cau-hinh\appsettings.Production.json'
if (-not $ThuMucSaoLuu) { $ThuMucSaoLuu = Join-Path $GocMay 'sao-luu' }
$appOffline = Join-Path $api 'app_offline.htm'
if (-not (Test-Path -LiteralPath $cauHinh)) {
    throw "Chưa có $cauHinh. Chạy scripts\trien-khai\tao-cau-hinh-may-demo.ps1 trước."
}

$phienBan = Join-Path $ThuMucGoi 'phien-ban.txt'
if (Test-Path -LiteralPath $phienBan) {
    Write-Host 'Phiên bản trong gói:'
    Get-Content -LiteralPath $phienBan | ForEach-Object { Write-Host "  $_" }
}

# ---------------------------------------------------------------- 1. kết nối
Write-Buoc 'Đọc cấu hình kết nối CSDL'
$cfg = Get-Content -LiteralPath $cauHinh -Raw -Encoding UTF8 | ConvertFrom-Json
$chuoi = $cfg.ConnectionStrings.Default
if ([string]::IsNullOrWhiteSpace($chuoi)) { throw 'Thiếu ConnectionStrings:Default trong appsettings.Production.json.' }
$sb = New-Object System.Data.Common.DbConnectionStringBuilder
# PowerShell coi bộ dựng là từ điển nên gán thẳng .ConnectionString sẽ thành một khoá; đặt qua reflection.
[System.Data.Common.DbConnectionStringBuilder].GetProperty('ConnectionString').SetValue($sb, $chuoi)
function Lay-Khoa($sb, [string[]]$tenKhoa) {
    foreach ($k in $tenKhoa) {
        $v = $null
        if ($sb.TryGetValue($k, [ref]$v) -and -not [string]::IsNullOrWhiteSpace([string]$v)) { return [string]$v }
    }
    return $null
}
$ketNoi = @{
    Server   = Lay-Khoa $sb @('Server', 'Data Source', 'Address', 'Addr', 'Network Address')
    Database = Lay-Khoa $sb @('Database', 'Initial Catalog')
    User     = Lay-Khoa $sb @('User Id', 'UID', 'User')
    Password = Lay-Khoa $sb @('Password', 'PWD')
    TinCay   = $false
}
$tc = Lay-Khoa $sb @('Trusted_Connection', 'Integrated Security')
if ($tc -and $tc -match '^(true|yes|sspi)$') { $ketNoi.TinCay = $true }
if (-not $ketNoi.Server -or -not $ketNoi.Database) { throw 'Chuỗi kết nối thiếu Server hoặc Database.' }
if (-not $ketNoi.TinCay -and -not $ketNoi.User) { throw 'Chuỗi kết nối thiếu User Id (hoặc Trusted_Connection).' }
Write-Host ("Máy chủ SQL: {0} | CSDL: {1} | Đăng nhập: {2}" -f $ketNoi.Server, $ketNoi.Database, $(if ($ketNoi.TinCay) { 'Windows' } else { $ketNoi.User }))

$matKhauCu = $env:SQLCMDPASSWORD
if (-not $ketNoi.TinCay) { $env:SQLCMDPASSWORD = $ketNoi.Password }

$tepSaoLuu = $null
$daBatDauBuoc5 = $false
$coBanTruoc = Test-Path -LiteralPath (Join-Path $api 'DSVHVN.Api.dll')
$daDatOffline = $false

try {
    # ------------------------------------------------------------ 2. sao lưu
    Write-Buoc 'Sao lưu CSDL'
    if ($BoQuaSaoLuu) {
        Write-Host 'Bỏ qua sao lưu theo yêu cầu (-BoQuaSaoLuu).' -ForegroundColor Yellow
    } else {
        New-Item -ItemType Directory -Force -Path $ThuMucSaoLuu | Out-Null
        $tenTep = '{0}_{1}.bak' -f $ketNoi.Database, (Get-Date -Format 'yyyyMMdd_HHmmss')
        $tepSaoLuu = Join-Path $ThuMucSaoLuu $tenTep
        $duongDanSql = $tepSaoLuu.Replace("'", "''")
        $tenDb = $ketNoi.Database.Replace(']', ']]')
        Invoke-Sqlcmd2 $ketNoi @('-Q', "BACKUP DATABASE [$tenDb] TO DISK = N'$duongDanSql' WITH COPY_ONLY, INIT")
        Write-Host "Đã sao lưu: $tepSaoLuu"

        $cu = Get-ChildItem -LiteralPath $ThuMucSaoLuu -Filter ($ketNoi.Database + '_*.bak') -File |
            Sort-Object LastWriteTime -Descending | Select-Object -Skip 10
        foreach ($f in $cu) {
            Remove-Item -LiteralPath $f.FullName -Force
            Write-Host "Xoá bản sao lưu cũ: $($f.Name)"
        }
    }

    # ------------------------------------------------------------ 3. dừng ứng dụng
    Write-Buoc 'Dừng ứng dụng (app_offline.htm)'
    New-Item -ItemType Directory -Force -Path $api | Out-Null
    $trangOffline = @'
<!DOCTYPE html>
<html lang="vi"><head><meta charset="utf-8"><title>Đang cập nhật</title></head>
<body style="font-family:sans-serif;text-align:center;margin-top:20vh">
<h1>Đang cập nhật</h1><p>Hệ thống DSVHVN đang được cập nhật, vui lòng quay lại sau ít phút.</p>
</body></html>
'@
    [System.IO.File]::WriteAllText($appOffline, $trangOffline, (New-Object System.Text.UTF8Encoding($false)))
    $daDatOffline = $true
    Start-Sleep -Seconds 3

    # ------------------------------------------------------------ 4. migration
    Write-Buoc 'Áp migration (migrations.sql, idempotent)'
    Invoke-Sqlcmd2 $ketNoi @('-f', '65001', '-i', $goiSql)
    Write-Host 'Migration đã áp xong.'

    # ------------------------------------------------------------ 5. thay bản chạy
    Write-Buoc 'Thay bản chạy'
    $daBatDauBuoc5 = $true
    if ($coBanTruoc) {
        Write-Host 'Giữ bản đang chạy làm bản trước (api-truoc).'
        New-Item -ItemType Directory -Force -Path $apiTruoc | Out-Null
        Invoke-Robocopy $api $apiTruoc @('/XF', 'app_offline.htm', '/XD', 'logs')
    } else {
        Write-Host 'Lần triển khai đầu tiên: chưa có bản trước để giữ.'
    }
    Invoke-Robocopy $goiApi $api @('/XF', 'appsettings.Production.json', 'app_offline.htm', '/XD', 'logs')
    Copy-Item -LiteralPath $cauHinh -Destination (Join-Path $api 'appsettings.Production.json') -Force
    if (Test-Path -LiteralPath $phienBan) { Copy-Item -LiteralPath $phienBan -Destination (Join-Path $api 'phien-ban.txt') -Force }
    Write-Host 'Đã chép bản mới và cấu hình bí mật.'

    # ------------------------------------------------------------ 6. khởi động lại và kiểm sức khoẻ
    Write-Buoc "Khởi động lại và kiểm sức khoẻ ($KiemSucKhoeUrl, tối đa $ThoiGianChoGiay giây)"
    Remove-Item -LiteralPath $appOffline -Force -ErrorAction SilentlyContinue
    $daDatOffline = $false
    $han = (Get-Date).AddSeconds($ThoiGianChoGiay)
    $script:LoiSucKhoe = ''
    $khoe = $false
    do {
        if (Test-SucKhoe $KiemSucKhoeUrl) { $khoe = $true; break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $han)
    if (-not $khoe) {
        throw "Địa chỉ sức khoẻ không trả 200 trong $ThoiGianChoGiay giây. Lỗi cuối: $($script:LoiSucKhoe)"
    }
    Write-Host 'Ứng dụng đã khoẻ (200).' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Triển khai API thành công.' -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host "TRIỂN KHAI HỎNG: $($_.Exception.Message)" -ForegroundColor Red

    # ------------------------------------------------------------ 7. trả bản trước
    if ($daDatOffline -or $daBatDauBuoc5) {
        Write-Host 'Trả lại bản trước...' -ForegroundColor Yellow
        try {
            if ($daBatDauBuoc5 -and $coBanTruoc -and (Test-Path -LiteralPath $apiTruoc)) {
                Start-Sleep -Seconds 2
                Invoke-Robocopy $apiTruoc $api @('/XF', 'app_offline.htm', '/XD', 'logs')
                Write-Host 'Đã trả api-truoc về api.' -ForegroundColor Yellow
            } elseif ($daBatDauBuoc5) {
                Write-Host 'Không có bản trước để trả (lần triển khai đầu tiên).' -ForegroundColor Yellow
            } else {
                Write-Host 'Chưa thay bản chạy nên bản cũ còn nguyên.' -ForegroundColor Yellow
            }
        } catch {
            Write-Host "Không trả được bản trước: $($_.Exception.Message)" -ForegroundColor Red
        }
        Remove-Item -LiteralPath $appOffline -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    if ($tepSaoLuu) {
        Write-Host 'CSDL KHÔNG được tự khôi phục. Migration của dự án chỉ thêm nên bản trước thường vẫn chạy được.' -ForegroundColor Yellow
        Write-Host 'Nếu cần khôi phục CSDL từ bản sao lưu vừa tạo, chạy bằng quyền quản trị:'
        Write-Host ("  sqlcmd -S `"{0}`" -C -Q `"ALTER DATABASE [{1}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [{1}] FROM DISK = N'{2}' WITH REPLACE; ALTER DATABASE [{1}] SET MULTI_USER;`"" -f $ketNoi.Server, $ketNoi.Database, $tepSaoLuu)
    } else {
        Write-Host 'Không có tệp sao lưu của lần này (đã bỏ qua hoặc hỏng ngay từ bước sao lưu).' -ForegroundColor Yellow
    }
    exit 1
}
finally {
    if ($null -eq $matKhauCu) { Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue } else { $env:SQLCMDPASSWORD = $matKhauCu }
}
