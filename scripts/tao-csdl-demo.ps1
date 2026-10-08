# Tạo cơ sở dữ liệu thật trên SQL Server LocalDB (đủ mọi migration) và nạp dữ liệu demo.
#
# Dùng:
#   pwsh -File scripts/tao-csdl-demo.ps1                                  # CSDL sep490_dsvhvn, cổng 5180, hỏi mật khẩu quản trị
#   pwsh -File scripts/tao-csdl-demo.ps1 -Database dsvhvn_tam_thu -Port 5190
#
# Script làm lần lượt: kiểm LocalDB, Python và .NET; dựng backend; chạy backend ở môi trường Development trên CSDL đích (backend tự tạo CSDL,
# áp migration, nạp 34 tỉnh, 20 di sản, gói dịch vụ và 4 quản trị hệ thống); chạy scripts/du-lieu-demo/nap-du-lieu-demo.py để nạp dữ liệu demo qua API;
# tắt backend; in bảng tài khoản demo. Email luôn ở chế độ Log (không gửi thư thật), nơi lưu tệp là Local, AI là giả lập.
#
# -AdminPassword là mật khẩu chung đặt cho 4 quản trị hệ thống khi CSDL còn mới (8-64 ký tự, có chữ hoa, chữ thường và chữ số). Không truyền
# thì script hỏi. Mật khẩu chỉ đi vào backend bằng biến môi trường của tiến trình con, không ghi ra tệp hay log.
# CSDL đã có quản trị rồi thì mật khẩu phải khớp với mật khẩu quản trị đang dùng (nếu không script dừng ở bước đăng nhập).
# CSDL đã có dữ liệu demo (trường demo đã tồn tại) thì script báo và thoát, không nạp trùng.
[CmdletBinding()]
param(
    [string] $Database = 'sep490_dsvhvn',
    [int] $Port = 5180,
    [securestring] $AdminPassword
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$env:PYTHONIOENCODING = 'utf-8'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$seedScript = Join-Path $PSScriptRoot 'du-lieu-demo\nap-du-lieu-demo.py'
$work = Join-Path $env:TEMP ('dsvhvn-demo-' + (Get-Date -Format 'yyyyMMddHHmmss'))
$localDb = '(localdb)\MSSQLLocalDB'
$demoPassword = 'DemoDsvhvn2026'
$adminLogin = 'admin.nho'

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Wait-Until([scriptblock] $test, [int] $seconds, [string] $what) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        try { if (& $test) { return } } catch { }
        Start-Sleep -Seconds 2
    }
    throw "Hết thời gian chờ: $what"
}

function Invoke-Sql([string] $query) {
    $out = & sqlcmd -S $localDb -d master -h -1 -W -Q "SET NOCOUNT ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd lỗi: $out" }
    return ($out | Out-String).Trim()
}

# ---- Kiểm môi trường ----
Step 'Kiểm LocalDB, Python và .NET'
if ($Database -notmatch '^[A-Za-z0-9_]{1,100}$') { throw "Tên CSDL '$Database' không hợp lệ (chỉ chữ không dấu, số, gạch dưới)." }
foreach ($tool in 'dotnet', 'python', 'sqlcmd') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Không thấy lệnh '$tool' trong PATH." }
}
& sqllocaldb start MSSQLLocalDB 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Không bật được SQL Server LocalDB (MSSQLLocalDB). Cài LocalDB (đi kèm SQL Server Express hoặc Visual Studio) rồi chạy lại.' }
Invoke-Sql 'SELECT 1' | Out-Null
if (-not (Test-Path $seedScript)) { throw "Không thấy $seedScript" }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Cổng $Port đang bị dùng: chọn cổng khác bằng -Port." }

$demoCount = Invoke-Sql "IF DB_ID(N'$Database') IS NOT NULL AND OBJECT_ID(N'[$Database].dbo.organizations') IS NOT NULL EXEC(N'SELECT COUNT(*) FROM [$Database].dbo.organizations WHERE name LIKE N''%Demo Di%''') ELSE SELECT 0"
if ([int]($demoCount -split "`n")[-1] -gt 0) {
    Write-Host "CSDL $Database đã có dữ liệu demo (trường demo đã tồn tại). Không nạp trùng; muốn nạp lại thì xoá CSDL rồi chạy lại." -ForegroundColor Yellow
    exit 0
}

if (-not $AdminPassword) { $AdminPassword = Read-Host -AsSecureString 'Mật khẩu cho 4 quản trị hệ thống (8-64 ký tự, có chữ hoa, chữ thường, chữ số)' }
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($AdminPassword)
try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
if ($plain.Length -lt 8 -or $plain.Length -gt 64 -or $plain -cnotmatch '[A-Z]' -or $plain -cnotmatch '[a-z]' -or $plain -notmatch '[0-9]') {
    throw 'Mật khẩu quản trị phải dài 8-64 ký tự, có chữ hoa, chữ thường và chữ số.'
}

