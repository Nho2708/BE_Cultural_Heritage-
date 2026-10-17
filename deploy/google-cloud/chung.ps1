# Hàm dùng chung cho các script chạy trên máy dev (nạp bằng: . "$PSScriptRoot\chung.ps1").
# Thông tin hạ tầng do tao-ha-tang.ps1 ghi ở %APPDATA%\DSVHVN\may-chu\ha-tang.json (không có bí mật); cấu hình bí mật của
# máy chủ cũng nằm trong thư mục đó, không bao giờ vào repo.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$script:ThuMucMayChu = Join-Path $env:APPDATA 'DSVHVN\may-chu'
$script:TepHaTang = Join-Path $script:ThuMucMayChu 'ha-tang.json'

function Write-Buoc([string]$noiDung) {
    Write-Host ''
    Write-Host "== $noiDung" -ForegroundColor Cyan
}

# gcloud không hỏi lại (--quiet); lỗi thì dừng kèm thông báo của gcloud. Trả các dòng của đầu ra chuẩn.
# Hàm đơn giản dùng $args để mọi tham số dạng --ten=gia-tri đi thẳng vào gcloud.
function Invoke-Gcloud {
    $kq = & gcloud @args --quiet 2>&1
    $ma = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($ma -ne 0) {
        throw ("gcloud {0} lỗi (mã {1}):`n{2}" -f ($args -join ' '), $ma, (($kq | ForEach-Object { "$_" }) -join "`n"))
    }
    return @($kq | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
}

# Trả $true khi lệnh gcloud thành công (dùng để hỏi tài nguyên đã có chưa).
function Test-Gcloud {
    & gcloud @args --quiet *> $null
    $ok = ($LASTEXITCODE -eq 0)
    $global:LASTEXITCODE = 0
    return $ok
}

function Read-HaTang {
    if (-not (Test-Path -LiteralPath $script:TepHaTang)) { throw "Chưa có $($script:TepHaTang). Chạy tao-ha-tang.ps1 trước." }
    return Get-Content -LiteralPath $script:TepHaTang -Raw -Encoding UTF8 | ConvertFrom-Json
}

# Chạy lệnh trên máy chủ qua SSH (đi qua IAP, không cần mở cổng 22 ra Internet); đầu ra hiện thẳng ra màn hình.
# Lệnh không được chứa dấu nháy kép: gcloud trên Windows là tệp .cmd, tham số có nháy kép bị tách sai (dùng nháy đơn).
function Invoke-MayChu($haTang, [string]$lenh) {
    if ($lenh.Contains('"')) { throw 'Lệnh chạy trên máy chủ không được chứa dấu nháy kép.' }
    & gcloud compute ssh $haTang.TenMay "--zone=$($haTang.VungCon)" "--project=$($haTang.DuAn)" --tunnel-through-iap --quiet "--command=$lenh"
    $ma = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($ma -ne 0) { throw "Lệnh trên máy chủ lỗi (mã $ma)." }
}

# Chép tệp hoặc thư mục lên máy chủ. Đích là đường dẫn chưa tồn tại: tuyệt đối, hoặc tương đối tính từ thư mục nhà.
# Thư mục được nén thành một tệp rồi giải nén trên máy chủ: pscp mà gcloud dùng trên Windows không chép thư mục vào đường dẫn
# chưa có (báo "unable to open").
function Copy-LenMayChu($haTang, [string]$nguon, [string]$dich) {
    $thamSo = @('compute', 'scp', "--zone=$($haTang.VungCon)", "--project=$($haTang.DuAn)", '--tunnel-through-iap', '--quiet')
    if (-not (Test-Path -LiteralPath $nguon -PathType Container)) {
        & gcloud @thamSo $nguon "$($haTang.TenMay):$dich"
        $ma = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        if ($ma -ne 0) { throw "Chép $nguon lên máy chủ lỗi (mã $ma)." }
        return
    }
    $tenNen = 'dsvhvn-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tar.gz'
    $nen = Join-Path ([System.IO.Path]::GetTempPath()) $tenNen
    try {
        & tar -czf $nen -C $nguon .
        $ma = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        if ($ma -ne 0) { throw "Nén $nguon lỗi (mã $ma)." }
        Copy-LenMayChu $haTang $nen $tenNen
        Invoke-MayChu $haTang ("mkdir -p $dich && tar -xzf `$HOME/$tenNen -C $dich --warning=no-unknown-keyword " +
            "&& rm -f `$HOME/$tenNen")
    } finally {
        Remove-Item -LiteralPath $nen -Force -ErrorAction SilentlyContinue
    }
}

# Chép thư mục deploy\google-cloud\may-chu sang thư mục tạm, đổi CRLF thành LF (bash trên máy chủ không chạy được CRLF).
function New-BanChepMayChu {
    $tam = Join-Path ([System.IO.Path]::GetTempPath()) ("dsvhvn-may-chu-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'may-chu') -Destination $tam -Recurse
    Get-ChildItem -LiteralPath $tam -Recurse -File | ForEach-Object {
        $noiDung = [System.IO.File]::ReadAllText($_.FullName)
        if ($noiDung.Contains("`r")) {
            [System.IO.File]::WriteAllText($_.FullName, $noiDung.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))
        }
    }
    return $tam
}
