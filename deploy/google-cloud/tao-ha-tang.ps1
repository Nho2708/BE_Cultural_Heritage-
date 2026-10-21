<#
.SYNOPSIS
  Tạo hạ tầng Google Cloud cho DiViet và cài máy chủ (chạy trên máy dev đã cài gcloud và "gcloud auth login").

.DESCRIPTION
  Mỗi bước kiểm tài nguyên đã có chưa rồi mới tạo, nên chạy lại sau khi hỏng giữa chừng vẫn đúng.
    1. Bật các API: Compute Engine, Artifact Registry, IAM, STS, IAP, OS Login, Cloud Storage.
    2. Kho ảnh Docker "dsvhvn" trên Artifact Registry (giữ 10 bản mới nhất mỗi ảnh, xóa bản cũ hơn 30 ngày).
    3. Địa chỉ IP tĩnh; tường lửa mở 80, 443, 8443 cho mọi nơi và 22 chỉ cho IAP; bỏ hai quy tắc mặc định mở SSH, RDP cho mọi nơi.
    4. Bucket giữ bản sao lưu CSDL hằng đêm 30 ngày.
    5. Tài khoản dịch vụ của máy chủ (kéo ảnh, ghi bản sao lưu, ghi nhật ký) và của GitHub Actions (đẩy ảnh, SSH qua IAP).
    6. Workload Identity Federation: CD của ba repo đăng nhập Google Cloud bằng token OIDC của GitHub, không cần khóa JSON.
    7. Máy ảo Ubuntu 24.04 (bảo vệ chống xóa), chép deploy\google-cloud\may-chu lên rồi chạy cai-dat-may-chu.sh.
    8. Ghi %APPDATA%\DSVHVN\may-chu\ha-tang.json; in biến repo cho CD (đặt luôn bằng gh khi có -DatBienGitHub).
  Thời gian dùng thử của Google Cloud chỉ 90 ngày: hết hạn thì máy ảo bị dừng; nâng lên tài khoản trả phí trước hạn để giữ máy.

.PARAMETER DuAn
  Mã dự án Google Cloud (project ID), dự án đã gắn tài khoản thanh toán (kể cả tài khoản dùng thử).
.PARAMETER Vung
  Vùng, mặc định asia-southeast1 (Singapore, gần Việt Nam).
.PARAMETER VungCon
  Vùng con của máy ảo, mặc định asia-southeast1-b.
.PARAMETER LoaiMay
  Loại máy ảo, mặc định e2-standard-2 (2 vCPU, 8 GB: SQL Server cần tối thiểu 2 GB).
.PARAMETER DungLuongDiaGb
  Dung lượng ổ đĩa, mặc định 40 GB.
.PARAMETER ChuRepo
  Chủ ba repo trên GitHub, mặc định Nho2708.
.PARAMETER DatBienGitHub
  Đặt biến repo bằng gh (cần gh đã đăng nhập, có quyền quản trị ba repo).
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DuAn,
    [string]$Vung = 'asia-southeast1',
    [string]$VungCon = 'asia-southeast1-b',
    [string]$TenMay = 'dsvhvn-may-chu',
    [string]$LoaiMay = 'e2-standard-2',
    [int]$DungLuongDiaGb = 40,
    [string]$ChuRepo = 'Nho2708',
    [switch]$DatBienGitHub
)

. (Join-Path $PSScriptRoot 'chung.ps1')

$repoBackend = 'BE_Cultural_Heritage-'
$repoFrontend = 'FE_Cultural_Heritage-'
$repoAi = 'AI_Cultural_Heritage-'
$baRepo = @($repoBackend, $repoFrontend, $repoAi)
$p = "--project=$DuAn"
$tenKho = 'dsvhvn'
$tenIp = 'dsvhvn-ip'
$theMay = 'dsvhvn-web'
$bucket = "$DuAn-dsvhvn-sao-luu"
$saMay = "dsvhvn-may-chu@$DuAn.iam.gserviceaccount.com"
$saTrienKhai = "dsvhvn-trien-khai@$DuAn.iam.gserviceaccount.com"
$tam = Join-Path ([System.IO.Path]::GetTempPath()) "dsvhvn-ha-tang-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $tam | Out-Null

