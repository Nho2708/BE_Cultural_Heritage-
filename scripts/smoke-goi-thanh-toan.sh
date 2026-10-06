#!/usr/bin/env bash
# Smoke test luồng Gói và thanh toán VNPay trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Luồng cần tài khoản quản trị hệ thống và đọc liên kết đặt mật khẩu của quản trị trường từ log của API (Email:Mode = Log). IPN của VNPay được GIẢ ở đây:
# script tự ký tham số bằng HMAC-SHA512 với khóa THỬ (không phải khóa thật của VNPay), nên Api phải chạy với cùng khóa thử đó. Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_tam_a2_goi;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Ai__Mode=Simulated Storage__Provider=Local Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   Seed__HeritageData__ProvincesFile= Seed__HeritageData__HeritagesFile= \
#   VnPay__TmnCode=SMOKETMN VnPay__HashSecret='<khóa thử, vd smoke-only-secret-0123456789>' \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5080 > api.log 2>&1 &
#   API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' VNPAY_HASH_SECRET='<khóa thử như trên>' bash scripts/smoke-goi-thanh-toan.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), VNPAY_TMN_CODE (mặc định SMOKETMN), SMOKE_OUT (thư mục ghi kết quả từng bước).
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
VNPAY_HASH_SECRET=${VNPAY_HASH_SECRET:?Cần VNPAY_HASH_SECRET: khóa thử mà Api đang dùng (VnPay__HashSecret)}
VNPAY_TMN_CODE=${VNPAY_TMN_CODE:-SMOKETMN}
OUT=${SMOKE_OUT:-smoke-goi-thanh-toan-out}
PW=MatKhau2026
S=$(date +%H%M%S)
export PYTHONIOENCODING=utf-8 VNPAY_HASH_SECRET VNPAY_TMN_CODE

mkdir -p "$OUT"
API_LOG=$(cd "$(dirname "$API_LOG")" && pwd -W 2>/dev/null || pwd)/$(basename "$API_LOG")
cd "$OUT" || exit 1
PASS=0; FAIL=0

# Đọc data.<đường dẫn> của phản hồi, vd: val 03-login.json accessToken
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

# step <tên> <mã HTTP mong đợi> <method> <url> [tệp body JSON] [token]
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
    print(((d.get('errorCode') or '')+' '+(d.get('message') or '')).strip()[:150])
except Exception: print('')" "$name.json")
  if [ "$got" = "$want" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | %s %s | %s | %s | %s | %s |\n' "$name" "$method" "$url" "$want" "$got" "$r" "$info"
}

