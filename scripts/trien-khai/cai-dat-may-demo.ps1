<#
.SYNOPSIS
  Cài đặt máy demo QUANG-NHO một lần: thư mục, quyền, IIS, tường lửa, runner GitHub, đường hầm ngrok.

.DESCRIPTION
  Chạy bằng PowerShell 7 mở bằng "Run as Administrator". Chạy lại nhiều lần vẫn đúng (mỗi bước kiểm rồi mới làm).
  Backend và frontend dùng chung script này (frontend gọi lại bản trong repo backend hoặc bản chép của nó).

.PARAMETER BoQuaRunner
  Không cài runner GitHub.
.PARAMETER BoQuaNgrok
  Không cài dịch vụ ngrok.
.PARAMETER NgrokExe
  Đường dẫn ngrok.exe sẵn có để chép vào C:\dsvhvn\cong-cu\.
.PARAMETER ThuMucStorageDev
  Thư mục tệp tải lên của máy dev, chép sang du-lieu\storage nếu đích còn trống.
#>
#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$BoQuaRunner,
    [switch]$BoQuaNgrok,
    [string]$NgrokExe = 'C:\Users\levono\Downloads\ngrok-v3-stable-windows-amd64\ngrok.exe',
    [string]$ThuMucStorageDev
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$goc = 'C:\dsvhvn'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $ThuMucStorageDev) { $ThuMucStorageDev = Join-Path $repoRoot 'src\DSVHVN.Api\storage' }

$tenPoolApi = 'DSVHVN API'
$tenPoolWeb = 'DSVHVN Web'
$cacSite = @(
    @{ Ten = 'DSVHVN API';           Cong = 8080; Thu = "$goc\api";          Pool = $tenPoolApi },
    @{ Ten = 'DSVHVN Web quan tri';  Cong = 8081; Thu = "$goc\web-quan-tri"; Pool = $tenPoolWeb },
    @{ Ten = 'DSVHVN Web hoc tap';   Cong = 8082; Thu = "$goc\web-hoc-tap";  Pool = $tenPoolWeb }
)
$diaChiNgrok = 'https://maynard-unphysical-planographically.ngrok-free.dev'
$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
$nguoiHienTai = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$trangThai = [ordered]@{}
$canhBao = New-Object System.Collections.Generic.List[string]

function Write-Buoc([string]$noiDung) {
    Write-Host ''
    Write-Host "== $noiDung" -ForegroundColor Cyan
}

function Invoke-Native([string]$tep, [string[]]$thamSo, [switch]$ChoPhepLoi) {
    $kq = & $tep @thamSo 2>&1
    $ma = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($ma -ne 0 -and -not $ChoPhepLoi) { throw "$([System.IO.Path]::GetFileName($tep)) $($thamSo -join ' ') lỗi (mã $ma): $($kq -join ' ')" }
    return [pscustomobject]@{ Ma = $ma; DauRa = ($kq -join "`n") }
}

function Grant([string]$duongDan, [string]$nguoi, [string]$quyen, [switch]$KhongKeThua) {
    # quyen: F, M, RX, R. Thư mục thì kế thừa xuống con.
    $dich = if ($KhongKeThua) { $quyen } else { "(OI)(CI)$quyen" }
    [void](Invoke-Native icacls @($duongDan, '/grant', "${nguoi}:${dich}"))
}