function Write-TepTam([string]$ten, [string]$noiDung) {
    $duongDan = Join-Path $tam $ten
    [System.IO.File]::WriteAllText($duongDan, $noiDung, (New-Object System.Text.UTF8Encoding($false)))
    return $duongDan
}

# ---------------------------------------------------------------- 0. kiểm gcloud và dự án
Write-Buoc 'Kiểm gcloud và dự án'
if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) { throw 'Chưa cài Google Cloud CLI (gcloud).' }
$taiKhoan = (Invoke-Gcloud auth list '--filter=status:ACTIVE' '--format=value(account)') | Select-Object -First 1
if (-not $taiKhoan) { throw 'gcloud chưa đăng nhập: chạy "gcloud auth login".' }
$soDuAn = (Invoke-Gcloud projects describe $DuAn '--format=value(projectNumber)') | Select-Object -First 1
Write-Host "Dự án $DuAn (số $soDuAn), tài khoản gcloud đang dùng đã đăng nhập."

# ---------------------------------------------------------------- 1. API
Write-Buoc 'Bật các API cần dùng (lần đầu mất vài phút)'
Invoke-Gcloud services enable compute.googleapis.com artifactregistry.googleapis.com iam.googleapis.com iamcredentials.googleapis.com `
    sts.googleapis.com iap.googleapis.com oslogin.googleapis.com storage.googleapis.com $p | Out-Null
Write-Host 'Đã bật.'

# ---------------------------------------------------------------- 2. Artifact Registry
Write-Buoc "Kho ảnh Docker $tenKho ($Vung)"
if (-not (Test-Gcloud artifacts repositories describe $tenKho "--location=$Vung" $p)) {
    Invoke-Gcloud artifacts repositories create $tenKho '--repository-format=docker' "--location=$Vung" `
        '--description=DiViet images (api, ai, web)' $p | Out-Null
    Write-Host 'Đã tạo.'
} else { Write-Host 'Đã có.' }
$chinhSach = Write-TepTam 'don-dep-anh.json' @'
[
  { "name": "giu-10-ban-moi-nhat", "action": { "type": "Keep" }, "mostRecentVersions": { "keepCount": 10 } },
  { "name": "xoa-ban-cu-hon-30-ngay", "action": { "type": "Delete" }, "condition": { "tagState": "any", "olderThan": "30d" } }
]
'@
Invoke-Gcloud artifacts repositories set-cleanup-policies $tenKho "--location=$Vung" "--policy=$chinhSach" --no-dry-run $p | Out-Null
$khoAnh = "$Vung-docker.pkg.dev/$DuAn/$tenKho"
Write-Host "Kho ảnh: $khoAnh"

# ---------------------------------------------------------------- 3. mạng
Write-Buoc 'Địa chỉ IP tĩnh và tường lửa'
if (-not (Test-Gcloud compute addresses describe $tenIp "--region=$Vung" $p)) {
    Invoke-Gcloud compute addresses create $tenIp "--region=$Vung" $p | Out-Null
}
$ip = (Invoke-Gcloud compute addresses describe $tenIp "--region=$Vung" '--format=value(address)' $p) | Select-Object -First 1
Write-Host "IP tĩnh: $ip"

