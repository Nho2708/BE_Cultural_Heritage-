#!/usr/bin/env bash
# Smoke test luồng Khách tham quan trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Luồng này chỉ cần tài khoản quản trị hệ thống (để xuất bản vài di sản) rồi gọi API công khai như một khách: không đăng nhập, mã thiết bị do script tự sinh (UUID phiên bản 4).
# Cần CSDL có sẵn các di sản nạp từ doc/du-lieu-di-san ở trạng thái nháp: script tự xuất bản ba di sản đầu tiên xuất bản được. Dịch vụ AI dùng bản giả lập (Ai:Mode = Simulated).
# Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_tam_a2_smokekhach;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Ai__Mode=Simulated Storage__Provider=Local Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5092 > api.log 2>&1 &
#   API_LOG="$PWD/api.log" BASE=http://localhost:5092 ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-khach-tham-quan.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước), API_LOG (nhật ký của Api: có thì kiểm tọa độ không lọt vào nhật ký).
set -u
BASE=${BASE:-http://localhost:5080}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
API_LOG=${API_LOG:-}
OUT=${SMOKE_OUT:-smoke-khach-tham-quan-out}
export PYTHONIOENCODING=utf-8

mkdir -p "$OUT"
cd "$OUT" || exit 1
PASS=0; FAIL=0

val() { python -c "
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))['data']
for k in sys.argv[2].split('.'): d=d[int(k)] if isinstance(d,list) else d[k]
print(d)" "$1" "$2"; }

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
    print(((d.get('errorCode') or '')+' '+(d.get('message') or '')).strip()[:150])
except Exception: print('(tệp)')" "$name.json")
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

# checkheader <tên> <đường dẫn> <chuỗi phải có trong tiêu đề Cache-Control> <tiêu đề khách thêm...>
checkheader() {
  local name=$1 path=$2 want=$3; shift 3
  local got; got=$(curl -s -o /dev/null -D - "$BASE$path" "$@" | tr -d '\r' | grep -i '^cache-control:' | head -1)
  if printf '%s' "$got" | grep -qi "$want"; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | GET %s | Cache-Control có %s | %s | %s | |\n' "$name" "$path" "$want" "$got" "$r"
}

json() { printf '%s' "$2" > "$1"; }
newkey() { python -c "import uuid; print(uuid.uuid4())"; }

