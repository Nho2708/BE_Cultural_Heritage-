# Đặt khóa MoMo sandbox và PayOS vào User Secrets của backend (không vào appsettings, không vào kho mã nguồn).
#
# Dùng (trong thư mục BE_Cultural_Heritage-):
#   pwsh -File scripts/dat-khoa-momo-payos.ps1 -PublicBaseUrl https://<tên miền công khai hoặc ngrok>
#
# MoMo: bộ khóa thử công khai ở https://developers.momo.vn (mục Môi trường thử nghiệm); đối chiếu lại trước khi dùng.
# PayOS: my.payos.vn → Kênh thanh toán → Client ID, API Key, Checksum Key. Bỏ trống một cổng nếu chưa có khóa.
# Sau khi đặt: khởi động lại backend, bật cổng trong CSDL (xem cuối script), khai webhook PayOS ở my.payos.vn.
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $PublicBaseUrl)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\DSVHVN.Api'
$base = $PublicBaseUrl.TrimEnd('/')

function Dat($ten, $giaTri) {
    dotnet user-secrets set $ten $giaTri --project $project | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Không đặt được $ten." }
}
function Hoi-An($nhan) {
    $s = Read-Host $nhan -AsSecureString
    [System.Net.NetworkCredential]::new('', $s).Password.Trim()
}

$partner = (Read-Host 'MoMo PartnerCode (bỏ trống để bỏ qua MoMo)').Trim()
if ($partner) {
    Dat 'MoMo:PartnerCode' $partner
    Dat 'MoMo:AccessKey' (Read-Host 'MoMo AccessKey').Trim()
    Dat 'MoMo:SecretKey' (Hoi-An 'MoMo SecretKey')
    Dat 'MoMo:ReturnUrl' "$base/api/v1/payments/momo/return"
    Dat 'MoMo:IpnUrl' "$base/api/v1/payments/momo/ipn"
    Write-Host 'Đã đặt khóa MoMo.'
}

$clientId = (Read-Host 'PayOS Client ID (bỏ trống để bỏ qua PayOS)').Trim()
if ($clientId) {
    Dat 'PayOs:ClientId' $clientId
    Dat 'PayOs:ApiKey' (Hoi-An 'PayOS API Key')
    Dat 'PayOs:ChecksumKey' (Hoi-An 'PayOS Checksum Key')
    Dat 'PayOs:ReturnUrl' "$base/api/v1/payments/payos/return"
    Write-Host 'Đã đặt khóa PayOS.'
    Write-Host "Khai webhook ở my.payos.vn: $base/api/v1/payments/payos/webhook"
}

Write-Host ''
Write-Host 'Bật cổng trong CSDL (chạy một lần):'
Write-Host "  UPDATE payment_providers SET is_active = 1 WHERE code IN ('MOMO', 'PAYOS');"
Write-Host 'Khởi động lại backend để nạp khóa.'