# ---------------------------------------------------------------- kiểm tiền điều kiện
Write-Buoc 'Kiểm điều kiện của máy'
if (-not (Test-Path $appcmd)) { throw 'Chưa bật IIS (không thấy appcmd.exe).' }
if (-not (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) { $canhBao.Add('Thiếu ASP.NET Core Module V2 (cài .NET Hosting Bundle).') }
if (-not (Test-Path "$env:windir\System32\inetsrv\rewrite.dll")) { $canhBao.Add('Thiếu IIS URL Rewrite.') }
if (-not (Test-Path "$env:ProgramFiles\IIS\Application Request Routing\requestRouter.dll")) { $canhBao.Add('Thiếu IIS Application Request Routing (ARR).') }
$runtime = (& dotnet --list-runtimes 2>$null) -match '^Microsoft\.AspNetCore\.App 9\.'
if (-not $runtime) { $canhBao.Add('Chưa có runtime ASP.NET Core 9 trên máy.') }
$duongDanMay = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($duongDanMay -notmatch 'Tools\\Binn') { $canhBao.Add('PATH cấp máy không có thư mục sqlcmd; runner (chạy bằng NETWORK SERVICE) sẽ không gọi được sqlcmd.') }
foreach ($c in $canhBao) { Write-Warning $c }

# ---------------------------------------------------------------- 1. cây thư mục
Write-Buoc 'Tạo cây thư mục'
$cacThuMuc = @('api', 'api-truoc', 'web-quan-tri', 'web-quan-tri-truoc', 'web-hoc-tap', 'web-hoc-tap-truoc',
    'cau-hinh', 'du-lieu\storage', 'sao-luu', 'cong-cu', 'nhat-ky')
foreach ($t in $cacThuMuc) { New-Item -ItemType Directory -Force -Path (Join-Path $goc $t) | Out-Null }
Write-Host "Đã có cây thư mục dưới $goc"

# ---------------------------------------------------------------- 2. chép tệp tải lên của máy dev
Write-Buoc 'Chép tệp tải lên từ máy dev'
$dichStorage = Join-Path $goc 'du-lieu\storage'
if (@(Get-ChildItem -LiteralPath $dichStorage -Force -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-Host 'du-lieu\storage đã có tệp, giữ nguyên.'
} elseif (Test-Path -LiteralPath $ThuMucStorageDev) {
    Copy-Item -Path (Join-Path $ThuMucStorageDev '*') -Destination $dichStorage -Recurse -Force
    Write-Host "Đã chép từ $ThuMucStorageDev"
} else {
    Write-Host "Không thấy $ThuMucStorageDev, bỏ qua."
}

# ---------------------------------------------------------------- 3. ARR
Write-Buoc 'Bật proxy ARR ở cấp máy chủ'
[void](Invoke-Native $appcmd @('set', 'config', '-section:system.webServer/proxy', '/enabled:true', '/commit:apphost'))
Write-Host 'Đã bật.'

# ---------------------------------------------------------------- 4. IIS
Write-Buoc 'Tạo hoặc cập nhật app pool và site IIS'
function Test-IisCoTen([string]$loai, [string]$ten) {
    $r = Invoke-Native $appcmd @('list', $loai, $ten, '/text:name') -ChoPhepLoi
    return ($r.Ma -eq 0 -and $r.DauRa.Trim().Length -gt 0)
}

foreach ($pool in @($tenPoolApi, $tenPoolWeb)) {
    if (-not (Test-IisCoTen 'apppool' $pool)) {
        [void](Invoke-Native $appcmd @('add', 'apppool', "/name:$pool"))
        Write-Host "Đã tạo app pool '$pool'."
    }
}
# Không dùng .NET (No Managed Code); riêng API luôn chạy vì có tác vụ nền, không tự tắt khi rảnh.
[void](Invoke-Native $appcmd @('set', 'apppool', $tenPoolApi, '/managedRuntimeVersion:', '/startMode:AlwaysRunning',
    '/processModel.idleTimeout:00:00:00', '/autoStart:true'))
[void](Invoke-Native $appcmd @('set', 'apppool', $tenPoolWeb, '/managedRuntimeVersion:', '/autoStart:true'))
Write-Host 'App pool: DSVHVN API (AlwaysRunning, idleTimeout 0), DSVHVN Web.'

foreach ($s in $cacSite) {
    $binding = "http/*:$($s.Cong):"
    if (-not @(Get-ChildItem -LiteralPath $s.Thu -Force -ErrorAction SilentlyContinue).Count) {
        $trang = '<!DOCTYPE html><html lang="vi"><head><meta charset="utf-8"><title>DSVHVN</title></head><body><p>Chưa triển khai bản nào.</p></body></html>'
        [System.IO.File]::WriteAllText((Join-Path $s.Thu 'index.html'), $trang, (New-Object System.Text.UTF8Encoding($false)))
    }
    if (-not (Test-IisCoTen 'site' $s.Ten)) {
        [void](Invoke-Native $appcmd @('add', 'site', "/name:$($s.Ten)", "/bindings:$binding", "/physicalPath:$($s.Thu)"))
        Write-Host "Đã tạo site '$($s.Ten)' cổng $($s.Cong)."
    } else {
        [void](Invoke-Native $appcmd @('set', 'site', $s.Ten, "/bindings:$binding"))
        [void](Invoke-Native $appcmd @('set', 'vdir', "$($s.Ten)/", "/physicalPath:$($s.Thu)"))
        Write-Host "Đã cập nhật site '$($s.Ten)' cổng $($s.Cong)."
    }
    [void](Invoke-Native $appcmd @('set', 'app', "$($s.Ten)/", "/applicationPool:$($s.Pool)"))
    if ($s.Pool -eq $tenPoolApi) {
        [void](Invoke-Native $appcmd @('set', 'app', "$($s.Ten)/", '/preloadEnabled:true'))
    }
    [void](Invoke-Native $appcmd @('start', 'site', $s.Ten) -ChoPhepLoi)
}

# ---------------------------------------------------------------- 5. quyền thư mục
Write-Buoc 'Đặt quyền thư mục'
function Lay-TenTaiKhoan([System.Security.Principal.WellKnownSidType]$loai) {
    # Tên tài khoản dựng sẵn thay đổi theo ngôn ngữ Windows nên dịch từ loại tài khoản.
    ([System.Security.Principal.SecurityIdentifier]::new($loai, $null)).Translate([System.Security.Principal.NTAccount]).Value
}
$networkService = Lay-TenTaiKhoan NetworkServiceSid
$quanTri = Lay-TenTaiKhoan BuiltinAdministratorsSid
$system = Lay-TenTaiKhoan LocalSystemSid
$identityApi = "IIS AppPool\$tenPoolApi"

Grant $goc $networkService 'M'
Grant (Join-Path $goc 'api') $identityApi 'RX'
Grant (Join-Path $goc 'du-lieu\storage') $identityApi 'M'
Grant (Join-Path $goc 'nhat-ky') $identityApi 'M'

# Dịch vụ SQL Server ghi tệp .bak vào sao-luu: lấy tài khoản chạy dịch vụ thật của PRN222.
$dvSql = Get-CimInstance Win32_Service -Filter "Name='MSSQL`$PRN222'" -ErrorAction SilentlyContinue
if ($dvSql) {
    $taiKhoanSql = $dvSql.StartName
    Grant (Join-Path $goc 'sao-luu') $taiKhoanSql 'M'
    Write-Host "SQL Server PRN222 chạy bằng '$taiKhoanSql': cấp ghi cho sao-luu."
} else {
    Write-Warning 'Không thấy dịch vụ MSSQL$PRN222; tự cấp quyền ghi cho dịch vụ SQL Server trên thư mục sao-luu.'
}

# cau-hinh chứa bí mật: bỏ kế thừa, chỉ các tài khoản cần thiết.
$cauHinhDir = Join-Path $goc 'cau-hinh'
[void](Invoke-Native icacls @($cauHinhDir, '/inheritance:r'))
Grant $cauHinhDir $quanTri 'F'
Grant $cauHinhDir $system 'F'
Grant $cauHinhDir $nguoiHienTai 'M'
Grant $cauHinhDir $networkService 'R'
Grant $cauHinhDir $identityApi 'R'
Write-Host 'Đã đặt quyền (cau-hinh chỉ cho quản trị, SYSTEM, người dùng hiện tại, NETWORK SERVICE đọc, app pool API đọc).'

# ---------------------------------------------------------------- 6. tường lửa
Write-Buoc 'Mở cổng 8080-8082 cho hồ sơ mạng Private'
$tenLuat = 'DSVHVN demo'
if (Get-NetFirewallRule -DisplayName $tenLuat -ErrorAction SilentlyContinue) {
    Set-NetFirewallRule -DisplayName $tenLuat -Enabled True -Direction Inbound -Action Allow -Profile Private -Protocol TCP -LocalPort '8080-8082'
} else {
    New-NetFirewallRule -DisplayName $tenLuat -Direction Inbound -Protocol TCP -LocalPort '8080-8082' -Action Allow -Profile Private | Out-Null
}
Write-Host "Luật '$tenLuat' sẵn sàng."

# ---------------------------------------------------------------- 7. runner GitHub
function Install-Runner([string]$ten, [string]$repo, [string]$thuMuc, [string]$zip) {
    if (Test-Path (Join-Path $thuMuc '.runner')) {
        Write-Host "Runner $ten đã cấu hình, bỏ qua."
        return
    }
    New-Item -ItemType Directory -Force -Path $thuMuc | Out-Null
    if (-not (Test-Path (Join-Path $thuMuc 'config.cmd'))) {
        Expand-Archive -LiteralPath $zip -DestinationPath $thuMuc -Force
    }
    Grant $thuMuc $networkService 'M'
    $token = (& gh api -X POST "repos/$repo/actions/runners/registration-token" --jq .token)
    if ($LASTEXITCODE -ne 0 -or -not $token) { throw "Không lấy được mã đăng ký runner cho $repo (kiểm tra 'gh auth status')." }
    Push-Location $thuMuc
    try {
        & .\config.cmd --unattended --url "https://github.com/$repo" --token $token --name $ten --labels may-demo `
            --work _work --runasservice --windowslogonaccount 'NT AUTHORITY\NETWORK SERVICE' --replace | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "config.cmd của runner $ten lỗi (mã $LASTEXITCODE)." }
    } finally { Pop-Location }
    Write-Host "Đã đăng ký runner $ten cho $repo và chạy làm dịch vụ."
}

Write-Buoc 'Cài runner GitHub tự host'
if ($BoQuaRunner) {
    Write-Host 'Bỏ qua (-BoQuaRunner).'
} else {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Không thấy gh (GitHub CLI) trong PATH.' }
    $cacRunner = @(
        @{ Ten = 'may-demo-api'; Repo = 'Nho2708/BE_Cultural_Heritage-'; Thu = 'C:\actions-runner\api' },
        @{ Ten = 'may-demo-web'; Repo = 'Nho2708/FE_Cultural_Heritage-'; Thu = 'C:\actions-runner\web' }
    )
    $canTai = @($cacRunner | Where-Object { -not (Test-Path (Join-Path $_.Thu '.runner')) -and -not (Test-Path (Join-Path $_.Thu 'config.cmd')) })
    $zip = $null
    if ($canTai.Count -gt 0) {
        $thuTai = Join-Path ([System.IO.Path]::GetTempPath()) ('runner-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $thuTai | Out-Null
        $ban = (& gh release view --repo actions/runner --json tagName,body,assets) | ConvertFrom-Json
        $tai = $ban.assets | Where-Object { $_.name -match '^actions-runner-win-x64-\d+\.\d+\.\d+\.zip$' } | Select-Object -First 1
        if (-not $tai) { throw 'Không tìm thấy gói actions-runner win-x64 trong bản phát hành mới nhất.' }
        Write-Host "Tải $($tai.name) ($($ban.tagName))..."
        & gh release download $ban.tagName --repo actions/runner --pattern $tai.name --dir $thuTai
        if ($LASTEXITCODE -ne 0) { throw 'Tải runner lỗi.' }
        $zip = Join-Path $thuTai $tai.name
        if ($ban.body -match 'BEGIN SHA win-x64 -->\s*([0-9a-fA-F]{64})\s*<!--') {
            $mong = $Matches[1].ToLowerInvariant()
            $thuc = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($mong -ne $thuc) { throw "SHA-256 của gói runner không khớp (mong $mong, thực tế $thuc)." }
            Write-Host 'SHA-256 khớp với ghi chú phát hành.'
        } else {
            Write-Warning 'Ghi chú phát hành không có SHA-256 của win-x64, không kiểm được.'
        }
    }
    try {
        foreach ($r in $cacRunner) { Install-Runner $r.Ten $r.Repo $r.Thu $zip }
    } finally {
        if ($zip) { Remove-Item -LiteralPath (Split-Path -Parent $zip) -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- 8. ngrok
Write-Buoc 'Cài đường hầm ngrok làm dịch vụ Windows'
if ($BoQuaNgrok) {
    Write-Host 'Bỏ qua (-BoQuaNgrok).'
} else {
    $dichNgrok = Join-Path $goc 'cong-cu\ngrok.exe'
    if (-not (Test-Path $dichNgrok)) {
        if (-not (Test-Path -LiteralPath $NgrokExe)) { throw "Không thấy ngrok.exe tại $NgrokExe (truyền -NgrokExe)." }
        Copy-Item -LiteralPath $NgrokExe -Destination $dichNgrok
    }
    $tepNguoiDung = Join-Path $env:LOCALAPPDATA 'ngrok\ngrok.yml'
    if (-not (Test-Path -LiteralPath $tepNguoiDung)) { throw "Không thấy $tepNguoiDung để lấy authtoken." }
    $yml = Get-Content -LiteralPath $tepNguoiDung -Raw
    if ($yml -notmatch '(?m)^\s*authtoken:\s*["'']?([^\s"'']+)') { throw 'Không đọc được authtoken trong cấu hình ngrok của người dùng.' }
    $authtoken = $Matches[1]

    $tepNgrok = Join-Path $goc 'cau-hinh\ngrok.yml'
    $noiDung = @"
version: "3"
agent:
  authtoken: $authtoken
endpoints:
  - name: dsvhvn
    url: $diaChiNgrok
    upstream:
      url: 8082
"@
    [System.IO.File]::WriteAllText($tepNgrok, $noiDung, (New-Object System.Text.UTF8Encoding($false)))
    [void](Invoke-Native $dichNgrok @('config', 'check', '--config', $tepNgrok))

    if (Get-Process ngrok -ErrorAction SilentlyContinue) {
        Write-Warning 'Đang có tiến trình ngrok chạy tay. Tên miền cố định chỉ nhận một phiên: tắt tiến trình đó (ví dụ "ngrok http 5080") rồi chạy lại script.'
    }
    # Đã cài dịch vụ thì gỡ rồi cài lại để nhận cấu hình mới.
    $dvNgrok = Get-Service -Name 'ngrok' -ErrorAction SilentlyContinue
    if ($dvNgrok) {
        [void](Invoke-Native $dichNgrok @('service', 'stop') -ChoPhepLoi)
        [void](Invoke-Native $dichNgrok @('service', 'uninstall') -ChoPhepLoi)
    }
    [void](Invoke-Native $dichNgrok @('service', 'install', '--config', $tepNgrok))
    [void](Invoke-Native $dichNgrok @('service', 'start'))
    Write-Host "Dịch vụ ngrok đã cài và chạy: $diaChiNgrok -> cổng 8082."
    Write-Host 'Khi dịch vụ chạy thì KHÔNG chạy "ngrok http 5080" thủ công cùng lúc (tên miền chỉ một phiên).' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 9. bảng trạng thái
Write-Buoc 'Trạng thái'
$bang = @(foreach ($s in $cacSite) {
    $tt = Invoke-Native $appcmd @('list', 'site', $s.Ten, '/text:state') -ChoPhepLoi
    [pscustomobject]@{ Thanhphan = "Site $($s.Ten)"; Cong = $s.Cong; TrangThai = $tt.DauRa.Trim() }
})
$bang += foreach ($dv in @(Get-Service -Name 'actions.runner.*' -ErrorAction SilentlyContinue)) {
    [pscustomobject]@{ Thanhphan = "Runner $($dv.Name)"; Cong = ''; TrangThai = [string]$dv.Status }
}
if (-not $BoQuaNgrok) {
    $dv = Get-Service -Name 'ngrok' -ErrorAction SilentlyContinue
    $bang += [pscustomobject]@{ Thanhphan = 'Dịch vụ ngrok'; Cong = ''; TrangThai = $(if ($dv) { [string]$dv.Status } else { 'chưa cài' }) }
}
$bang | Format-Table -AutoSize

Write-Host 'Việc tiếp theo:' -ForegroundColor Cyan
Write-Host '  1. Chạy (không cần quản trị):  pwsh scripts\trien-khai\tao-cau-hinh-may-demo.ps1'
Write-Host '  2. Đẩy mã lên main (hoặc chạy lại workflow CD) để runner triển khai API và hai web.'
Write-Host '  3. Mở http://localhost:8080/health và (nếu ngrok chạy) ' -NoNewline
Write-Host $diaChiNgrok
if ($canhBao.Count -gt 0) {
    Write-Host ''
    Write-Host 'Cảnh báo cần xử lý:' -ForegroundColor Yellow
    $canhBao | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}