KEY=$(newkey)         # khách chính
KEY2=$(newkey)        # khách thứ hai, để thử không đụng lịch sử của nhau
KEY3=$(newkey)        # khách chưa từng thấy
json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json empty.json '{}'
json cai-dat.json '{"language":"zh-Hans","locationAllowed":true}'
json ngon-ngu-sai.json '{"language":"fr"}'
json ngon-ngu-phon-the.json '{"language":"zh-Hant"}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# Xuất bản tối đa ba di sản đủ điều kiện (có ảnh đại diện, nguồn); ghi lại đường dẫn, id và tọa độ.
step 02-ds-di-san-nhap     200 GET "/api/v1/admin/heritages?status=DRAFT&pageSize=100" "" "$ADMIN"
CANDIDATES=$(python -c "
import json
d=json.load(open('02-ds-di-san-nhap.json',encoding='utf-8'))['data']['items']
print(' '.join(str(i['id']) for i in d))")
IDS=""
for id in $CANDIDATES; do
  code=$(curl -s -o "pub-$id.json" -w "%{http_code}" -X POST "$BASE/api/v1/admin/heritages/$id/publish" -H "Authorization: Bearer $ADMIN")
  if [ "$code" = "200" ]; then IDS="$IDS $id"; fi
  [ "$(echo $IDS | wc -w | tr -d ' ')" = "3" ] && break
done
set -- $IDS
if [ "$#" -lt 3 ]; then echo "SAI: không xuất bản đủ ba di sản (cần doc/du-lieu-di-san)"; exit 1; fi
H1=$1; H2=$2; H3=$3
PASS=$((PASS+1)); printf '| 03-xuat-ban-ba-di-san | POST /api/v1/admin/heritages/{id}/publish | 200 | 200 | ĐÚNG | %s |\n' "$IDS"
SLUG1=$(val "pub-$H1.json" slug); SLUG2=$(val "pub-$H2.json" slug); SLUG3=$(val "pub-$H3.json" slug)
# Di sản có tọa độ để thử "Gần bạn": lấy tọa độ chính của di sản đó làm vị trí khách.
read -r NEARSLUG NEARLAT NEARLON < <(python -c "
import json
for h in '$H1 $H2 $H3'.split():
    d=json.load(open('pub-%s.json'%h,encoding='utf-8'))['data']
    if d.get('latitude') is not None and d.get('longitude') is not None:
        print(d['slug'], d['latitude'], d['longitude']); break
else:
    print('- 21.033 105.85')" | tr -d '\r')

G=/api/v1/public/guest

# ---- Mã thiết bị ----
step 10-lich-su-thieu-ma   400 GET "$G/history"
checkcode 10-ma-required REQUIRED 10-lich-su-thieu-ma.json
step 11-lich-su-ma-sai     400 GET "$G/history" "" "" -H "X-Device-Key: khong-phai-uuid"
checkcode 11-ma-invalid INVALID 11-lich-su-ma-sai.json
step 12-ma-phien-ban-1     400 GET "$G/device" "" "" -H "X-Device-Key: 3f2b8c1e-9a47-1d6b-8e10-5c7a9b2d4f61"
step 13-nen-tang-sai       400 POST "$G/devices" empty.json "" -H "X-Device-Key: $KEY" -H "X-Client-Platform: NOKIA"

# ---- Đăng ký, cài đặt ----
step 20-may-chu-cap-ma     201 POST "$G/devices" empty.json ""
check 20-ma-do-may-chu-cap "len(d['deviceKey'])==36 and d['deviceKey'][14]=='4' and d['isRegistered'] is True and d['platform']=='WEB' and d['language']=='vi'" 20-may-chu-cap-ma.json
step 21-dang-ky-ma-tu-sinh 201 POST "$G/devices" empty.json "" -H "X-Device-Key: $KEY" -H "X-Client-Platform: android"
check 21-nen-tang-android "d['deviceKey']=='$KEY' and d['platform']=='ANDROID'" 21-dang-ky-ma-tu-sinh.json
step 22-dang-ky-lai        200 POST "$G/devices" empty.json "" -H "X-Device-Key: $KEY"
step 23-doc-ma-chua-thay   200 GET "$G/device?lang=ja" "" "" -H "X-Device-Key: $KEY3"
check 23-khong-tao-ban-ghi "d['isRegistered'] is False and d['language']=='ja' and d['createdAt'] is None" 23-doc-ma-chua-thay.json
step 24-doi-cai-dat        200 PUT "$G/device" cai-dat.json "" -H "X-Device-Key: $KEY"
check 24-ngon-ngu-tiep-trung "d['language']=='zh-Hans' and d['locationAllowed'] is True and d['platform']=='ANDROID'" 24-doi-cai-dat.json
step 25-ngon-ngu-sai       400 PUT "$G/device" ngon-ngu-sai.json "" -H "X-Device-Key: $KEY"
step 26-trung-phon-the     400 PUT "$G/device" ngon-ngu-phon-the.json "" -H "X-Device-Key: $KEY"
step 27-doc-lai-cai-dat    200 GET "$G/device" "" "" -H "X-Device-Key: $KEY"
check 27-cai-dat-con-nguyen "d['language']=='zh-Hans' and d['locationAllowed'] is True" 27-doc-lai-cai-dat.json

# ---- Tự ghi lịch sử khi gọi API công khai kèm mã thiết bị ----
step 30-xem-khong-ma       200 GET "/api/v1/public/heritages/$SLUG1"
step 31-lich-su-chua-co    200 GET "$G/history" "" "" -H "X-Device-Key: $KEY"
check 31-chua-ghi-gi-khi-khong-ma "d['totalItems']==0" 31-lich-su-chua-co.json
step 32-tim-co-ma          200 GET "/api/v1/public/heritages?keyword=van" "" "" -H "X-Device-Key: $KEY"
step 33-xem-chi-tiet-co-ma 200 GET "/api/v1/public/heritages/$SLUG1?lang=en" "" "" -H "X-Device-Key: $KEY"
step 34-xem-lai-trong-10-phut 200 GET "/api/v1/public/heritages/$SLUG1" "" "" -H "X-Device-Key: $KEY"
step 35-xem-di-san-hai     200 GET "/api/v1/public/heritages/$SLUG2" "" "" -H "X-Device-Key: $KEY"
step 36-xem-di-san-nhap    404 GET "/api/v1/public/heritages/khong-co-di-san-nay" "" "" -H "X-Device-Key: $KEY"
step 37-tom-tat-chua-co    404 GET "/api/v1/public/heritages/$SLUG1/summary" "" "" -H "X-Device-Key: $KEY"
json nghe.json "{\"type\":\"LISTEN\",\"slug\":\"$SLUG1\"}"
step 38-ghi-tay-nghe       200 POST "$G/activities?lang=ko" nghe.json "" -H "X-Device-Key: $KEY"
check 38-da-ghi "d['recorded'] is True and d['reason'] is None" 38-ghi-tay-nghe.json
step 39-nghe-lap-lai       200 POST "$G/activities" nghe.json "" -H "X-Device-Key: $KEY"
check 39-bo-luot-lap "d['recorded'] is False and d['reason']=='DUPLICATE'" 39-nghe-lap-lai.json
json nghe-sai.json '{"type":"LISTEN","slug":"khong-co-di-san-nay"}'
step 40-ghi-di-san-la      404 POST "$G/activities" nghe-sai.json "" -H "X-Device-Key: $KEY"
json tim-ngan.json '{"type":"SEARCH","query":"a"}'
step 41-tu-khoa-ngan       400 POST "$G/activities" tim-ngan.json "" -H "X-Device-Key: $KEY"
json loai-sai.json "{\"type\":\"DANCE\",\"slug\":\"$SLUG1\"}"
step 42-loai-sai           400 POST "$G/activities" loai-sai.json "" -H "X-Device-Key: $KEY"

# ---- Xem lịch sử ----
step 50-lich-su            200 GET "$G/history?lang=en" "" "" -H "X-Device-Key: $KEY"
check 50-nam-luot-moi-nhat-truoc "d['totalItems']==4 and [i['type'] for i in d['items']]==['LISTEN','VIEW','VIEW','SEARCH'] and d['items'][0]['heritage']['slug']=='$SLUG1' and d['items'][3]['query']=='van' and d['items'][3]['heritage'] is None" 50-lich-su.json
check 50-co-nhan-tieng-viet "d['items'][0]['typeLabel']=='Nghe thuyết minh' and d['items'][3]['typeLabel']=='Tìm kiếm'" 50-lich-su.json
step 51-phan-trang         200 GET "$G/history?pageSize=2&page=2" "" "" -H "X-Device-Key: $KEY"
check 51-trang-hai "d['totalPages']==2 and len(d['items'])==2" 51-phan-trang.json
step 52-trang-qua-lon      400 GET "$G/history?pageSize=101" "" "" -H "X-Device-Key: $KEY"
step 53-thiet-bi-khac-rong 200 GET "$G/history" "" "" -H "X-Device-Key: $KEY2"
check 53-khong-lo-lich-su "d['totalItems']==0" 53-thiet-bi-khac-rong.json
checkheader 54-khong-luu-dem "$G/history" no-store -H "X-Device-Key: $KEY"

# ---- Gợi ý cho bạn ----
step 60-goi-y              200 GET "$G/recommendations?lang=en" "" "" -H "X-Device-Key: $KEY"
# Chỉ ba di sản được xuất bản: nếu di sản còn lại không giống hai di sản đã xem thì gợi ý quay về di sản nổi bật (isPersonalized = false). Khi đã cá nhân hóa thì không lặp di sản đã xem và mỗi mục có lý do.
check 60-goi-y-hop-le "len(d['items'])<=6 and d['usedFallback'] is False and (d['isPersonalized'] is False or all(i['heritage']['slug'] not in ['$SLUG1','$SLUG2'] and len(i['reasons'])>0 and i['becauseOfName'] for i in d['items']))" 60-goi-y.json
step 61-khong-gui-ma       200 GET "$G/recommendations"
check 61-di-san-noi-bat "d['isPersonalized'] is False and d['usedFallback'] is False and len(d['items'])<=6" 61-khong-gui-ma.json
step 62-ma-chua-thay       200 GET "$G/recommendations" "" "" -H "X-Device-Key: $KEY3"
check 62-moi-xem-thu "d['isPersonalized'] is False and d['message'] is not None" 62-ma-chua-thay.json

# ---- Gần bạn ----
python -c "
import json
json.dump({'latitude': float('$NEARLAT'), 'longitude': float('$NEARLON')}, open('vi-tri-di-san.json','w'))
json.dump({'latitude': 16.123456, 'longitude': 107.654321}, open('vi-tri-rieng.json','w'))
json.dump({'latitude': 35.68, 'longitude': 139.69}, open('vi-tri-tokyo.json','w'))
json.dump({'latitude': 91.0, 'longitude': 105.0}, open('vi-tri-sai.json','w'))
json.dump({'latitude': 21.0}, open('vi-tri-thieu.json','w'))"
step 70-gan-ban            200 POST "$G/nearby" vi-tri-di-san.json ""
check 70-di-san-gan-nhat "len(d['items'])>=1 and len(d['items'])<=6 and d['unavailableReason'] is None and d['items'][0]['distanceMeters']<=120 and [i['distanceMeters'] for i in d['items']]==sorted(i['distanceMeters'] for i in d['items'])" 70-gan-ban.json
step 71-gan-ban-tieng-anh  200 POST "$G/nearby?lang=en" vi-tri-di-san.json ""
check 71-chu-khoang-cach "d['items'][0]['distanceText'].endswith(' m') or d['items'][0]['distanceText'].endswith(' km')" 71-gan-ban-tieng-anh.json
step 72-ngoai-viet-nam     200 POST "$G/nearby" vi-tri-tokyo.json ""
check 72-khong-hien-danh-sach "d['items']==[] and d['unavailableReason']=='OUTSIDE_VIETNAM' and d['message'] is not None" 72-ngoai-viet-nam.json
step 73-toa-do-sai         400 POST "$G/nearby" vi-tri-sai.json ""
step 74-thieu-toa-do       400 POST "$G/nearby" vi-tri-thieu.json ""
checkcode 74-ma-required REQUIRED 74-thieu-toa-do.json
step 75-gan-ban-co-ma      200 POST "$G/nearby" vi-tri-rieng.json "" -H "X-Device-Key: $KEY"
step 76-lich-su-khong-doi  200 GET "$G/history" "" "" -H "X-Device-Key: $KEY"
check 76-vi-tri-khong-vao-lich-su "d['totalItems']==4" 76-lich-su-khong-doi.json
if [ -n "$API_LOG" ] && [ -f "$API_LOG" ]; then
  if grep -q -e "16.123456" -e "107.654321" "$API_LOG"; then FAIL=$((FAIL+1)); r=SAI; else PASS=$((PASS+1)); r=ĐÚNG; fi
  printf '| 77-toa-do-khong-vao-nhat-ky | grep tọa độ riêng trong %s | không có | %s | %s | |\n' "$API_LOG" "$r" "$r"
fi

# ---- Xóa lịch sử ----
python -c "
import json
items=json.load(open('50-lich-su.json',encoding='utf-8'))['data']['items']
open('id-muc-dau.txt','w').write(str(items[0]['id']))"
ID1=$(cat id-muc-dau.txt)
step 80-xoa-muc-cua-nguoi-khac 404 DELETE "$G/history/$ID1" "" "" -H "X-Device-Key: $KEY2"
step 81-xoa-muc-khong-co   404 DELETE "$G/history/999999999" "" "" -H "X-Device-Key: $KEY"
step 82-xoa-mot-muc        200 DELETE "$G/history/$ID1" "" "" -H "X-Device-Key: $KEY"
step 83-con-ba-muc         200 GET "$G/history" "" "" -H "X-Device-Key: $KEY"
check 83-da-xoa-that "d['totalItems']==3 and all(i['id']!=$ID1 for i in d['items'])" 83-con-ba-muc.json
step 84-xoa-het            200 DELETE "$G/history" "" "" -H "X-Device-Key: $KEY"
check 84-xoa-ba-muc "d['deleted']==3" 84-xoa-het.json
step 85-lich-su-rong       200 GET "$G/history" "" "" -H "X-Device-Key: $KEY"
check 85-rong "d['totalItems']==0" 85-lich-su-rong.json
step 86-goi-y-ve-mac-dinh  200 GET "$G/recommendations" "" "" -H "X-Device-Key: $KEY"
check 86-di-san-noi-bat-va-moi-xem-thu "d['isPersonalized'] is False and d['message'] is not None" 86-goi-y-ve-mac-dinh.json
step 87-cai-dat-con-nguyen 200 GET "$G/device" "" "" -H "X-Device-Key: $KEY"
check 87-van-nho-lua-chon-gan-ban "d['isRegistered'] is True and d['locationAllowed'] is True" 87-cai-dat-con-nguyen.json
step 88-xoa-het-ma-chua-thay 200 DELETE "$G/history" "" "" -H "X-Device-Key: $KEY3"
check 88-khong-co-gi-de-xoa "d['deleted']==0" 88-xoa-het-ma-chua-thay.json

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
