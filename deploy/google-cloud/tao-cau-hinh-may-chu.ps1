<#
.SYNOPSIS
  Sinh cấu hình bí mật của máy chủ Google Cloud từ User Secrets của backend và tệp khóa của Dịch vụ AI, rồi tải lên (-TaiLen).

.DESCRIPTION
  Ghi vào %APPDATA%\DSVHVN\may-chu\ (không vào repo, không in giá trị bí mật):
    appsettings.Production.json  cấu hình API: chuỗi kết nối bằng đăng nhập dsvhvn_app, khóa ký JWT, VNPay, email, AI, kho tệp,
                                 địa chỉ hai web, CORS, quảng cáo, tài khoản quản trị seed
    ai.env                       biến của Dịch vụ AI (nhà cung cấp, khóa mô hình) cùng khóa nội bộ dùng chung với API
    bi-mat.env                   MSSQL_SA_PASSWORD và MAT_KHAU_CSDL_UNG_DUNG cho Docker Compose và script trên máy chủ
  Mật khẩu sa, mật khẩu dsvhvn_app, khóa ký JWT và khóa nội bộ AI sinh một lần rồi giữ trong bi-mat.json cùng thư mục: chạy lại
  (ví dụ sau khi đổi khóa Gmail hay Gemini) không đổi các giá trị này. Mật khẩu sa không đổi được sau khi SQL Server đã chạy.
  Khóa VNPay, email, AdMob, người nhận thư liên hệ, mật khẩu quản trị seed lấy từ User Secrets của src\DSVHVN.Api như máy demo.

.PARAMETER TaiLen
  Chép ba tệp lên máy chủ và cài bằng /opt/dsvhvn/bin/nap-cau-hinh.sh (API, Dịch vụ AI đang chạy được tạo lại).
.PARAMETER TepKhoaAi
  Tệp khóa của Dịch vụ AI trên máy dev, mặc định %APPDATA%\DSVHVN\ai-service.env.
.PARAMETER TatCongQuangCao
  Tắt cổng quảng cáo (Ads:GateEnabled = false).
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$TaiLen,
    [string]$TepKhoaAi = (Join-Path $env:APPDATA 'DSVHVN\ai-service.env'),
    [switch]$TatCongQuangCao
)

. (Join-Path $PSScriptRoot 'chung.ps1')

$haTang = Read-HaTang
$diaChiHocTap = [string]$haTang.DiaChiHocTap
$diaChiQuanTri = [string]$haTang.DiaChiQuanTri
$thuMucApi = (Resolve-Path (Join-Path $PSScriptRoot '..\..\src\DSVHVN.Api')).Path
$tepBiMatMayChu = Join-Path $script:ThuMucMayChu 'bi-mat.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function New-ChuoiNgauNhien([int]$doDai) {
    # Chỉ chữ và số (đặt thẳng vào chuỗi kết nối, câu lệnh SQL, tệp .env); luôn có chữ hoa, chữ thường, chữ số cho chính sách mật khẩu SQL.
    $kyTu = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'.ToCharArray()
    do {
        $s = -join (1..$doDai | ForEach-Object { $kyTu[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($kyTu.Length)] })
    } until ($s -cmatch '[A-Z]' -and $s -cmatch '[a-z]' -and $s -match '[0-9]')
    return $s
}

# ---------------------------------------------------------------- bí mật riêng của máy chủ (sinh một lần)
Write-Buoc 'Bí mật riêng của máy chủ'
New-Item -ItemType Directory -Force -Path $script:ThuMucMayChu | Out-Null
$biMat = [ordered]@{}
if (Test-Path -LiteralPath $tepBiMatMayChu) {
    $cu = Get-Content -LiteralPath $tepBiMatMayChu -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in $cu.PSObject.Properties) { $biMat[$p.Name] = [string]$p.Value }
}
$moi = @()
if (-not $biMat.Contains('MatKhauSa')) { $biMat['MatKhauSa'] = New-ChuoiNgauNhien 32; $moi += 'mật khẩu sa' }
if (-not $biMat.Contains('MatKhauUngDung')) { $biMat['MatKhauUngDung'] = New-ChuoiNgauNhien 32; $moi += 'mật khẩu dsvhvn_app' }
if (-not $biMat.Contains('KhoaKyJwt')) {
    $byte = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(64)
    $biMat['KhoaKyJwt'] = [Convert]::ToBase64String($byte)
    $moi += 'khóa ký JWT'
}
if (-not $biMat.Contains('KhoaNoiBoAi')) { $biMat['KhoaNoiBoAi'] = New-ChuoiNgauNhien 48; $moi += 'khóa nội bộ AI' }
[System.IO.File]::WriteAllText($tepBiMatMayChu, ($biMat | ConvertTo-Json), $utf8)
Write-Host $(if ($moi) { "Đã sinh: $($moi -join ', ')." } else { 'Giữ nguyên các giá trị đã sinh trước đây.' })

