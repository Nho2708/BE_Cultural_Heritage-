<#
.SYNOPSIS
  Tạo appsettings.Production.json của máy demo từ User Secrets của backend (không cần quyền quản trị).

.DESCRIPTION
  Đọc %APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json của src\DSVHVN.Api (khoá dạng phẳng A:B),
  rồi ghi C:\dsvhvn\cau-hinh\appsettings.Production.json. Không in giá trị bí mật, chỉ in danh sách khoá đã ghi.
  Khoá ký JWT đã có trong tệp đích thì giữ nguyên (đổi khoá làm mọi phiên đăng nhập mất hiệu lực).

.PARAMETER TenCsdl
  Tên CSDL demo, mặc định dsvhvn_demo (đổi Database trong chuỗi kết nối của User Secrets).
.PARAMETER TepDich
  Tệp cấu hình đích, mặc định C:\dsvhvn\cau-hinh\appsettings.Production.json.
.PARAMETER DiaChiNgrok
  Địa chỉ công khai của đường hầm ngrok (đã khai cho VNPay và AdMob).
.PARAMETER TatCongQuangCao
  Tắt cổng quảng cáo (Ads:GateEnabled = false).
.PARAMETER TepBiMat
  Ghi đè đường dẫn secrets.json (mặc định suy ra từ UserSecretsId trong csproj).
.PARAMETER CheDoAi
  TuDong (mặc định): Dịch vụ AI trên máy demo trả 200 ở <DiaChiAi>/health thì dùng AI thật (Remote), không thì giả lập.
  Remote hoặc Simulated: đặt thẳng, không kiểm.
.PARAMETER DiaChiAi
  Địa chỉ Dịch vụ AI trên máy demo (site IIS "DSVHVN AI" do cai-dat-may-demo-ai.ps1 của repo AI tạo), mặc định http://127.0.0.1:8090.
.PARAMETER ApDungNgay
  Chép tệp vừa tạo vào C:\dsvhvn\api và khởi động lại API (chạm web.config) thay vì chờ lần triển khai sau.