if (-not (Test-Gcloud compute networks describe default $p)) {
    Invoke-Gcloud compute networks create default '--subnet-mode=auto' $p | Out-Null
    Write-Host 'Đã tạo mạng default.'
}
$quyTac = @(
    @{ Ten = 'dsvhvn-web'; Luat = 'tcp:80,tcp:443,tcp:8443,udp:443,udp:8443'; Nguon = '0.0.0.0/0' },
    # Dải địa chỉ của Identity-Aware Proxy: SSH chỉ đi qua IAP (gcloud compute ssh --tunnel-through-iap).
    @{ Ten = 'dsvhvn-ssh-iap'; Luat = 'tcp:22'; Nguon = '35.235.240.0/20' }
)
foreach ($q in $quyTac) {
    if (-not (Test-Gcloud compute firewall-rules describe $q.Ten $p)) {
        Invoke-Gcloud compute firewall-rules create $q.Ten '--network=default' '--direction=INGRESS' '--action=ALLOW' "--rules=$($q.Luat)" `
            "--source-ranges=$($q.Nguon)" "--target-tags=$theMay" $p | Out-Null
        Write-Host "Đã tạo quy tắc $($q.Ten)."
    } else { Write-Host "Đã có quy tắc $($q.Ten)." }
}
foreach ($ten in @('default-allow-ssh', 'default-allow-rdp')) {
    if (Test-Gcloud compute firewall-rules describe $ten $p) {
        Invoke-Gcloud compute firewall-rules delete $ten $p | Out-Null
        Write-Host "Đã bỏ quy tắc mặc định $ten (mở cho mọi nơi)."
    }
}

# ---------------------------------------------------------------- 4. bucket sao lưu
Write-Buoc "Bucket sao lưu gs://$bucket"
if (-not (Test-Gcloud storage buckets describe "gs://$bucket" $p)) {
    Invoke-Gcloud storage buckets create "gs://$bucket" "--location=$Vung" '--uniform-bucket-level-access' '--public-access-prevention' $p | Out-Null
    Write-Host 'Đã tạo.'
} else { Write-Host 'Đã có.' }
$vongDoi = Write-TepTam 'vong-doi.json' '{ "rule": [ { "action": { "type": "Delete" }, "condition": { "age": 30 } } ] }'
Invoke-Gcloud storage buckets update "gs://$bucket" "--lifecycle-file=$vongDoi" $p | Out-Null

# ---------------------------------------------------------------- 5. tài khoản dịch vụ
Write-Buoc 'Tài khoản dịch vụ'
foreach ($sa in @(
        @{ Ten = 'dsvhvn-may-chu'; Email = $saMay; Hien = 'DiViet server' },
        @{ Ten = 'dsvhvn-trien-khai'; Email = $saTrienKhai; Hien = 'DiViet GitHub deploy' })) {
    if (-not (Test-Gcloud iam service-accounts describe $sa.Email $p)) {
        Invoke-Gcloud iam service-accounts create $sa.Ten "--display-name=$($sa.Hien)" $p | Out-Null
        Write-Host "Đã tạo $($sa.Email)."
        Start-Sleep -Seconds 10  # tài khoản mới cần vài giây mới gán quyền được
    } else { Write-Host "Đã có $($sa.Email)." }
}

Write-Host 'Gán quyền (gán lại quyền đã có không đổi gì):'
Invoke-Gcloud artifacts repositories add-iam-policy-binding $tenKho "--location=$Vung" "--member=serviceAccount:$saMay" `
    '--role=roles/artifactregistry.reader' $p | Out-Null
Invoke-Gcloud artifacts repositories add-iam-policy-binding $tenKho "--location=$Vung" "--member=serviceAccount:$saTrienKhai" `
    '--role=roles/artifactregistry.writer' $p | Out-Null
Invoke-Gcloud storage buckets add-iam-policy-binding "gs://$bucket" "--member=serviceAccount:$saMay" '--role=roles/storage.objectCreator' $p | Out-Null
foreach ($vaiTro in @('roles/logging.logWriter', 'roles/monitoring.metricWriter')) {
    Invoke-Gcloud projects add-iam-policy-binding $DuAn "--member=serviceAccount:$saMay" "--role=$vaiTro" '--condition=None' | Out-Null
}
foreach ($vaiTro in @('roles/compute.viewer', 'roles/iap.tunnelResourceAccessor')) {
    Invoke-Gcloud projects add-iam-policy-binding $DuAn "--member=serviceAccount:$saTrienKhai" "--role=$vaiTro" '--condition=None' | Out-Null
}
# Đăng nhập OS Login vào máy ảo chạy bằng tài khoản dịch vụ khác cần quyền "dùng" tài khoản đó.
Invoke-Gcloud iam service-accounts add-iam-policy-binding $saMay "--member=serviceAccount:$saTrienKhai" '--role=roles/iam.serviceAccountUser' $p | Out-Null
Write-Host '  máy chủ: đọc kho ảnh, ghi bucket sao lưu, ghi nhật ký và số đo'
Write-Host '  GitHub Actions: đẩy ảnh, xem máy ảo, đi qua IAP, dùng tài khoản của máy chủ'

# ---------------------------------------------------------------- 6. Workload Identity Federation
Write-Buoc 'Workload Identity Federation cho GitHub Actions'
if (-not (Test-Gcloud iam workload-identity-pools describe github '--location=global' $p)) {
    Invoke-Gcloud iam workload-identity-pools create github '--location=global' '--display-name=GitHub Actions' $p | Out-Null
    Write-Host 'Đã tạo pool github.'
}
if (-not (Test-Gcloud iam workload-identity-pools providers describe github '--location=global' '--workload-identity-pool=github' $p)) {
    Invoke-Gcloud iam workload-identity-pools providers create-oidc github '--location=global' '--workload-identity-pool=github' `
        '--display-name=GitHub' '--issuer-uri=https://token.actions.githubusercontent.com' `
        '--attribute-mapping=google.subject=assertion.sub,attribute.repository=assertion.repository,attribute.repository_owner=assertion.repository_owner,attribute.ref=assertion.ref' `
        "--attribute-condition=assertion.repository_owner=='$ChuRepo'" $p | Out-Null
    Write-Host "Đã tạo provider github (chỉ nhận token của repo thuộc $ChuRepo)."
}
foreach ($repo in $baRepo) {
    Invoke-Gcloud iam service-accounts add-iam-policy-binding $saTrienKhai '--role=roles/iam.workloadIdentityUser' `
        "--member=principalSet://iam.googleapis.com/projects/$soDuAn/locations/global/workloadIdentityPools/github/attribute.repository/$ChuRepo/$repo" $p | Out-Null
}
$wifProvider = "projects/$soDuAn/locations/global/workloadIdentityPools/github/providers/github"
Write-Host "Provider: $wifProvider (ba repo được dùng $saTrienKhai)"

# ---------------------------------------------------------------- 7. máy ảo
Write-Buoc "Máy ảo $TenMay ($LoaiMay, $VungCon)"
if (-not (Test-Gcloud compute instances describe $TenMay "--zone=$VungCon" $p)) {
    Invoke-Gcloud compute instances create $TenMay "--zone=$VungCon" "--machine-type=$LoaiMay" `
        '--image-family=ubuntu-2404-lts-amd64' '--image-project=ubuntu-os-cloud' `
        "--boot-disk-size=${DungLuongDiaGb}GB" '--boot-disk-type=pd-balanced' `
        "--address=$ip" "--tags=$theMay" "--service-account=$saMay" '--scopes=cloud-platform' `
        '--metadata=enable-oslogin=TRUE' '--shielded-secure-boot' '--shielded-vtpm' '--shielded-integrity-monitoring' `
        '--deletion-protection' $p | Out-Null
    Write-Host 'Đã tạo (bảo vệ chống xóa đang bật).'
} else { Write-Host 'Đã có.' }
Invoke-Gcloud compute instances add-iam-policy-binding $TenMay "--zone=$VungCon" "--member=serviceAccount:$saTrienKhai" `
    '--role=roles/compute.osAdminLogin' $p | Out-Null

$haTang = [ordered]@{
    DuAn = $DuAn; SoDuAn = $soDuAn; Vung = $Vung; VungCon = $VungCon; TenMay = $TenMay; DiaChiIp = $ip
    KhoAnh = $khoAnh; Bucket = $bucket; TaiKhoanMayChu = $saMay; TaiKhoanTrienKhai = $saTrienKhai; WifProvider = $wifProvider
    DiaChiHocTap = "https://$ip"; DiaChiQuanTri = "https://${ip}:8443"
}
New-Item -ItemType Directory -Force -Path $script:ThuMucMayChu | Out-Null
[System.IO.File]::WriteAllText($script:TepHaTang, ($haTang | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Đã ghi $($script:TepHaTang)."
$haTang = Read-HaTang

Write-Buoc 'Chờ SSH qua IAP sẵn sàng'
$sanSang = $false
for ($i = 1; $i -le 30; $i++) {
    if (Test-Gcloud compute ssh $TenMay "--zone=$VungCon" '--tunnel-through-iap' '--command=true' $p) { $sanSang = $true; break }
    Write-Host "  chưa được (lần $i), thử lại sau 10 giây..."
    Start-Sleep -Seconds 10
}
if (-not $sanSang) { throw 'Không SSH được vào máy ảo qua IAP sau 5 phút.' }

Write-Buoc 'Cài máy chủ (Docker, /opt/dsvhvn, lịch sao lưu)'
$banChep = New-BanChepMayChu
$dich = "/tmp/dsvhvn-ha-tang-$(Get-Date -Format 'yyyyMMddHHmmss')"
Copy-LenMayChu $haTang $banChep $dich
Invoke-MayChu $haTang ("sudo bash $dich/cai-dat-may-chu.sh --registry '$khoAnh' --dia-chi-hoc-tap '$ip' " +
    "--dia-chi-quan-tri '${ip}:8443' --bucket-sao-luu '$bucket'; r=`$?; rm -rf $dich; exit `$r")
Remove-Item -LiteralPath $banChep -Recurse -Force
Remove-Item -LiteralPath $tam -Recurse -Force

# ---------------------------------------------------------------- 8. biến repo
Write-Buoc 'Biến repo cho CD'
$bienChung = [ordered]@{
    GCP_DEPLOY = 'true'; GCP_PROJECT = $DuAn; GCP_ZONE = $VungCon; GCP_VM = $TenMay
    GCP_REGISTRY = $khoAnh; GCP_WIF_PROVIDER = $wifProvider; GCP_DEPLOY_SA = $saTrienKhai
}
$bienFrontend = [ordered]@{ GCP_LEARNING_WEB_URL = $haTang.DiaChiHocTap; GCP_ADMIN_WEB_URL = $haTang.DiaChiQuanTri }
foreach ($repo in $baRepo) {
    $bien = [ordered]@{}
    foreach ($k in $bienChung.Keys) { $bien[$k] = $bienChung[$k] }
    if ($repo -eq $repoFrontend) { foreach ($k in $bienFrontend.Keys) { $bien[$k] = $bienFrontend[$k] } }
    Write-Host "$ChuRepo/${repo}:"
    foreach ($k in $bien.Keys) {
        if ($DatBienGitHub) {
            & gh variable set $k --body $bien[$k] -R "$ChuRepo/$repo" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "gh variable set $k cho $repo lỗi." }
            Write-Host "  đã đặt $k = $($bien[$k])"
        } else {
            Write-Host "  $k = $($bien[$k])"
        }
    }
}

Write-Host ''
Write-Host 'Xong phần hạ tầng.' -ForegroundColor Green
Write-Host "  Web học tập: $($haTang.DiaChiHocTap)    Web quản trị: $($haTang.DiaChiQuanTri)"
Write-Host 'Bước tiếp theo: tao-cau-hinh-may-chu.ps1 -TaiLen, rồi chuyen-du-lieu.ps1, rồi chạy CD của ba repo.'