New-Item -ItemType Directory -Force $work | Out-Null
$beOut = Join-Path $work 'backend.out'
$beErr = Join-Path $work 'backend.err'
$resultFile = Join-Path $work 'du-lieu-demo.json'
$backend = $null
$envNames = 'ConnectionStrings__Default', 'ASPNETCORE_ENVIRONMENT', 'Database__InitMode', 'Email__Mode', 'Ai__Mode', 'Storage__Provider', 'Seed__AdminPassword'
$exit = 0

try {
    Step 'Dựng backend'
    & dotnet build (Join-Path $repo 'src\DSVHVN.Api') -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build thất bại.' }

    Step "Chạy backend trên CSDL $Database (cổng $Port, email chế độ Log)"
    $env:ConnectionStrings__Default = "Server=$localDb;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Database__InitMode = 'Migrate'
    $env:Email__Mode = 'Log'          # bắt buộc: không gửi thư thật dù User Secrets đang cấu hình SMTP
    $env:Ai__Mode = 'Simulated'
    $env:Storage__Provider = 'Local'
    $env:Seed__AdminPassword = $plain
    $backend = Start-Process -FilePath 'dotnet' -ArgumentList "run --project src/DSVHVN.Api --no-build --no-launch-profile --urls http://localhost:$Port" `
        -WorkingDirectory $repo -RedirectStandardOutput $beOut -RedirectStandardError $beErr -WindowStyle Hidden -PassThru
    foreach ($name in $envNames) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    Wait-Until {
        if ($backend.HasExited) { throw 'Backend thoát sớm, xem log: ' + $beOut + ' và ' + $beErr }
        (Invoke-WebRequest "http://localhost:$Port/api/v1/public/languages" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200
    } 300 'backend sẵn sàng'
    Write-Host "Backend sẵn sàng (PID $($backend.Id)). Log: $beOut"

    Step 'Nạp dữ liệu demo'
    $env:BASE = "http://localhost:$Port"
    $env:API_LOG = $beOut
    $env:ADMIN_LOGIN = $adminLogin
    $env:ADMIN_PASSWORD = $plain
    $env:OUT = $resultFile
    & python -X utf8 $seedScript
    $seedExit = $LASTEXITCODE
    foreach ($name in 'BASE', 'API_LOG', 'ADMIN_LOGIN', 'ADMIN_PASSWORD', 'OUT') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    if ($seedExit -eq 3) { Write-Host 'CSDL đã có dữ liệu demo, không nạp trùng.' -ForegroundColor Yellow; return }
    if ($seedExit -ne 0) { throw "Nạp dữ liệu demo thất bại (mã thoát $seedExit). Log backend: $beOut" }

    $data = Get-Content $resultFile -Raw -Encoding utf8 | ConvertFrom-Json
    Step "Tài khoản demo trên CSDL $Database (mật khẩu chung: $demoPassword)"
    Write-Host "Quản trị hệ thống: admin.nho, admin.lam, admin.trieu, admin.huy (mật khẩu là mật khẩu bạn đã truyền cho script)"
    Write-Host ("Quản trị trường: {0} / {1}" -f $data.organizationAdmin.username, $data.organizationAdmin.email)
    foreach ($t in $data.teachers) { Write-Host ("Giáo viên: {0} / {1}" -f $t.username, $t.email) }
    foreach ($grade in '3', '8', '11') {
        $c = $data.classes.$grade
        $done = @($data.submitted | Where-Object { $c.students.studentCode -contains $_.studentCode } | ForEach-Object { $_.studentCode } | Select-Object -Unique)
        Write-Host ("Lớp {0} (khối {1}, giáo viên {2}): {3}" -f $c.name, $grade, $c.teacher, (($c.students | ForEach-Object { $_.studentCode }) -join ', '))
        Write-Host ("   đã nộp bài: {0}" -f ($done -join ', '))
    }
    foreach ($l in $data.lessons) { Write-Host ("Bài học khối {0}: {1} - mã {2}; bài kiểm tra: {3}" -f $l.grade, $l.title, $l.code, $l.quizName) }
    Write-Host "Di sản đã xuất bản: $($data.heritagesPublished); đã dịch và tóm tắt: $(@($data.heritagesTranslated).Count); có mô hình 3D mẫu: $(@($data.heritagesWith3d).Count)"
    Write-Host "Tệp kết quả: $resultFile"
}
catch {
    Write-Host "LỖI: $($_.Exception.Message)" -ForegroundColor Red
    $exit = 1
}
finally {
    foreach ($name in $envNames + 'BASE', 'API_LOG', 'ADMIN_LOGIN', 'ADMIN_PASSWORD', 'OUT') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    if ($backend -and -not $backend.HasExited) {
        & taskkill /PID $backend.Id /T /F 2>&1 | Out-Null
        Write-Host "`nĐã tắt backend (PID $($backend.Id))."
    }
    $plain = $null
}
exit $exit
