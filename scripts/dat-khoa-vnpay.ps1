# Đặt khóa VNPay sandbox của nhóm (mã website và chuỗi bí mật trong email VNPay gửi khi đăng ký) vào User Secrets của backend.
#
# Dùng (trong thư mục BE_Cultural_Heritage-):
#   pwsh -File scripts/dat-khoa-vnpay.ps1
#   pwsh -File scripts/dat-khoa-vnpay.ps1 -PublicBaseUrl https://<tên miền công khai>   # in thêm địa chỉ IPN cần khai ở trang quản lý VNPay
#
# Script hỏi hai giá trị; chuỗi bí mật nhập ẩn nên không hiện trên màn hình và không vào lịch sử lệnh. Khóa chỉ nằm trong User Secrets trên máy này
# (không vào appsettings, không vào kho mã nguồn). Đặt xong phải khởi động lại backend để nạp khóa.
# Địa chỉ IPN (VNPay gọi máy chủ khi giao dịch xong) khai ở https://sandbox.vnpayment.vn/merchantv2/ → Thông tin tài khoản → sửa website → IPN URL.
[CmdletBinding()]
param(
    [string] $PublicBaseUrl
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\DSVHVN.Api'

$tmnCode = (Read-Host 'Mã website (vnp_TmnCode)').Trim()
$secure = Read-Host 'Chuỗi bí mật (vnp_HashSecret)' -AsSecureString
$hashSecret = [System.Net.NetworkCredential]::new('', $secure).Password.Trim()
if (-not $tmnCode -or -not $hashSecret) { throw 'Thiếu mã website hoặc chuỗi bí mật.' }
if ($tmnCode -notmatch '^[A-Za-z0-9]{4,20}$') { throw 'Mã website chỉ gồm chữ và số (thường 8 ký tự), hãy kiểm lại email của VNPay.' }

dotnet user-secrets set 'VnPay:TmnCode' $tmnCode --project $project | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Không đặt được VnPay:TmnCode.' }
dotnet user-secrets set 'VnPay:HashSecret' $hashSecret --project $project | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Không đặt được VnPay:HashSecret.' }
$hashSecret = $null

Write-Host 'Đã đặt VnPay:TmnCode và VnPay:HashSecret trong User Secrets. Khởi động lại backend để nạp khóa.'
if ($PublicBaseUrl) {
    Write-Host "Địa chỉ IPN khai ở trang quản lý VNPay: $($PublicBaseUrl.TrimEnd('/'))/api/v1/payments/vnpay/ipn"
}
