#!/usr/bin/env bash
# Smoke test luồng Lớp, học sinh và tài khoản học sinh trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-nen-tang.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file), liên kết đặt mật khẩu
# lấy từ log của API (Email:Mode = Log). Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5091 > api.log 2>&1 &
#   BASE=http://localhost:5091 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<Seed:AdminPassword>' bash scripts/smoke-lop-hoc-sinh.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-lop-out}
PW=MatKhau2026
NEWPW=MatKhauCuaEm2026
S=$(date +%H%M%S)
export PYTHONIOENCODING=utf-8

mkdir -p "$OUT"
API_LOG=$(cd "$(dirname "$API_LOG")" && pwd -W 2>/dev/null || pwd)/$(basename "$API_LOG")
cd "$OUT" || exit 1
PASS=0; FAIL=0

val() { python -c "
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))['data']
for k in sys.argv[2].split('.'): d=d[int(k)] if isinstance(d,list) else d[k]
print(d)" "$1" "$2"; }

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

# step <tên> <mã HTTP mong đợi> <method> <url> [tệp body JSON] [token] [tham số curl thêm...]
step() {
  local name=$1 want=$2 method=$3 url=$4 body=${5:-} token=${6:-}
  shift 6 2>/dev/null || shift $#
  local args=(-s -o "$name.json" -w "%{http_code}" -X "$method" "$BASE$url" -H "Accept: application/json")
  [ -n "$body" ] && args+=(-H "Content-Type: application/json; charset=utf-8" --data-binary "@$body")
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  args+=("$@")
  local got; got=$(curl "${args[@]}")
  local info; info=$(python -c "
import json,sys
try:
    d=json.load(open(sys.argv[1],encoding='utf-8'))
    print(((d.get('errorCode') or '')+' '+(d.get('message') or '')).strip())
except Exception: print('(tệp)')" "$name.json")
  if [ "$got" = "$want" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | %s %s | %s | %s | %s | %s |\n' "$name" "$method" "$url" "$want" "$got" "$r" "$info"
}

json() { printf '%s' "$2" > "$1"; }

json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json org.json "{\"name\":\"Trường THCS Nguyễn Du $S\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@nguyendu.edu.vn\"}}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S\",\"password\":\"$PW\"}"
json teacher.json "{\"fullName\":\"Đặng Thị Mỹ Linh\",\"username\":\"linh.$S\",\"email\":\"linh.$S@nguyendu.edu.vn\"}"
json login-linh.json "{\"emailOrUsername\":\"linh.$S\",\"password\":\"$PW\"}"
json class.json '{"name":"8A1","grade":8,"schoolYear":"2026-2027"}'
json class-bad.json '{"name":"8A1","grade":13,"schoolYear":"2026-2027"}'
json class-edit.json '{"name":"8A1","grade":9,"schoolYear":"2026-2027"}'
json student.json '{"fullName":"Nguyễn Văn Đạt"}'
json text.txt $'Họ và tên\nTrần Thị Bé\n\nLê Văn Cường\nPhạm Minh Đức'
printf '\xef\xbb\xbfHọ và tên;Ghi chú\r\nĐỗ Thị Hạnh;x\r\nVũ Văn Khánh;y\r\n' > list.csv
json import.json '{"rows":[{"rowNumber":2,"fullName":"Trần Thị Bé"},{"rowNumber":3,"fullName":""},{"rowNumber":4,"fullName":"Lê Văn Cường"},{"rowNumber":5,"fullName":"Phạm Minh Đức"}]}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)
step 02-tao-truong         201 POST /api/v1/admin/organizations org.json "$ADMIN"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 03-qt-truong-dat-mk   200 POST /api/v1/auth/set-password set-thu.json
step 04-qt-truong-login    200 POST /api/v1/auth/login login-thu.json
THU=$(val 04-qt-truong-login.json accessToken)
step 05-tao-giao-vien      201 POST /api/v1/org/teachers teacher.json "$THU"
json set-linh.json "{\"token\":\"$(mail_token "linh.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 06-gv-dat-mk          200 POST /api/v1/auth/set-password set-linh.json
step 07-gv-login           200 POST /api/v1/auth/login login-linh.json
LINH=$(val 07-gv-login.json accessToken)

step 10-khoi-lop-sai       400 POST /api/v1/teacher/classes class-bad.json "$LINH"
step 11-tao-lop            201 POST /api/v1/teacher/classes class.json "$LINH"
CLASS=$(val 11-tao-lop.json id)
step 12-tao-lop-trung-ten  409 POST /api/v1/teacher/classes class.json "$LINH"
step 13-ds-lop             200 GET  /api/v1/teacher/classes "" "$LINH"
step 14-sua-lop            200 PUT  "/api/v1/teacher/classes/$CLASS" class-edit.json "$LINH"
step 15-qt-truong-vao-lop  403 GET  /api/v1/teacher/classes "" "$THU"
step 16-qt-truong-so-lieu  200 GET  /api/v1/org/school-summary "" "$THU"

step 20-them-hoc-sinh      201 POST "/api/v1/teacher/classes/$CLASS/students" student.json "$LINH"
CODE=$(val 20-them-hoc-sinh.json sheet.entries.0.studentCode)
TEMP=$(val 20-them-hoc-sinh.json sheet.entries.0.temporaryPassword)
STUDENT_ID=$(val 20-them-hoc-sinh.json students.0.id)
step 21-xem-truoc-van-ban  200 POST "/api/v1/teacher/classes/$CLASS/students/import/preview" "" "$LINH" -F "text=<text.txt"
step 22-xem-truoc-csv      200 POST "/api/v1/teacher/classes/$CLASS/students/import/preview" "" "$LINH" -F "file=@list.csv"
step 23-nhap-danh-sach     201 POST "/api/v1/teacher/classes/$CLASS/students/import" import.json "$LINH"
step 24-tep-mau-xlsx       200 GET  "/api/v1/teacher/classes/$CLASS/students/import/template?format=xlsx" "" "$LINH"
step 25-ds-hoc-sinh        200 GET  "/api/v1/teacher/classes/$CLASS/students?pageSize=100" "" "$LINH"

json hs-sai.json "{\"studentCode\":\"$CODE\",\"password\":\"SaiMatKhau1\"}"
json hs-gv.json "{\"studentCode\":\"linh.$S\",\"password\":\"$PW\"}"
json hs-tam.json "{\"studentCode\":\"$(echo "$CODE" | tr 'A-Z' 'a-z')\",\"password\":\"$TEMP\"}"
step 30-hs-sai-mat-khau    401 POST /api/v1/student/auth/login hs-sai.json
step 31-gv-o-khu-hoc       401 POST /api/v1/student/auth/login hs-gv.json
step 32-hs-login-lan-dau   200 POST /api/v1/student/auth/login hs-tam.json
HS1=$(val 32-hs-login-lan-dau.json accessToken)
step 33-hs-thong-tin       200 GET  /api/v1/student/me "" "$HS1"
step 34-hs-lop-bi-chan     403 GET  /api/v1/student/class "" "$HS1"
json pw-lech.json "{\"newPassword\":\"$NEWPW\",\"confirmNewPassword\":\"KhacNhau2026\"}"
json pw-trung.json "{\"newPassword\":\"$TEMP\",\"confirmNewPassword\":\"$TEMP\"}"
json pw-moi.json "{\"newPassword\":\"$NEWPW\",\"confirmNewPassword\":\"$NEWPW\"}"
step 35-doi-mk-khong-khop  400 PUT  /api/v1/student/me/password pw-lech.json "$HS1"
step 36-doi-mk-trung-tam   400 PUT  /api/v1/student/me/password pw-trung.json "$HS1"
step 37-doi-mk-lan-dau     200 PUT  /api/v1/student/me/password pw-moi.json "$HS1"
HS2=$(val 37-doi-mk-lan-dau.json accessToken)
step 38-token-truoc-doi-mk 401 GET  /api/v1/student/me "" "$HS1"
step 39-hs-xem-lop         200 GET  /api/v1/student/class "" "$HS2"

json reset.json "{\"studentIds\":[$STUDENT_ID]}"
step 40-dat-lai-mat-khau   200 POST "/api/v1/teacher/classes/$CLASS/students/reset-password" reset.json "$LINH"
TEMP2=$(val 40-dat-lai-mat-khau.json entries.0.temporaryPassword)
step 41-phien-cu-het-dung  401 GET  /api/v1/student/class "" "$HS2"
json hs-tam2.json "{\"studentCode\":\"$CODE\",\"password\":\"$TEMP2\"}"
step 42-login-mat-khau-tam 200 POST /api/v1/student/auth/login hs-tam2.json
step 43-vo-hieu            200 POST "/api/v1/teacher/classes/$CLASS/students/$STUDENT_ID/deactivate" "" "$LINH"
step 44-vo-hieu-login      403 POST /api/v1/student/auth/login hs-tam2.json
step 45-mo-lai             200 POST "/api/v1/teacher/classes/$CLASS/students/$STUDENT_ID/reactivate" "" "$LINH"
step 46-mo-lai-login       200 POST /api/v1/student/auth/login hs-tam2.json
for i in 1 2 3 4; do step 47-sai-$i 401 POST /api/v1/student/auth/login hs-sai.json; done
step 48-sai-lan-5-tam-khoa 403 POST /api/v1/student/auth/login hs-sai.json
step 49-dang-tam-khoa      403 POST /api/v1/student/auth/login hs-tam2.json
step 50-dat-lai-go-khoa    200 POST "/api/v1/teacher/classes/$CLASS/students/reset-password" reset.json "$LINH"
TEMP3=$(val 50-dat-lai-go-khoa.json entries.0.temporaryPassword)
json hs-tam3.json "{\"studentCode\":\"$CODE\",\"password\":\"$TEMP3\"}"
step 51-het-tam-khoa-login 200 POST /api/v1/student/auth/login hs-tam3.json
step 52-xoa-lop            200 DELETE "/api/v1/teacher/classes/$CLASS" "" "$LINH"
step 53-lop-da-xoa-login   403 POST /api/v1/student/auth/login hs-tam3.json
step 54-nhat-ky            200 GET  "/api/v1/admin/audit-logs?pageSize=50" "" "$ADMIN"
for pw in "$TEMP" "$TEMP2" "$TEMP3" "$NEWPW"; do
  if grep -q "$pw" 54-nhat-ky.json; then echo "SAI: mật khẩu xuất hiện trong nhật ký"; FAIL=$((FAIL+1)); fi
done

echo
echo "Hậu tố dữ liệu: $S · lớp id=$CLASS · mã học sinh=$CODE"
echo "Kết quả: $PASS/$((PASS+FAIL)) bước đúng mã HTTP. Phản hồi từng bước: $(pwd)"
[ "$FAIL" -eq 0 ]