#>
[CmdletBinding()]
param(
    [string]$TenCsdl = 'dsvhvn_demo',
    [string]$TepDich = 'C:\dsvhvn\cau-hinh\appsettings.Production.json',
    [string]$DiaChiNgrok = 'https://maynard-unphysical-planographically.ngrok-free.dev',
    [switch]$TatCongQuangCao,
    [string]$TepBiMat,
    [ValidateSet('TuDong', 'Remote', 'Simulated')][string]$CheDoAi = 'TuDong',
    [string]$DiaChiAi = 'http://127.0.0.1:8090',
    [switch]$ApDungNgay
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$goiRepo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$thuMucApi = Join-Path $goiRepo 'src\DSVHVN.Api'

# ---------------------------------------------------------------- user-secrets
if (-not $TepBiMat) {
    $csproj = Get-Content -LiteralPath (Join-Path $thuMucApi 'DSVHVN.Api.csproj') -Raw
    if ($csproj -notmatch '<UserSecretsId>([^<]+)</UserSecretsId>') { throw 'csproj không có UserSecretsId.' }
    $TepBiMat = Join-Path $env:APPDATA ("Microsoft\UserSecrets\{0}\secrets.json" -f $Matches[1].Trim())
}
if (-not (Test-Path -LiteralPath $TepBiMat)) { throw "Không thấy tệp User Secrets: $TepBiMat" }

$bimat = @{}
$doc = Get-Content -LiteralPath $TepBiMat -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($p in $doc.PSObject.Properties) { $bimat[$p.Name] = $p.Value }

function Lay-BiMat([string]$khoa) {
    if ($bimat.ContainsKey($khoa) -and -not [string]::IsNullOrWhiteSpace([string]$bimat[$khoa])) { return [string]$bimat[$khoa] }
    return $null
}
function Lay-DanhSach([string]$tienTo) {
    # Khoá dạng "Mục:Mảng:0", "Mục:Mảng:1"... -> mảng theo thứ tự chỉ số.
    $kq = @()
    $mau = '^' + [regex]::Escape($tienTo) + ':(\d+)$'
    $bimat.Keys | Where-Object { $_ -match $mau } |
        Sort-Object { [int]([regex]::Match($_, $mau).Groups[1].Value) } |
        ForEach-Object { if (-not [string]::IsNullOrWhiteSpace([string]$bimat[$_])) { $kq += [string]$bimat[$_] } }
    return , $kq
}

# ---------------------------------------------------------------- cấu hình Development (đơn vị quảng cáo thử của Google)
$dev = Get-Content -LiteralPath (Join-Path $thuMucApi 'appsettings.Development.json') -Raw -Encoding UTF8 | ConvertFrom-Json

# ---------------------------------------------------------------- cây cấu hình đích
$cauHinh = [ordered]@{}
$daGhi = New-Object System.Collections.Generic.List[string]
function Dat([string]$duongDan, $giaTri) {
    # "A:B:C" -> cauHinh.A.B.C
    $phan = $duongDan -split ':'
    $nut = $script:cauHinh
    for ($i = 0; $i -lt $phan.Count - 1; $i++) {
        if (-not $nut.Contains($phan[$i])) { $nut[$phan[$i]] = [ordered]@{} }
        $nut = $nut[$phan[$i]]
    }
    $nut[$phan[-1]] = $giaTri
    $script:daGhi.Add($duongDan)
}

# Chuỗi kết nối: lấy từ User Secrets, đổi Database sang CSDL demo.
$chuoiGoc = Lay-BiMat 'ConnectionStrings:Default'
if (-not $chuoiGoc) { throw 'User Secrets không có ConnectionStrings:Default.' }
$sb = New-Object System.Data.Common.DbConnectionStringBuilder
# PowerShell coi bộ dựng là từ điển nên gán thẳng .ConnectionString sẽ thành một khoá; đặt và đọc qua reflection.
$thuocTinhChuoi = [System.Data.Common.DbConnectionStringBuilder].GetProperty('ConnectionString')
$thuocTinhChuoi.SetValue($sb, $chuoiGoc)
[void]$sb.Remove('Initial Catalog')
$sb['Database'] = $TenCsdl
Dat 'ConnectionStrings:Default' ([string]$thuocTinhChuoi.GetValue($sb))

Dat 'Database:InitMode' 'None'

# Khoá ký JWT: giữ nếu đích đã có, chưa có thì sinh mới 64 byte.
$khoaJwt = $null
if (Test-Path -LiteralPath $TepDich) {
    try {
        $cu = Get-Content -LiteralPath $TepDich -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($cu.PSObject.Properties['Jwt'] -and $cu.Jwt.PSObject.Properties['SigningKey'] -and $cu.Jwt.SigningKey) { $khoaJwt = [string]$cu.Jwt.SigningKey }
    } catch { Write-Warning 'Tệp đích hiện có không đọc được, sẽ sinh khoá ký JWT mới.' }
}
if ($khoaJwt) { Write-Host 'Giữ nguyên khoá ký JWT hiện có.' }
else {
    $byte = New-Object byte[] 64
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($byte); $rng.Dispose()
    $khoaJwt = [Convert]::ToBase64String($byte)
    Write-Host 'Đã sinh khoá ký JWT mới.'
}
Dat 'Jwt:SigningKey' $khoaJwt

# VNPay
$tmn = Lay-BiMat 'VnPay:TmnCode'
$hash = Lay-BiMat 'VnPay:HashSecret'
if (-not $tmn -or -not $hash) { Write-Warning 'User Secrets thiếu VnPay:TmnCode hoặc VnPay:HashSecret (thanh toán sẽ không dùng được).' }
if ($tmn) { Dat 'VnPay:TmnCode' $tmn }
if ($hash) { Dat 'VnPay:HashSecret' $hash }
Dat 'VnPay:ReturnUrl' ($DiaChiNgrok.TrimEnd('/') + '/api/v1/payments/vnpay/return')

# Email: mọi khoá Email:* trong User Secrets.
foreach ($k in ($bimat.Keys | Where-Object { $_ -like 'Email:*' } | Sort-Object)) {
    $v = Lay-BiMat $k
    if ($null -ne $v) {
        if ($k -eq 'Email:Smtp:Port' -and $v -match '^\d+$') { $v = [int]$v }
        if ($k -eq 'Email:Smtp:UseStartTls' -and $v -match '^(true|false)$') { $v = ($v -eq 'true') }
        Dat $k $v
    }
}
if (-not (Lay-BiMat 'Email:Mode')) { Dat 'Email:Mode' 'Log' }

# AI: dịch vụ thật trên máy demo nếu đã chạy (site IIS 127.0.0.1:8090), không thì giả lập.
# Không lấy Ai:Mode, Ai:BaseUrl của User Secrets: đó là cấu hình máy dev (dịch vụ chạy tay ở cổng 8000).
$diaChiAi = $DiaChiAi.TrimEnd('/')
$cheDo = $CheDoAi
if ($cheDo -eq 'TuDong') {
    try {
        $r = Invoke-WebRequest -Uri "$diaChiAi/health" -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
        $cheDo = if ($r.StatusCode -eq 200) { 'Remote' } else { 'Simulated' }
    } catch {
        $cheDo = 'Simulated'
    }
    if ($cheDo -eq 'Simulated') {
        Write-Warning "Dịch vụ AI chưa trả lời ở $diaChiAi/health: dùng AI giả lập. Cài và triển khai Dịch vụ AI (repo AI, scripts\trien-khai) rồi chạy lại script này."
    }
}
Dat 'Ai:Mode' $cheDo
if ($cheDo -eq 'Remote') { Dat 'Ai:BaseUrl' $diaChiAi }
$khoaAi = Lay-BiMat 'Ai:InternalKey'
if ($khoaAi) { Dat 'Ai:InternalKey' $khoaAi } elseif ($cheDo -eq 'Remote') { throw 'Dùng AI thật cần Ai:InternalKey trong User Secrets (cùng giá trị AI_INTERNAL_KEY của Dịch vụ AI).' }

# Kho tệp: đĩa cục bộ.
Dat 'Storage:Provider' 'Local'
Dat 'Storage:Local:RootPath' 'C:\dsvhvn\du-lieu\storage'

# Địa chỉ ứng dụng và CORS.
$ngrok = $DiaChiNgrok.TrimEnd('/')
Dat 'App:FrontendBaseUrl' 'http://localhost:8081'
Dat 'App:LearningWebBaseUrl' $ngrok
Dat 'Cors:AllowedOrigins' @('http://localhost:8081', 'http://localhost:8082', $ngrok)

# Quảng cáo.
$android = @(); if ($dev.Ads.AdMob.AndroidAdUnitIds) { $android += @($dev.Ads.AdMob.AndroidAdUnitIds) }
$android += Lay-DanhSach 'Ads:AdMob:AndroidAdUnitIds'
$ios = @(); if ($dev.Ads.AdMob.IosAdUnitIds) { $ios += @($dev.Ads.AdMob.IosAdUnitIds) }
$ios += Lay-DanhSach 'Ads:AdMob:IosAdUnitIds'
$duongDanQc = @(); if ($dev.Ads.AdManager.AdUnitPaths) { $duongDanQc += @($dev.Ads.AdManager.AdUnitPaths) }
$duongDanQc += Lay-DanhSach 'Ads:AdManager:AdUnitPaths'
Dat 'Ads:GateEnabled' (-not $TatCongQuangCao)
Dat 'Ads:AllowNoAdsDeclaration' $false
Dat 'Ads:AdMob:AndroidAdUnitIds' @($android | Select-Object -Unique)
Dat 'Ads:AdMob:IosAdUnitIds' @($ios | Select-Object -Unique)
Dat 'Ads:AdManager:AdUnitPaths' @($duongDanQc | Select-Object -Unique)

# Tài khoản quản trị seed: danh sách lấy từ cấu hình Development, mật khẩu chung từ User Secrets.
# Tài khoản đã có trong CSDL demo thì bỏ qua, không ghi đè mật khẩu.
$matKhauAdmin = Lay-BiMat 'Seed:AdminPassword'
if ($matKhauAdmin) {
    Dat 'Seed:AdminPassword' $matKhauAdmin
    if ($dev.Seed.Admins) { Dat 'Seed:Admins' @($dev.Seed.Admins) }
} else {
    Write-Warning 'User Secrets không có Seed:AdminPassword, bỏ qua mục Seed.'
}

# ---------------------------------------------------------------- ghi tệp
$thuMuc = Split-Path -Parent $TepDich
New-Item -ItemType Directory -Force -Path $thuMuc | Out-Null
$json = $cauHinh | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($TepDich, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Đã ghi: $TepDich" -ForegroundColor Green
Write-Host 'Các khoá đã ghi (không in giá trị):'
$daGhi | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
Write-Host "AI: $cheDo$(if ($cheDo -eq 'Remote') { " ($diaChiAi)" })"

if ($ApDungNgay) {
    # API đọc appsettings lúc khởi động: chép tệp rồi chạm web.config để IIS khởi động lại ứng dụng.
    $thuApi = 'C:\dsvhvn\api'
    if (-not (Test-Path -LiteralPath (Join-Path $thuApi 'web.config'))) { throw "Chưa có bản API ở $thuApi (chạy CD của backend trước)." }
    Copy-Item -LiteralPath $TepDich -Destination (Join-Path $thuApi 'appsettings.Production.json') -Force
    (Get-Item -LiteralPath (Join-Path $thuApi 'web.config')).LastWriteTime = Get-Date
    Write-Host "Đã chép vào $thuApi và khởi động lại API." -ForegroundColor Green
}
Write-Host ''
Write-Host 'Lưu ý: cau-hinh\ chứa bí mật, chỉ cấp quyền cho quản trị viên, người dùng hiện tại và tài khoản chạy ứng dụng (do cai-dat-may-demo.ps1 làm).'
