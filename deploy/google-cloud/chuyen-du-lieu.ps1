<#
.SYNOPSIS
  Chép dữ liệu của máy demo sang máy chủ Google Cloud: bản sao lưu CSDL mới nhất và kho tệp tải lên (ảnh, mô hình 3D).

.DESCRIPTION
  Chỉ đọc tệp có sẵn trên máy demo, không chạm CSDL dsvhvn_demo: lấy bản sao lưu hằng đêm mới nhất ở C:\dsvhvn\sao-luu\hang-dem
  (hoặc tệp chỉ định), khôi phục thành CSDL dsvhvn trên máy chủ bằng khoi-phuc-csdl.sh (tạo luôn đăng nhập dsvhvn_app của API).
  Kho tệp nén thành .tar.gz rồi giải vào /opt/dsvhvn/du-lieu/storage (tệp trùng tên được ghi đè, tệp khác giữ nguyên).
  Chạy sau tao-cau-hinh-may-chu.ps1 -TaiLen và trước lần triển khai API đầu tiên.

.PARAMETER TepSaoLuu
  Tệp .bak cần khôi phục; mặc định bản mới nhất trong C:\dsvhvn\sao-luu\hang-dem.
.PARAMETER ThuMucKhoTep
  Kho tệp của máy demo, mặc định C:\dsvhvn\du-lieu\storage.
.PARAMETER GhiDe
  CSDL dsvhvn trên máy chủ đã có thì thay bằng bản sao lưu (script trên máy chủ sao lưu bản hiện tại trước).
.PARAMETER BoQuaKhoTep
  Chỉ chuyển CSDL.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$TepSaoLuu,
    [string]$ThuMucKhoTep = 'C:\dsvhvn\du-lieu\storage',
    [switch]$GhiDe,
    [switch]$BoQuaKhoTep
)

. (Join-Path $PSScriptRoot 'chung.ps1')
$haTang = Read-HaTang

if (-not $TepSaoLuu) {
    $moiNhat = Get-ChildItem -LiteralPath 'C:\dsvhvn\sao-luu\hang-dem' -Filter '*.bak' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $moiNhat) { throw 'Không thấy bản sao lưu nào trong C:\dsvhvn\sao-luu\hang-dem (chỉ định -TepSaoLuu).' }
    $TepSaoLuu = $moiNhat.FullName
}
$TepSaoLuu = (Resolve-Path -LiteralPath $TepSaoLuu).Path
$tepInfo = Get-Item -LiteralPath $TepSaoLuu
Write-Host ("Bản sao lưu: {0} ({1:0.0} MB, lúc {2:dd/MM/yyyy HH:mm})" -f $tepInfo.Name, ($tepInfo.Length / 1MB), $tepInfo.LastWriteTime)

$tam = Join-Path ([System.IO.Path]::GetTempPath()) "dsvhvn-chuyen-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $tam | Out-Null
try {
    # ------------------------------------------------------------ CSDL
    Write-Buoc 'Chép bản sao lưu lên máy chủ và khôi phục'
    $tenBak = $tepInfo.Name
    Copy-Item -LiteralPath $TepSaoLuu -Destination (Join-Path $tam $tenBak)
    Copy-LenMayChu $haTang (Join-Path $tam $tenBak) $tenBak
    $ghiDe = if ($GhiDe) { ' --ghi-de' } else { '' }
    Invoke-MayChu $haTang ("sudo install -o 10001 -g 0 -m 640 `$HOME/$tenBak /opt/dsvhvn/sao-luu/nhap/$tenBak && rm -f `$HOME/$tenBak && " +
        "sudo /opt/dsvhvn/bin/khoi-phuc-csdl.sh /opt/dsvhvn/sao-luu/nhap/$tenBak$ghiDe")

    # ------------------------------------------------------------ kho tệp
    if (-not $BoQuaKhoTep) {
        Write-Buoc "Kho tệp $ThuMucKhoTep"
        if (-not (Test-Path -LiteralPath $ThuMucKhoTep)) { throw "Không thấy $ThuMucKhoTep." }
        $tep = @(Get-ChildItem -LiteralPath $ThuMucKhoTep -Recurse -File)
        Write-Host ("{0} tệp, {1:0.0} MB" -f $tep.Count, (($tep | Measure-Object Length -Sum).Sum / 1MB))
        $nen = Join-Path $tam 'storage.tar.gz'
        & tar -czf $nen -C $ThuMucKhoTep .
        if ($LASTEXITCODE -ne 0) { throw 'Nén kho tệp lỗi.' }
        $global:LASTEXITCODE = 0
        Copy-LenMayChu $haTang $nen 'dsvhvn-storage.tar.gz'
        # Lệnh không chứa dấu nháy kép: gcloud trên Windows là tệp .cmd, tham số có nháy kép bị tách sai.
        Invoke-MayChu $haTang ('sudo tar -xzf $HOME/dsvhvn-storage.tar.gz -C /opt/dsvhvn/du-lieu/storage --no-same-owner && ' +
            'sudo chown -R 1654:1654 /opt/dsvhvn/du-lieu/storage && rm -f $HOME/dsvhvn-storage.tar.gz && ' +
            'sudo du -sh /opt/dsvhvn/du-lieu/storage')
    }
} finally {
    Remove-Item -LiteralPath $tam -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Đã chuyển dữ liệu. Bước tiếp theo: chạy CD của backend, Dịch vụ AI và frontend (chọn nơi triển khai google-cloud).' -ForegroundColor Green