# check <tên> <điều kiện python trên d = phần data của tệp> <tệp phản hồi>
check() {
  local name=$1 cond=$2 file=$3
  local ok; ok=$(python -c "
import json,sys
d=json.load(open(sys.argv[2],encoding='utf-8'))['data']
print('1' if eval(sys.argv[1]) else '0')" "$cond" "$file" 2>/dev/null)
  if [ "$ok" = "1" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | kiểm nội dung %s | đúng | %s | %s | %s |\n' "$name" "$file" "$ok" "$r" "$cond"
}

# checkcode <tên> <errorCode mong đợi> <tệp phản hồi>
checkcode() {
  local name=$1 want=$2 file=$3
  local got; got=$(python -c "
import json,sys
print(json.load(open(sys.argv[1],encoding='utf-8')).get('errorCode'))" "$file" 2>/dev/null)
  if [ "$got" = "$want" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | errorCode của %s | %s | %s | %s | |\n' "$name" "$file" "$want" "$got" "$r"
}

json() { printf '%s' "$2" > "$1"; }

# Dựng chuỗi query của IPN/trang trả về có chữ ký như VNPay làm: ipn_qs <mã tham chiếu> <số tiền đồng> <mã phản hồi> <trạng thái> <mã giao dịch của cổng> [số tiền ghi đè] [khóa ghi đè]
cat > ipn.py <<'PY'
import hashlib, hmac, os, sys, urllib.parse
ref, price, code, status, txn = sys.argv[1:6]
amount = sys.argv[6] if len(sys.argv) > 6 and sys.argv[6] else str(int(price) * 100)
secret = sys.argv[7] if len(sys.argv) > 7 and sys.argv[7] else os.environ["VNPAY_HASH_SECRET"]
p = {"vnp_TmnCode": os.environ["VNPAY_TMN_CODE"], "vnp_Amount": amount, "vnp_BankCode": "NCB", "vnp_BankTranNo": "VNP" + txn,
     "vnp_CardType": "ATM", "vnp_OrderInfo": "thanh toan hoa don", "vnp_PayDate": "20261008103000", "vnp_ResponseCode": code,
     "vnp_TransactionNo": txn, "vnp_TransactionStatus": status, "vnp_TxnRef": ref}
data = "&".join(k + "=" + urllib.parse.quote_plus(p[k]) for k in sorted(p))
sig = hmac.new(secret.encode(), data.encode(), hashlib.sha512).hexdigest()
print(data + "&vnp_SecureHashType=HmacSHA512&vnp_SecureHash=" + sig)
PY
ipn_qs() { python ipn.py "$@"; }

# Kiểm chữ ký của địa chỉ thanh toán mà Api trả (tính độc lập bằng Python) và đọc tham số: payurl <tệp phản hồi> <điều kiện python trên p = tham số>
payurl_ok() {
  local name=$1 file=$2 cond=$3
  local ok; ok=$(python -c "
import hashlib, hmac, json, os, sys, urllib.parse
d=json.load(open(sys.argv[1],encoding='utf-8'))['data']
u=urllib.parse.urlparse(d['paymentUrl'])
p=dict(urllib.parse.parse_qsl(u.query, keep_blank_values=True))
got=p.pop('vnp_SecureHash')
data='&'.join(k+'='+urllib.parse.quote_plus(p[k]) for k in sorted(p) if p[k] != '')
mine=hmac.new(os.environ['VNPAY_HASH_SECRET'].encode(), data.encode(), hashlib.sha512).hexdigest()
print('1' if got==mine and u.netloc=='sandbox.vnpayment.vn' and eval(sys.argv[2]) else '0')" "$file" "$cond" 2>/dev/null)
  if [ "$ok" = "1" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | địa chỉ VNPay trong %s: chữ ký và tham số | đúng | %s | %s | %s |\n' "$name" "$file" "$ok" "$r" "$cond"
}

# ipn <tên> <RspCode mong đợi> <chuỗi query>: gọi IPN như VNPay (không đăng nhập), kiểm mã HTTP 200 và RspCode
ipn() {
  local name=$1 want=$2 qs=$3
  local code; code=$(curl -s -o "$name.json" -w "%{http_code}" "$BASE/api/v1/payments/vnpay/ipn?$qs")
  local rsp; rsp=$(python -c "
import json,sys
print(json.load(open(sys.argv[1],encoding='utf-8')).get('RspCode'))" "$name.json" 2>/dev/null)
  if [ "$code" = "200" ] && [ "$rsp" = "$want" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | GET /api/v1/payments/vnpay/ipn | 200 RspCode %s | %s RspCode %s | %s | |\n' "$name" "$want" "$code" "$rsp" "$r"
}

PLAN_A="Gói thử A $S"
PLAN_B="Gói thử B $S"
json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json plan-a.json "{\"name\":\"$PLAN_A\",\"price\":500000,\"durationDays\":30,\"maxTeachers\":5,\"maxAiRequests\":100}"
json plan-b.json "{\"name\":\"$PLAN_B\",\"price\":1990000,\"durationDays\":365,\"maxTeachers\":20,\"maxAiRequests\":1000}"
json plan-a-upper.json "{\"name\":\"GOI THU A $S\",\"price\":1000,\"durationDays\":30,\"maxTeachers\":1,\"maxAiRequests\":0}"
json plan-bad.json '{"name":"","price":-5,"durationDays":0}'
json plan-free-zero.json "{\"name\":\"Gói giá 0 thứ hai $S\",\"price\":0,\"durationDays\":30,\"maxTeachers\":1,\"maxAiRequests\":0}"
json plan-a-edit.json "{\"name\":\"$PLAN_A\",\"price\":500000,\"durationDays\":60,\"maxTeachers\":6,\"maxAiRequests\":120}"
json org-a.json "{\"name\":\"Trường THCS Nguyễn Du $S\",\"address\":\"25 Nguyễn Du, Quận 1, TP. Hồ Chí Minh\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@nguyendu.edu.vn\"}}"
json org-b.json "{\"name\":\"Trường Tiểu học Lê Lợi $S\",\"orgAdmin\":{\"fullName\":\"Trần Văn Hùng\",\"username\":\"hung.$S\",\"email\":\"hung.$S@leloi.edu.vn\"}}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S\",\"password\":\"$PW\"}"
json login-hung.json "{\"emailOrUsername\":\"hung.$S\",\"password\":\"$PW\"}"
json empty.json '{}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# ---- Quản lý gói (quản trị hệ thống) ----
step 02-tao-goi-a          201 POST /api/v1/admin/plans plan-a.json "$ADMIN"
PA=$(val 02-tao-goi-a.json id)
check 02-goi-dang-ban "d['isActive'] is True and d['isFree'] is False and d['price']==500000" 02-tao-goi-a.json
step 03-tao-goi-b          201 POST /api/v1/admin/plans plan-b.json "$ADMIN"
PB=$(val 03-tao-goi-b.json id)
step 04-ten-trung-khac-hoa 409 POST /api/v1/admin/plans plan-a-upper.json "$ADMIN"
step 05-goi-sai-du-lieu    400 POST /api/v1/admin/plans plan-bad.json "$ADMIN"
step 06-gia-0-thu-hai      400 POST /api/v1/admin/plans plan-free-zero.json "$ADMIN"
step 07-sua-goi-a          200 PUT  "/api/v1/admin/plans/$PA" plan-a-edit.json "$ADMIN"
check 07-da-sua "d['durationDays']==60 and d['maxTeachers']==6" 07-sua-goi-a.json
step 08-ds-goi             200 GET  /api/v1/admin/plans "" "$ADMIN"
check 08-co-goi-mien-phi "any(p['isFree'] and p['isActive'] for p in d)" 08-ds-goi.json
FREE=$(python -c "
import json
print([p['id'] for p in json.load(open('08-ds-goi.json',encoding='utf-8'))['data'] if p['isFree']][0])")
step 09-tat-goi-mien-phi   422 POST "/api/v1/admin/plans/$FREE/disable" "" "$ADMIN"
step 10-xoa-goi-khong-co   405 DELETE "/api/v1/admin/plans/$PA" "" "$ADMIN"

# ---- Hai trường và quản trị trường ----
step 11-tao-truong-a       201 POST /api/v1/admin/organizations org-a.json "$ADMIN"
ORG_A=$(val 11-tao-truong-a.json id)
step 12-tao-truong-b       201 POST /api/v1/admin/organizations org-b.json "$ADMIN"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
json set-hung.json "{\"token\":\"$(mail_token "hung.$S@leloi.edu.vn")\",\"newPassword\":\"$PW\"}"
step 13-a-dat-mat-khau     200 POST /api/v1/auth/set-password set-thu.json
step 14-b-dat-mat-khau     200 POST /api/v1/auth/set-password set-hung.json
step 15-login-a            200 POST /api/v1/auth/login login-thu.json
THU=$(val 15-login-a.json accessToken)
step 16-login-b            200 POST /api/v1/auth/login login-hung.json
HUNG=$(val 16-login-b.json accessToken)
step 17-chua-dang-nhap     401 GET  /api/v1/org/billing
step 18-admin-khong-vao-khu-truong 403 GET /api/v1/org/billing "" "$ADMIN"

# ---- Tổng quan, danh sách gói, xác nhận ----
step 20-tong-quan          200 GET  /api/v1/org/billing "" "$THU"
check 20-dang-dung-mien-phi "d['current']['isFree'] and d['hasEffectivePlan'] and d['pending'] is None and d['canActivateFreePlan'] is False" 20-tong-quan.json
step 21-ds-goi-ban         200 GET  /api/v1/org/plans "" "$THU"
check 21-khong-co-goi-mien-phi "all(p['price']>0 for p in d) and {p['id'] for p in d} >= {$PA,$PB} and all(p['kind']=='UPGRADE' for p in d if p['id'] in ($PA,$PB))" 21-ds-goi-ban.json
step 22-xac-nhan-goi-a     200 GET  "/api/v1/org/plans/$PA/quote" "" "$THU"
check 22-cau-xac-nhan "d['kind']=='UPGRADE' and d['amount']==500000 and '500.000' in d['confirmation'] and 'VNPay' in d['confirmation'] and d['confirmation'].endswith('Tiếp tục?')" 22-xac-nhan-goi-a.json
step 23-goi-khong-co       404 GET  /api/v1/org/plans/999999/quote "" "$THU"

# ---- Mua gói A: yêu cầu mua, hóa đơn, địa chỉ VNPay ----
json checkout-a.json "{\"planId\":$PA}"
step 30-mua-goi-a          201 POST /api/v1/org/billing/checkout checkout-a.json "$THU"
PAY1=$(val 30-mua-goi-a.json paymentId); INV1=$(val 30-mua-goi-a.json invoice.id); SUB1=$(val 30-mua-goi-a.json invoice.subscriptionId)
check 30-hoa-don-chua-tra "d['invoice']['status']=='UNPAID' and d['invoice']['amount']==500000 and d['invoice']['subscriptionStatus']=='PENDING' and d['invoice']['canPay'] and d['invoice']['invoiceNumber'].startswith('HD')" 30-mua-goi-a.json
payurl_ok 30-dia-chi-vnpay 30-mua-goi-a.json "p['vnp_TxnRef']=='$PAY1' and p['vnp_Amount']=='50000000' and p['vnp_TmnCode']=='$VNPAY_TMN_CODE' and p['vnp_CurrCode']=='VND'"
step 31-mua-lan-hai        422 POST /api/v1/org/billing/checkout checkout-a.json "$THU"
checkcode 31-ma-dang-cho PENDING_PURCHASE_EXISTS 31-mua-lan-hai.json
step 32-tong-quan-cho      200 GET  /api/v1/org/billing "" "$THU"
check 32-co-yeu-cau-cho "d['pending']['invoiceNumber'] is not None and d['pending']['hasWaitingPayment'] and d['current']['isFree']" 32-tong-quan-cho.json
step 33-ket-qua-dang-cho   200 GET  "/api/v1/org/payments/$PAY1" "" "$THU"
check 33-dang-xu-ly "d['outcome']=='PROCESSING' and 'VNPay' in d['message']" 33-ket-qua-dang-cho.json
step 34-b-xem-hoa-don-cua-a 404 GET "/api/v1/org/invoices/$INV1" "" "$HUNG"
step 35-b-xem-ket-qua-cua-a 404 GET "/api/v1/org/payments/$PAY1" "" "$HUNG"
step 36-b-thanh-toan-hd-cua-a 404 POST "/api/v1/org/invoices/$INV1/pay" "" "$HUNG"
step 37-b-huy-goi-cua-a    404 POST "/api/v1/org/subscriptions/$SUB1/cancel" "" "$HUNG"

# ---- Trang trả về chỉ chuyển trình duyệt, không đổi dữ liệu ----
RET=$(curl -s -o /dev/null -w "%{http_code} %{redirect_url}" "$BASE/api/v1/payments/vnpay/return?$(ipn_qs "$PAY1" 500000 00 00 SMOKE-R1)")
if [ "${RET%% *}" = "302" ] && [[ "$RET" == *"/truong/ket-qua-thanh-toan?payment=$PAY1" ]]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
printf '| 38-trang-tra-ve-hop-le | GET /api/v1/payments/vnpay/return | 302 .../ket-qua-thanh-toan?payment=%s | %s | %s | |\n' "$PAY1" "$RET" "$r"
RET=$(curl -s -o /dev/null -w "%{http_code} %{redirect_url}" "$BASE/api/v1/payments/vnpay/return?$(ipn_qs "$PAY1" 500000 00 00 SMOKE-R1 "" "khoa-khac")")
if [ "${RET%% *}" = "302" ] && [[ "$RET" == *"?error=invalid" ]]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
printf '| 39-trang-tra-ve-sai-chu-ky | GET /api/v1/payments/vnpay/return | 302 ...?error=invalid | %s | %s | |\n' "$RET" "$r"
step 39-van-chua-kich-hoat 200 GET  /api/v1/org/billing "" "$THU"
check 39-gia-tri-cu "d['current']['isFree'] and d['pending'] is not None" 39-van-chua-kich-hoat.json

# ---- IPN bị từ chối: không đổi dữ liệu ----
ipn 40-ipn-sai-chu-ky      97 "$(ipn_qs "$PAY1" 500000 00 00 SMOKE-1 "" "khoa-khac")"
ipn 41-ipn-lech-tien       04 "$(ipn_qs "$PAY1" 500000 00 00 SMOKE-1 50000)"
ipn 42-ipn-giao-dich-la    01 "$(ipn_qs 987654321 500000 00 00 SMOKE-1)"
step 43-van-cho-sau-tu-choi 200 GET "/api/v1/org/payments/$PAY1" "" "$THU"
check 43-dang-xu-ly "d['outcome']=='PROCESSING'" 43-van-cho-sau-tu-choi.json

# ---- Thanh toán thất bại rồi thanh toán lại ----
ipn 44-ipn-that-bai        00 "$(ipn_qs "$PAY1" 500000 24 02 0)"
step 45-ket-qua-that-bai   200 GET  "/api/v1/org/payments/$PAY1" "" "$THU"
check 45-thanh-toan-lai-duoc "d['outcome']=='UNPAID' and d['canPayAgain'] and d['message'].startswith('Thanh to') and d['invoice']['invoiceNumber'] in d['message']" 45-ket-qua-that-bai.json
ipn 46-ipn-that-bai-lap    02 "$(ipn_qs "$PAY1" 500000 24 02 0)"
step 47-thanh-toan-lai     200 POST "/api/v1/org/invoices/$INV1/pay" "" "$THU"
PAY2=$(val 47-thanh-toan-lai.json paymentId)
check 47-giao-dich-moi "d['paymentId'] != $PAY1 and d['invoice']['status']=='UNPAID'" 47-thanh-toan-lai.json
payurl_ok 47-dia-chi-vnpay 47-thanh-toan-lai.json "p['vnp_TxnRef']=='$PAY2' and p['vnp_Amount']=='50000000'"

# ---- IPN thành công: kích hoạt gói, IPN lặp không đổi gì ----
ipn 50-ipn-thanh-cong      00 "$(ipn_qs "$PAY2" 500000 00 00 SMOKE-OK-$S)"
ipn 51-ipn-lap-1           02 "$(ipn_qs "$PAY2" 500000 00 00 SMOKE-OK-$S)"
ipn 52-ipn-lap-2           02 "$(ipn_qs "$PAY2" 500000 00 00 SMOKE-OK-$S)"
ipn 53-ipn-lap-3           02 "$(ipn_qs "$PAY2" 500000 00 00 SMOKE-OK-$S)"
step 54-tong-quan-sau-mua  200 GET  /api/v1/org/billing "" "$THU"
check 54-goi-a-dang-hieu-luc "d['current']['planId']==$PA and d['current']['status']=='ACTIVE' and d['current']['isEffective'] and d['pending'] is None and d['teacherQuota']['max']==6 and d['aiQuota']['max']==120" 54-tong-quan-sau-mua.json
check 54-ngay-hieu-luc "d['current']['endDate'] > d['current']['startDate'] and d['canCancelCurrent'] and d['canActivateFreePlan'] is False" 54-tong-quan-sau-mua.json
step 55-ket-qua-thanh-cong 200 GET  "/api/v1/org/payments/$PAY2" "" "$THU"
check 55-da-thanh-toan "d['outcome']=='PAID' and d['subscription']['planId']==$PA and d['subscription']['status']=='ACTIVE' and d['message'].startswith('Thanh to')" 55-ket-qua-thanh-cong.json
step 56-ds-hoa-don         200 GET  /api/v1/org/invoices "" "$THU"
check 56-mot-hoa-don-da-tra "d['totalItems']==1 and d['items'][0]['status']=='PAID' and d['items'][0]['canPay'] is False" 56-ds-hoa-don.json
step 57-chi-tiet-hoa-don   200 GET  "/api/v1/org/invoices/$INV1" "" "$THU"
check 57-hai-giao-dich "[x['status'] for x in d['payments']]==['SUCCEEDED','FAILED'] and d['payments'][0]['providerTransactionId']=='SMOKE-OK-$S'" 57-chi-tiet-hoa-don.json
step 58-tra-lai-hoa-don-da-tra 422 POST "/api/v1/org/invoices/$INV1/pay" "" "$THU"

# ---- Gia hạn, chặn gia hạn lần hai, hủy ----
step 60-gia-han            201 POST /api/v1/org/billing/checkout checkout-a.json "$THU"
PAY3=$(val 60-gia-han.json paymentId); SUB3=$(val 60-gia-han.json invoice.subscriptionId)
check 60-la-gia-han "d['kind']=='RENEW'" 60-gia-han.json
ipn 61-ipn-gia-han         00 "$(ipn_qs "$PAY3" 500000 00 00 SMOKE-RN-$S)"
step 62-tong-quan-gia-han  200 GET  /api/v1/org/billing "" "$THU"
check 62-co-goi-gia-han-cho "d['upcoming']['status']=='ACTIVE' and d['upcoming']['isEffective'] is False and d['upcoming']['startDate'] > d['current']['endDate'] and d['canCancelCurrent'] is False" 62-tong-quan-gia-han.json
step 63-gia-han-lan-hai    422 POST /api/v1/org/billing/checkout checkout-a.json "$THU"
step 64-huy-goi-gia-han    422 POST "/api/v1/org/subscriptions/$SUB3/cancel" "" "$THU"
step 65-huy-goi-hien-tai   422 POST "/api/v1/org/subscriptions/$SUB1/cancel" "" "$THU"

# ---- Nâng cấp sang gói B: hiệu lực ngay, hủy gói hiện tại và gói gia hạn ----
json checkout-b.json "{\"planId\":$PB}"
step 70-nang-cap           201 POST /api/v1/org/billing/checkout checkout-b.json "$THU"
PAY4=$(val 70-nang-cap.json paymentId); SUB4=$(val 70-nang-cap.json invoice.subscriptionId)
check 70-la-nang-cap "d['kind']=='UPGRADE' and d['invoice']['amount']==1990000" 70-nang-cap.json
payurl_ok 70-dia-chi-vnpay 70-nang-cap.json "p['vnp_Amount']=='199000000'"
ipn 71-ipn-nang-cap        00 "$(ipn_qs "$PAY4" 1990000 00 00 SMOKE-UP-$S)"
step 72-tong-quan-nang-cap 200 GET  /api/v1/org/billing "" "$THU"
check 72-goi-b-hieu-luc-ngay "d['current']['planId']==$PB and d['current']['isEffective'] and d['upcoming'] is None and d['teacherQuota']['max']==20 and d['aiQuota']['max']==1000" 72-tong-quan-nang-cap.json
step 73-hoa-don-admin      200 GET  "/api/v1/admin/invoices?organizationId=$ORG_A&status=PAID" "" "$ADMIN"
check 73-ba-hoa-don-da-tra "d['totalItems']==3" 73-hoa-don-admin.json

# ---- Hủy gói trả phí, gói Miễn phí ----
step 80-huy-goi-b          200 POST "/api/v1/org/subscriptions/$SUB4/cancel" "" "$THU"
check 80-het-goi "d['hasEffectivePlan'] is False and d['canActivateFreePlan'] and d['current']['status']=='CANCELLED'" 80-huy-goi-b.json
step 81-huy-lan-hai        422 POST "/api/v1/org/subscriptions/$SUB4/cancel" "" "$THU"
step 82-kich-hoat-mien-phi 200 POST /api/v1/org/billing/free-plan "" "$THU"
check 82-dang-dung-mien-phi "d['current']['isFree'] and d['hasEffectivePlan'] and d['canActivateFreePlan'] is False" 82-kich-hoat-mien-phi.json
step 83-kich-hoat-lan-hai  422 POST /api/v1/org/billing/free-plan "" "$THU"
step 84-khong-co-hoa-don-moi 200 GET /api/v1/org/invoices "" "$THU"
check 84-van-3-hoa-don "d['totalItems']==3" 84-khong-co-hoa-don-moi.json

# ---- Hủy yêu cầu mua chưa trả ----
step 90-mua-roi-huy        201 POST /api/v1/org/billing/checkout checkout-a.json "$THU"
SUB5=$(val 90-mua-roi-huy.json invoice.subscriptionId); INV5=$(val 90-mua-roi-huy.json invoice.id)
step 91-huy-yeu-cau        200 POST "/api/v1/org/subscriptions/$SUB5/cancel" "" "$THU"
check 91-khong-con-cho "d['pending'] is None and d['current']['isFree']" 91-huy-yeu-cau.json
step 92-hoa-don-da-huy     200 GET  "/api/v1/org/invoices/$INV5" "" "$THU"
check 92-hoa-don-cancelled "d['invoice']['status']=='CANCELLED' and d['payments'][0]['status']=='FAILED'" 92-hoa-don-da-huy.json
step 93-tra-hoa-don-da-huy 422 POST "/api/v1/org/invoices/$INV5/pay" "" "$THU"

# ---- Tắt bán gói ----
step 95-tat-ban-goi-a      200 POST "/api/v1/admin/plans/$PA/disable" "" "$ADMIN"
step 96-mua-goi-da-tat     422 POST /api/v1/org/billing/checkout checkout-a.json "$THU"
checkcode 96-ma-khong-ban PLAN_NOT_ON_SALE 96-mua-goi-da-tat.json
step 97-bat-ban-lai        200 POST "/api/v1/admin/plans/$PA/enable" "" "$ADMIN"
step 98-giao-vien-khong-vao-gioi 403 GET /api/v1/admin/plans "" "$THU"

# ---- Nhật ký thao tác ----
for action in PLAN_UPDATED SUBSCRIPTION_CHANGED BILLING_STATUS_CHANGED IPN_REJECTED; do
  step "99-nhat-ky-$action" 200 GET "/api/v1/admin/audit-logs?action=$action&pageSize=100" "" "$ADMIN"
  check "99-co-$action" "d['totalItems']>=1" "99-nhat-ky-$action.json"
done
check 99-ipn-bi-tu-choi-co-ly-do "sum(1 for i in d['items'] if 'invalid signature' in (i['newValue'] or '') or 'amount mismatch' in (i['newValue'] or '') or 'transaction not found' in (i['newValue'] or ''))>=3" 99-nhat-ky-IPN_REJECTED.json
check 99-khong-lo-khoa "all('$VNPAY_HASH_SECRET' not in (i['newValue'] or '') for i in d['items'])" 99-nhat-ky-IPN_REJECTED.json

echo
echo "Tổng: $PASS đúng, $FAIL sai"
[ "$FAIL" = "0" ]
