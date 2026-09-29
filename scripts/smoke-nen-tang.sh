#!/usr/bin/env bash
# Smoke test luồng nền tảng tổ chức trên API đang chạy, mặc định http://localhost:5080 với CSDL sep490_dsvhvn_v3_dev.
#
# Body JSON có chữ tiếng Việt luôn đi qua FILE (curl --data-binary @file, đường dẫn tương đối): trên Windows, đối số
# dòng lệnh bị đổi bảng mã và hỏng dấu. Liên kết đặt mật khẩu lấy từ log của API: chạy API với Email:Mode = Log
# (mặc định) để thư được ghi ra log thay vì gửi đi.
#
# Dùng:
#   dotnet run --project src/DSVHVN.Api --launch-profile http > api.log 2>&1 &     # Seed:AdminPassword đã cấu hình
#   API_LOG="$PWD/api.log" ADMIN_PASSWORD='<Seed:AdminPassword>' bash scripts/smoke-nen-tang.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-out}
PW=MatKhau2026
S=$(date +%H%M%S)
export PYTHONIOENCODING=utf-8

mkdir -p "$OUT"
API_LOG=$(cd "$(dirname "$API_LOG")" && pwd -W 2>/dev/null || pwd)/$(basename "$API_LOG")
cd "$OUT" || exit 1
PASS=0; FAIL=0

# Đọc data.<đường dẫn> của phản hồi, vd: val 03-login.json accessToken ; val x.json user.id
val() { python -c "
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))['data']
for k in sys.argv[2].split('.'): d=d[int(k)] if isinstance(d,list) else d[k]
print(d)" "$1" "$2"; }