# ---------------------------------------------------------------- User Secrets của backend
$csproj = Get-Content -LiteralPath (Join-Path $thuMucApi 'DSVHVN.Api.csproj') -Raw
if ($csproj -notmatch '<UserSecretsId>([^<]+)</UserSecretsId>') { throw 'csproj không có UserSecretsId.' }
$tepUserSecrets = Join-Path $env:APPDATA ("Microsoft\UserSecrets\{0}\secrets.json" -f $Matches[1].Trim())
if (-not (Test-Path -LiteralPath $tepUserSecrets)) { throw "Không thấy tệp User Secrets: $tepUserSecrets" }
$userSecrets = @{}
foreach ($p in (Get-Content -LiteralPath $tepUserSecrets -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties) { $userSecrets[$p.Name] = $p.Value }
function Lay-BiMat([string]$khoa) {
    if ($userSecrets.ContainsKey($khoa) -and -not [string]::IsNullOrWhiteSpace([string]$userSecrets[$khoa])) { return [string]$userSecrets[$khoa] }
    return $null
}
function Lay-DanhSach([string]$tienTo) {
    $mau = '^' + [regex]::Escape($tienTo) + ':(\d+)$'
    $kq = @($userSecrets.Keys | Where-Object { $_ -match $mau } |
        Sort-Object { [int]([regex]::Match($_, $mau).Groups[1].Value) } |
        ForEach-Object { [string]$userSecrets[$_] } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return , $kq
}
$dev = Get-Content -LiteralPath (Join-Path $thuMucApi 'appsettings.Development.json') -Raw -Encoding UTF8 | ConvertFrom-Json

# ---------------------------------------------------------------- appsettings.Production.json
Write-Buoc 'appsettings.Production.json của API'
$cauHinh = [ordered]@{}
$daGhi = New-Object System.Collections.Generic.List[string]
function Dat([string]$duongDan, $giaTri) {
    $phan = $duongDan -split ':'
    $nut = $script:cauHinh
    for ($i = 0; $i -lt $phan.Count - 1; $i++) {
        if (-not $nut.Contains($phan[$i])) { $nut[$phan[$i]] = [ordered]@{} }
        $nut = $nut[$phan[$i]]
    }
    $nut[$phan[-1]] = $giaTri
    $script:daGhi.Add($duongDan)
}

Dat 'ConnectionStrings:Default' "Server=sql,1433;Database=dsvhvn;User Id=dsvhvn_app;Password=$($biMat['MatKhauUngDung']);TrustServerCertificate=True;Encrypt=True"
Dat 'Database:InitMode' 'None'
Dat 'Jwt:SigningKey' $biMat['KhoaKyJwt']

$tmn = Lay-BiMat 'VnPay:TmnCode'
$hash = Lay-BiMat 'VnPay:HashSecret'
if ($tmn -and $hash) { Dat 'VnPay:TmnCode' $tmn; Dat 'VnPay:HashSecret' $hash }
else { Write-Warning 'User Secrets thiếu VnPay:TmnCode hoặc VnPay:HashSecret: thanh toán sẽ không dùng được.' }
Dat 'VnPay:ReturnUrl' "$diaChiHocTap/api/v1/payments/vnpay/return"

foreach ($k in ($userSecrets.Keys | Where-Object { $_ -like 'Email:*' } | Sort-Object)) {
    $v = Lay-BiMat $k
    if ($null -eq $v) { continue }
    if ($k -eq 'Email:Smtp:Port' -and $v -match '^\d+$') { $v = [int]$v }
    if ($k -eq 'Email:Smtp:UseStartTls' -and $v -match '^(true|false)$') { $v = ($v -eq 'true') }
    Dat $k $v
}
if (-not (Lay-BiMat 'Email:Mode')) { Dat 'Email:Mode' 'Log' }
$nguoiNhan = Lay-DanhSach 'SchoolInquiry:Recipients'
if ($nguoiNhan.Count -gt 0) { Dat 'SchoolInquiry:Recipients' $nguoiNhan }
else { Write-Warning 'User Secrets chưa có SchoolInquiry:Recipients: thư liên hệ chỉ tới quản trị hệ thống có email thật.' }

# Dịch vụ AI chạy trong cùng mạng Docker (tên dịch vụ "ai").
Dat 'Ai:Mode' 'Remote'
Dat 'Ai:BaseUrl' 'http://ai:8000'
Dat 'Ai:InternalKey' $biMat['KhoaNoiBoAi']

Dat 'Storage:Provider' 'Local'
Dat 'Storage:Local:RootPath' '/data/storage'

Dat 'App:FrontendBaseUrl' $diaChiQuanTri
Dat 'App:LearningWebBaseUrl' $diaChiHocTap
Dat 'Cors:AllowedOrigins' @($diaChiQuanTri, $diaChiHocTap)

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

$matKhauAdmin = Lay-BiMat 'Seed:AdminPassword'
if ($matKhauAdmin) {
    Dat 'Seed:AdminPassword' $matKhauAdmin
    if ($dev.Seed.Admins) { Dat 'Seed:Admins' @($dev.Seed.Admins) }
} else { Write-Warning 'User Secrets không có Seed:AdminPassword, bỏ qua mục Seed.' }

$tepApi = Join-Path $script:ThuMucMayChu 'appsettings.Production.json'
[System.IO.File]::WriteAllText($tepApi, ($cauHinh | ConvertTo-Json -Depth 10), $utf8)
Write-Host "Đã ghi $tepApi. Các khóa (không in giá trị):"
$daGhi | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }

# ---------------------------------------------------------------- ai.env
Write-Buoc 'ai.env của Dịch vụ AI'
if (-not (Test-Path -LiteralPath $TepKhoaAi)) { throw "Không thấy $TepKhoaAi." }
$bienAi = [ordered]@{}
foreach ($dong in Get-Content -LiteralPath $TepKhoaAi -Encoding UTF8) {
    $t = $dong.Trim()
    if (-not $t -or $t.StartsWith('#')) { continue }
    if ($t -notmatch '^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { continue }
    $ten = $Matches[1].ToUpperInvariant()
    if (-not ($ten.StartsWith('AI_') -or $ten.StartsWith('GEMINI_') -or $ten.StartsWith('ANTHROPIC_'))) { continue }
    # Địa chỉ nghe và tệp cấu hình do container tự đặt; khóa nội bộ dùng khóa riêng của máy chủ.
    if ($ten -in @('AI_ENV_FILE', 'AI_HOST', 'AI_PORT', 'AI_INTERNAL_KEY')) { continue }
    $bienAi[$ten] = $Matches[2].Trim().Trim('"').Trim("'")
}
$bienAi['AI_INTERNAL_KEY'] = $biMat['KhoaNoiBoAi']
$nhaCungCap = if ($bienAi.Contains('AI_PROVIDER') -and $bienAi['AI_PROVIDER']) { $bienAi['AI_PROVIDER'].ToLowerInvariant() } else { 'anthropic' }
# Chỉ đưa lên máy chủ khóa của nhà cung cấp đang dùng.
$tienToBo = if ($nhaCungCap -eq 'gemini') { 'ANTHROPIC_' } else { 'GEMINI_' }
foreach ($ten in @($bienAi.Keys | Where-Object { $_.StartsWith($tienToBo) })) { $bienAi.Remove($ten) }
$khoaMoHinh = if ($nhaCungCap -eq 'gemini') { 'GEMINI_API_KEY' } else { 'ANTHROPIC_API_KEY' }
if (-not $bienAi.Contains($khoaMoHinh) -or -not $bienAi[$khoaMoHinh]) {
    Write-Warning "Chưa có $khoaMoHinh cho nhà cung cấp '$nhaCungCap': các chức năng AI sẽ báo dịch vụ chưa sẵn sàng."
}
$tepAi = Join-Path $script:ThuMucMayChu 'ai.env'
$dongAi = @('# Sinh bởi tao-cau-hinh-may-chu.ps1. Không đưa tệp này vào repo.') + @($bienAi.Keys | ForEach-Object { "$_=$($bienAi[$_])" })
[System.IO.File]::WriteAllText($tepAi, (($dongAi -join "`n") + "`n"), $utf8)
Write-Host "Đã ghi $tepAi ($($bienAi.Count) biến: $(@($bienAi.Keys) -join ', '); nhà cung cấp $nhaCungCap)."

# ---------------------------------------------------------------- bi-mat.env
$tepBien = Join-Path $script:ThuMucMayChu 'bi-mat.env'
[System.IO.File]::WriteAllText($tepBien, ("MSSQL_SA_PASSWORD=$($biMat['MatKhauSa'])`nMAT_KHAU_CSDL_UNG_DUNG=$($biMat['MatKhauUngDung'])`n"), $utf8)
Write-Host "Đã ghi $tepBien."

if (-not $TaiLen) {
    Write-Host ''
    Write-Host 'Chưa tải lên máy chủ (thêm -TaiLen).'
    return
}

# ---------------------------------------------------------------- tải lên
Write-Buoc "Tải cấu hình lên $($haTang.TenMay)"
$goi = Join-Path ([System.IO.Path]::GetTempPath()) "dsvhvn-cau-hinh-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $goi | Out-Null
try {
    Copy-Item -LiteralPath $tepApi, $tepAi, $tepBien -Destination $goi
    # Chép vào thư mục nhà trên máy chủ (người khác không đọc được, khác /tmp); nap-cau-hinh.sh xóa sau khi cài.
    $ten = Split-Path -Leaf $goi
    Copy-LenMayChu $haTang $goi $ten
    Invoke-MayChu $haTang "sudo /opt/dsvhvn/bin/nap-cau-hinh.sh `$HOME/$ten"
} finally {
    Remove-Item -LiteralPath $goi -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Đã cài cấu hình trên máy chủ.' -ForegroundColor Green
