# Máy chủ Google Cloud

Chạy cả hệ thống Vietnam Heritage trên một máy ảo Google Cloud bằng Docker Compose, thay cho máy demo QUANG-NHO
(IIS + ngrok). Máy demo vẫn giữ làm dự phòng: CD triển khai lên cả hai nơi, tắt máy demo bằng biến repo `MAY_DEMO_DEPLOY=false`.

## Kiến trúc

```
Internet ──443──▶ edge (Caddy, HTTPS tự gia hạn) ──┬─ /api, /files, /health ──▶ api (ASP.NET Core) ──▶ sql (SQL Server 2019 Express)
         ──8443─▶                                  │                              └──▶ ai (Dịch vụ AI) ──▶ Gemini
                                                   └─ còn lại ──▶ web (Caddy: 8080 web học tập, 8081 web quản trị)
```

| Thành phần | Nơi ở |
|---|---|
| Máy ảo | `dsvhvn-may-chu`, e2-standard-2 (2 vCPU, 8 GB), Ubuntu 24.04, `asia-southeast1-b` (Singapore), IP tĩnh |
| Ảnh Docker | Artifact Registry `asia-southeast1-docker.pkg.dev/<dự án>/dsvhvn/{api,ai,web}:<mã commit>` |
| Thư mục trên máy | `/opt/dsvhvn`: `compose.yml`, `.env` (chỉ root đọc), `cau-hinh/`, `du-lieu/storage/`, `sao-luu/`, `caddy/`, `bin/` |
| CSDL | `dsvhvn` trên SQL Server trong container (volume `dsvhvn_sql-data`); API đăng nhập bằng `dsvhvn_app` (chỉ đọc, ghi dữ liệu) |
| Sao lưu | trước mỗi lần triển khai API (giữ 10 bản) và 01:30 hằng đêm (giữ 14 bản, chép thêm lên bucket `<dự án>-dsvhvn-sao-luu`, giữ 30 ngày) |

Chưa có tên miền thì hai web chạy bằng IP: web học tập `https://<IP>`, web quản trị `https://<IP>:8443`. Chứng chỉ của
Let's Encrypt cho địa chỉ IP (hồ sơ `shortlived`, hạn khoảng 6 ngày, Caddy tự gia hạn).

## Cài lần đầu (trên máy dev Windows, PowerShell 7)

Cần: Google Cloud CLI đã đăng nhập (`gcloud auth login`), dự án đã gắn tài khoản thanh toán, `gh` đã đăng nhập.

1. `deploy\google-cloud\tao-ha-tang.ps1 -DuAn <mã dự án> -DatBienGitHub`: tạo API, kho ảnh, IP, tường lửa, bucket, tài khoản dịch
   vụ, Workload Identity Federation, máy ảo; cài Docker và `/opt/dsvhvn`; đặt biến repo cho CD. Ghi
   `%APPDATA%\DSVHVN\may-chu\ha-tang.json`.
2. `deploy\google-cloud\tao-cau-hinh-may-chu.ps1 -TaiLen`: sinh cấu hình bí mật từ User Secrets của backend và
   `%APPDATA%\DSVHVN\ai-service.env`, tải lên máy chủ.
3. `deploy\google-cloud\chuyen-du-lieu.ps1`: khôi phục bản sao lưu hằng đêm mới nhất của máy demo thành CSDL `dsvhvn`, chép kho tệp.
4. Chạy CD của ba repo (Actions → CD → Run workflow, nơi triển khai `google-cloud`), thứ tự backend, Dịch vụ AI, frontend.

Từ đó, gộp vào `main` mà CI đạt thì CD tự dựng ảnh, đẩy lên Artifact Registry rồi SSH (qua IAP) vào máy chạy
`/opt/dsvhvn/bin/trien-khai.sh <api|ai|web> <mã commit>`.

## Vận hành

Các lệnh chạy trên máy chủ (`gcloud compute ssh dsvhvn-may-chu --zone asia-southeast1-b --tunnel-through-iap`):

| Việc | Lệnh |
|---|---|
| Trạng thái, nhật ký | `cd /opt/dsvhvn && sudo docker compose ps`, `sudo docker compose logs -f --tail 100 api` |
| Triển khai lại một bản | `sudo /opt/dsvhvn/bin/trien-khai.sh api <mã commit>` (ảnh phải có trong Artifact Registry) |
| Sao lưu ngay | `sudo /opt/dsvhvn/bin/sao-luu-csdl.sh hang-dem` |
| Khôi phục CSDL | `sudo /opt/dsvhvn/bin/khoi-phuc-csdl.sh --ghi-de /opt/dsvhvn/sao-luu/truoc-trien-khai/<tệp>.bak` |
| Đổi khóa (Gmail, Gemini, VNPay...) | sửa User Secrets hoặc `ai-service.env` trên máy dev, chạy lại `tao-cau-hinh-may-chu.ps1 -TaiLen` |
| App Android | dựng bằng `scripts\dung-apk.ps1 -ApiBaseUrl https://<địa chỉ>/api/v1 -WebBaseUrl https://<địa chỉ>` của repo frontend, đăng bản phát hành mới (tag `android-<phiên bản>-<ngày>`, tệp `vietnam-heritage.apk`, đặt Latest): web học tập (biến repo `GCP_ANDROID_APP_URL`) và phiếu đăng nhập học sinh trỏ `releases/latest/download/vietnam-heritage.apk` nên không phải sửa link |
| Chuyển sang tên miền | sửa `DIA_CHI_HOC_TAP`, `DIA_CHI_QUAN_TRI` trong `/opt/dsvhvn/.env`, `sudo docker compose up -d edge`; đổi địa chỉ trong `ha-tang.json`, chạy lại `tao-cau-hinh-may-chu.ps1 -TaiLen`; đổi biến repo `GCP_LEARNING_WEB_URL`, `GCP_ADMIN_WEB_URL` của frontend rồi chạy lại CD frontend |

Triển khai hỏng (không khỏe sau 90 giây) thì script tự chạy lại ảnh trước. CSDL không tự khôi phục: migration của dự án chỉ thêm
nên bản trước thường vẫn chạy; script in lệnh khôi phục từ bản sao lưu vừa tạo.

## Kiểm thử

Job `docker` trong CI dựng ảnh API và chạy đúng các script ở `may-chu/` trên máy ảo của GitHub (thư mục giả lập, CSDL trống,
triển khai, sao lưu, khôi phục), nên đổi script hay Dockerfile mà hỏng thì Pull Request báo trước khi lên máy chủ.

## Chi phí và thời hạn

Máy ảo, ổ đĩa, IP, kho ảnh, bucket tính vào tín dụng dùng thử 300 USD (90 ngày từ ngày đăng ký Google Cloud). Hết hạn dùng thử
mà chưa nâng lên tài khoản trả phí thì máy ảo bị dừng và dữ liệu có thể bị xóa sau 30 ngày: nâng cấp trước hạn, hoặc chuyển lại
máy demo (bản sao lưu ở bucket và trong `/opt/dsvhvn/sao-luu`).