# Token trong thư mới nhất ghi ra log cho email (chờ tối đa 5 giây cho log ghi xong).
mail_token() { python -c "
import re,sys,time
email=sys.argv[2].lower()
for _ in range(25):
    text=open(sys.argv[1],encoding='utf-8',errors='replace').read()
    pos=text.lower().rfind('tới: '+email)
    if pos<0: pos=text.lower().rfind(': '+email+' |')
    if pos>=0:
        m=re.search(r'token=([A-Za-z0-9_\-%]+)',text[pos:])
        if m: print(m.group(1)); sys.exit(0)
    time.sleep(0.2)
sys.exit('không thấy thư tới '+email)" "$API_LOG" "$1"; }

# step <tên> <mã HTTP mong đợi> <method> <url> [tệp body] [token]
step() {
  local name=$1 want=$2 method=$3 url=$4 body=${5:-} token=${6:-}
  local args=(-s -o "$name.json" -w "%{http_code}" -X "$method" "$BASE$url" -H "Accept: application/json")
  [ -n "$body" ] && args+=(-H "Content-Type: application/json; charset=utf-8" --data-binary "@$body")
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  local got; got=$(curl "${args[@]}")
  local info; info=$(python -c "
import json,sys
try:
    d=json.load(open(sys.argv[1],encoding='utf-8'))
    print(((d.get('errorCode') or '')+' '+(d.get('message') or '')).strip())
except Exception: print('')" "$name.json")
  if [ "$got" = "$want" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | %s %s | %s | %s | %s | %s |\n' "$name" "$method" "$url" "$want" "$got" "$r" "$info"
}

json() { printf '%s' "$2" > "$1"; }

json admin-wrong.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"SaiMatKhau1\"}"
json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json org-a.json "{\"name\":\"Trường THCS Nguyễn Du $S\",\"address\":\"25 Nguyễn Du, Quận 1, TP. Hồ Chí Minh\",\"phone\":\"028 3829 0000\",\"email\":\"vanthu.$S@nguyendu.edu.vn\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@nguyendu.edu.vn\",\"phone\":\"0903 111 222\"}}"
json org-b.json "{\"name\":\"Trường Tiểu học Lê Lợi $S\",\"orgAdmin\":{\"fullName\":\"Trần Văn Hùng\",\"username\":\"hung.$S\",\"email\":\"hung.$S@leloi.edu.vn\"}}"
json org-contact.json '{"address":"180 Cao Lỗ, Phường 4, Quận 8, TP. Hồ Chí Minh","phone":"028 3850 5520","email":"lienhe@nguyendu.edu.vn"}'
json teacher.json "{\"fullName\":\"Đặng Thị Mỹ Linh\",\"username\":\"linh.$S\",\"email\":\"linh.$S@nguyendu.edu.vn\",\"phone\":\"0912 345 678\"}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S@nguyendu.edu.vn\",\"password\":\"$PW\"}"
json login-hung.json "{\"emailOrUsername\":\"hung.$S\",\"password\":\"$PW\"}"
json login-linh.json "{\"emailOrUsername\":\"linh.$S\",\"password\":\"$PW\"}"
json change-pw.json "{\"currentPassword\":\"$PW\",\"newPassword\":\"MatKhauMoi2026\"}"
json forgot.json "{\"email\":\"thu.$S@nguyendu.edu.vn\"}"

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-health            200 GET  /health
step 02-login-sai         401 POST /api/v1/auth/login admin-wrong.json
step 03-login-admin       200 POST /api/v1/auth/login admin.json
ADMIN=$(val 03-login-admin.json accessToken)
step 04-me-admin          200 GET  /api/v1/me "" "$ADMIN"
step 05-tao-truong-a      201 POST /api/v1/admin/organizations org-a.json "$ADMIN"
ORG_A=$(val 05-tao-truong-a.json id)
step 06-tao-truong-trung  409 POST /api/v1/admin/organizations org-a.json "$ADMIN"
step 07-ds-truong         200 GET  "/api/v1/admin/organizations?keyword=$S&status=ACTIVE" "" "$ADMIN"
json token-thu.json "{\"token\":\"$(mail_token "thu.$S@nguyendu.edu.vn")\"}"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 08-kiem-lien-ket     200 POST /api/v1/auth/password-token/check token-thu.json
step 09-dat-mk-lan-dau    200 POST /api/v1/auth/set-password set-thu.json
step 10-dung-lai-lien-ket 422 POST /api/v1/auth/set-password set-thu.json
step 11-login-org-admin   200 POST /api/v1/auth/login login-thu.json
THU=$(val 11-login-org-admin.json accessToken)
step 12-xem-truong        200 GET  /api/v1/org "" "$THU"
step 13-sua-lien-he       200 PUT  /api/v1/org org-contact.json "$THU"
step 14-tao-giao-vien     201 POST /api/v1/org/teachers teacher.json "$THU"
LINH_ID=$(val 14-tao-giao-vien.json id)
step 15-ds-giao-vien      200 GET  /api/v1/org/teachers "" "$THU"
json set-linh.json "{\"token\":\"$(mail_token "linh.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 16-gv-dat-mk         200 POST /api/v1/auth/set-password set-linh.json
step 17-login-gv          200 POST /api/v1/auth/login login-linh.json
LINH=$(val 17-login-gv.json accessToken)
step 18-gv-vao-khu-truong 403 GET  /api/v1/org/teachers "" "$LINH"
step 19-org-admin-vao-qt  403 GET  /api/v1/admin/organizations "" "$THU"
step 20-tao-truong-b      201 POST /api/v1/admin/organizations org-b.json "$ADMIN"
ORG_B=$(val 20-tao-truong-b.json id)
json set-hung.json "{\"token\":\"$(mail_token "hung.$S@leloi.edu.vn")\",\"newPassword\":\"$PW\"}"
step 21-b-dat-mk          200 POST /api/v1/auth/set-password set-hung.json
step 22-login-b           200 POST /api/v1/auth/login login-hung.json
HUNG=$(val 22-login-b.json accessToken)
step 23-b-xem-gv-cua-a    404 GET  "/api/v1/org/teachers/$LINH_ID" "" "$HUNG"
step 24-b-khoa-gv-cua-a   404 POST "/api/v1/org/teachers/$LINH_ID/lock" "" "$HUNG"
step 25-khoa-gv           200 POST "/api/v1/org/teachers/$LINH_ID/lock" "" "$THU"
step 26-gv-bi-khoa        401 GET  /api/v1/me "" "$LINH"
step 27-mo-khoa-gv        200 POST "/api/v1/org/teachers/$LINH_ID/unlock" "" "$THU"
step 28-gv-vao-lai        200 GET  /api/v1/me "" "$LINH"
step 29-doi-mk            200 PUT  /api/v1/me/password change-pw.json "$LINH"
step 30-token-cu          401 GET  /api/v1/me "" "$LINH"
step 31-xoa-gv            200 DELETE "/api/v1/org/teachers/$LINH_ID" "" "$THU"
step 32-gv-da-xoa-login   401 POST /api/v1/auth/login login-linh.json
step 33-nhat-ky           200 GET  "/api/v1/admin/audit-logs?pageSize=50" "" "$ADMIN"
step 34-ngung-truong-b    200 DELETE "/api/v1/admin/organizations/$ORG_B" "" "$ADMIN"
step 35-b-token-cu        401 GET  /api/v1/me "" "$HUNG"
step 36-b-login           403 POST /api/v1/auth/login login-hung.json
step 37-khong-token       401 GET  /api/v1/me
step 38-quen-mk           200 POST /api/v1/auth/forgot-password forgot.json
step 39-chi-tiet-truong-a 200 GET  "/api/v1/admin/organizations/$ORG_A" "" "$ADMIN"

echo
echo "Hậu tố dữ liệu: $S · trường A id=$ORG_A · trường B id=$ORG_B · giáo viên id=$LINH_ID"
echo "Kết quả: $PASS/$((PASS+FAIL)) bước đúng mã HTTP. Phản hồi từng bước: $(pwd)"
[ "$FAIL" -eq 0 ]
