<#
.SYNOPSIS
  Sao lưu CSDL của máy demo (BACKUP ... COPY_ONLY) vào thư mục sao lưu hằng đêm, giữ số bản mới nhất đã chọn.

.DESCRIPTION
  Workflow "Sao lưu hằng đêm" gọi script này trên runner của máy demo; chạy tay được bằng đúng script:
    pwsh scripts/trien-khai/sao-luu-csdl.ps1

  Chuỗi kết nối đọc từ <GocMay>\cau-hinh\appsettings.Production.json (không in ra), mật khẩu đi qua biến môi trường của sqlcmd.
  Tệp ghi vào <GocMay>\sao-luu\hang-dem\, tách khỏi bản sao lưu trước mỗi lần triển khai (sao-luu\*.bak) để hai cách xoay vòng không xoá nhầm của nhau.
  COPY_ONLY nên không làm đổi chuỗi sao lưu của SQL Server.

.PARAMETER GocMay
  Gốc triển khai, mặc định C:\dsvhvn.
.PARAMETER ThuMucSaoLuu
  Nơi ghi tệp .bak, mặc định <GocMay>\sao-luu\hang-dem. Dịch vụ SQL Server phải ghi được vào đây.
.PARAMETER GiuSoBan
  Số bản mới nhất được giữ, mặc định 14.
.PARAMETER TepCauHinh
  Tệp chứa ConnectionStrings:Default, mặc định <GocMay>\cau-hinh\appsettings.Production.json.
#>
[CmdletBinding()]
param(
    [string]$GocMay = 'C:\dsvhvn',
    [string]$ThuMucSaoLuu,
    [ValidateRange(1, 365)][int]$GiuSoBan = 14,
    [string]$TepCauHinh
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if (-not $ThuMucSaoLuu) { $ThuMucSaoLuu = Join-Path $GocMay 'sao-luu\hang-dem' }
if (-not $TepCauHinh) { $TepCauHinh = Join-Path $GocMay 'cau-hinh\appsettings.Production.json' }
if (-not (Test-Path -LiteralPath $TepCauHinh)) { throw "Chưa có $TepCauHinh. Chạy scripts\trien-khai\tao-cau-hinh-may-demo.ps1 trước." }
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw 'Không thấy sqlcmd trong PATH.' }

# ---------------------------------------------------------------- kết nối
$cfg = Get-Content -LiteralPath $TepCauHinh -Raw -Encoding UTF8 | ConvertFrom-Json
$chuoi = $cfg.ConnectionStrings.Default
if ([string]::IsNullOrWhiteSpace($chuoi)) { throw "Thiếu ConnectionStrings:Default trong $TepCauHinh." }
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
$server = Lay-Khoa $sb @('Server', 'Data Source', 'Address', 'Addr', 'Network Address')
$database = Lay-Khoa $sb @('Database', 'Initial Catalog')
$user = Lay-Khoa $sb @('User Id', 'UID', 'User')
$password = Lay-Khoa $sb @('Password', 'PWD')
$tc = Lay-Khoa $sb @('Trusted_Connection', 'Integrated Security')
$tinCay = [bool]($tc -and $tc -match '^(true|yes|sspi)$')
if (-not $server -or -not $database) { throw 'Chuỗi kết nối thiếu Server hoặc Database.' }
if (-not $tinCay -and -not $user) { throw 'Chuỗi kết nối thiếu User Id (hoặc Trusted_Connection).' }
Write-Host ("Máy chủ SQL: {0} | CSDL: {1}" -f $server, $database)

# ---------------------------------------------------------------- sao lưu
New-Item -ItemType Directory -Force -Path $ThuMucSaoLuu | Out-Null
$tep = Join-Path $ThuMucSaoLuu ('{0}_{1}.bak' -f $database, (Get-Date -Format 'yyyyMMdd_HHmmss'))
$sql = "BACKUP DATABASE [{0}] TO DISK = N'{1}' WITH COPY_ONLY, INIT, CHECKSUM" -f $database.Replace(']', ']]'), $tep.Replace("'", "''")

$matKhauCu = $env:SQLCMDPASSWORD
try {
    $thamSo = @('-S', $server, '-d', 'master', '-C', '-b', '-I')
    if ($tinCay) { $thamSo += '-E' } else { $thamSo += @('-U', $user); $env:SQLCMDPASSWORD = $password }
    & sqlcmd @thamSo -Q $sql
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd trả mã lỗi $LASTEXITCODE khi sao lưu." }
    # Đọc lại phần đầu tệp để chắc tệp dùng được khi cần khôi phục.
    & sqlcmd @thamSo -Q ("RESTORE VERIFYONLY FROM DISK = N'{0}' WITH CHECKSUM" -f $tep.Replace("'", "''"))
    if ($LASTEXITCODE -ne 0) { throw "Kiểm tệp sao lưu lỗi (mã $LASTEXITCODE)." }
} finally {
    if ($null -eq $matKhauCu) { Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue } else { $env:SQLCMDPASSWORD = $matKhauCu }
}
$kichThuoc = [math]::Round((Get-Item -LiteralPath $tep).Length / 1MB, 1)
Write-Host "Đã sao lưu và kiểm: $tep ($kichThuoc MB)" -ForegroundColor Green

# ---------------------------------------------------------------- xoay vòng
$cu = Get-ChildItem -LiteralPath $ThuMucSaoLuu -Filter ($database + '_*.bak') -File |
    Sort-Object LastWriteTime -Descending | Select-Object -Skip $GiuSoBan
foreach ($f in $cu) {
    Remove-Item -LiteralPath $f.FullName -Force
    Write-Host "Xoá bản cũ: $($f.Name)"
}
$con = @(Get-ChildItem -LiteralPath $ThuMucSaoLuu -Filter ($database + '_*.bak') -File)
Write-Host "Đang giữ $($con.Count) bản (tối đa $GiuSoBan)."
