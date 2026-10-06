#!/usr/bin/env bash
# Smoke test luồng Đa ngôn ngữ, AI dịch và tóm tắt di sản, nghe thuyết minh trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-ngan-hang-cau-hoi.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file). Luồng này chỉ cần tài khoản quản trị hệ thống
# nên không đọc log thư. Dịch vụ AI chạy bản giả lập (Ai:Mode = Simulated, mặc định), không gọi nhà cung cấp nào. Cần CSDL có sẵn các di sản nạp từ
# doc/du-lieu-di-san ở trạng thái nháp: script tự xuất bản một di sản đủ điều kiện. Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_tam_a2_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Ai__Mode=Simulated Storage__Provider=Local Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5092 > api.log 2>&1 &
#   BASE=http://localhost:5092 ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-da-ngon-ngu.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
set -u
BASE=${BASE:-http://localhost:5080}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-da-ngon-ngu-out}
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

json() { printf '%s' "$2" > "$1"; }

json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json empty.json '{}'
json approve-vi-summary.json '{"kind":"SUMMARY"}'
json approve-en-both.json '{"kind":"BOTH"}'
json approve-en-translation.json '{"kind":"TRANSLATION"}'
json approve-ko.json '{"kind":"BOTH"}'
json reject-en.json '{"kind":"TRANSLATION","reason":"Dịch sai tên riêng."}'
json edit-en.json '{"name":"Imperial Citadel (đã sửa)","shortDescription":"Centre of power of many dynasties."}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# Xuất bản một di sản đủ điều kiện (có giai đoạn, ảnh đại diện, nguồn).
step 02-ds-di-san-nhap     200 GET "/api/v1/admin/heritages?status=DRAFT&pageSize=100" "" "$ADMIN"
CANDIDATES=$(python -c "
import json
d=json.load(open('02-ds-di-san-nhap.json',encoding='utf-8'))['data']['items']
print(' '.join(str(i['id']) for i in d if i['timelineCount']>0))")
H=""
for id in $CANDIDATES; do
  code=$(curl -s -o "pub-$id.json" -w "%{http_code}" -X POST "$BASE/api/v1/admin/heritages/$id/publish" -H "Authorization: Bearer $ADMIN")
  if [ "$code" = "200" ]; then H=$id; break; fi
done
if [ -z "$H" ]; then echo "SAI: không xuất bản được di sản có dòng thời gian (cần doc/du-lieu-di-san)"; exit 1; fi
PASS=$((PASS+1)); printf '| 03-xuat-ban-di-san | POST /api/v1/admin/heritages/{id}/publish | 200 | 200 | ĐÚNG | %s |\n' "$H"
SLUG=$(val "pub-$H.json" slug)
NAME=$(val "pub-$H.json" name)

# ---- Danh sách ngôn ngữ, chưa dịch ----
step 04-ngon-ngu           200 GET /api/v1/public/languages
check 04-nam-ngon-ngu "[l['code'] for l in d]==['vi','en','ko','zh-Hans','ja'] and d[3]['nativeName']=='简体中文'" 04-ngon-ngu.json
step 05-chua-dich-en       200 GET "/api/v1/public/heritages/$SLUG?lang=en"
check 05-lui-ve-tieng-viet "d['localization']['isFallback'] is True and d['localization']['language']=='vi' and d['hasSummary'] is False" 05-chua-dich-en.json
step 06-chua-co-tom-tat    404 GET "/api/v1/public/heritages/$SLUG/summary?lang=en"
checkcode 06-ma-chua-co-tom-tat SUMMARY_UNAVAILABLE 06-chua-co-tom-tat.json
step 07-tong-quan-truoc    200 GET "/api/v1/admin/heritages/$H/translations" "" "$ADMIN"
check 07-chua-co-ban-nao "d['canGenerate'] is True and all(c['status'] is None for c in d['translations']+d['summaries']) and len(d['translations'])==4 and len(d['summaries'])==5" 07-tong-quan-truoc.json
step 08-chua-dang-nhap     401 POST "/api/v1/admin/heritages/$H/translations/generate" empty.json

# ---- Dịch và tóm tắt bằng AI ----
step 10-dich-va-tom-tat    200 POST "/api/v1/admin/heritages/$H/translations/generate" empty.json "$ADMIN"
check 10-trang-thai "next(c for c in d['translations'] if c['language']=='en')['status']=='PENDING' and all(c['status']=='MACHINE' for c in d['translations'] if c['language']!='en') and next(c for c in d['summaries'] if c['language']=='vi')['status']=='PENDING' and next(c for c in d['summaries'] if c['language']=='en')['status']=='PENDING' and all(c['status']=='MACHINE' for c in d['summaries'] if c['language'] in ('ko','zh-Hans','ja'))" 10-dich-va-tom-tat.json
step 11-hang-cho           200 GET "/api/v1/admin/translations?heritageId=$H" "" "$ADMIN"
check 11-ba-muc-cho "d['totalItems']==3" 11-hang-cho.json
step 12-ban-dich-may       200 GET "/api/v1/admin/translations?heritageId=$H&tab=MACHINE&pageSize=100" "" "$ADMIN"
check 12-sau-ban-may "d['totalItems']==6" 12-ban-dich-may.json
step 13-so-dem             200 GET /api/v1/admin/translations/counts "" "$ADMIN"
check 13-dem "d['pending']>=3 and d['machine']>=6" 13-so-dem.json

# ---- Khách xem trước khi duyệt ----
step 20-en-cho-duyet       200 GET "/api/v1/public/heritages/$SLUG?lang=en"
check 20-van-tieng-viet "d['localization']['isFallback'] is True" 20-en-cho-duyet.json
step 21-ko-ban-may         200 GET "/api/v1/public/heritages/$SLUG?lang=ko"
check 21-nhan-ban-dich-may "d['localization']['language']=='ko' and d['localization']['isMachineTranslated'] is True and d['name'].startswith('[ko] ')" 21-ko-ban-may.json
step 22-tieu-de-ngon-ngu   200 GET "/api/v1/public/heritages/$SLUG" "" "" -H "Accept-Language: fr-FR,ja;q=0.8,en;q=0.5"
check 22-chon-ja "d['localization']['language']=='ja'" 22-tieu-de-ngon-ngu.json
step 23-ma-la-ve-tieng-viet 200 GET "/api/v1/public/heritages/$SLUG?lang=zh-TW"
check 23-tieng-viet "d['localization']['language']=='vi' and d['localization']['isFallback'] is False and d['name']==$(python -c "import json,sys;print(json.dumps(sys.argv[1]))" "$NAME")" 23-ma-la-ve-tieng-viet.json
step 24-timeline-ko        200 GET "/api/v1/public/heritages/$SLUG/timeline?lang=ko"
check 24-giai-doan-dich "d['localization']['isMachineTranslated'] is True and d['phases'][0]['name'].startswith('[ko] ')" 24-timeline-ko.json
step 25-tom-tat-chua-duyet 404 GET "/api/v1/public/heritages/$SLUG/summary?lang=ko"

# ---- Duyệt tóm tắt tiếng Việt: bản ngắn để nghe, bản chi tiết để đọc ----
step 30-duyet-tom-tat-vi   200 POST "/api/v1/admin/heritages/$H/translations/vi/approve" approve-vi-summary.json "$ADMIN"
step 31-tom-tat-vi         200 GET "/api/v1/public/heritages/$SLUG/summary?lang=vi"
check 31-ngan-va-chi-tiet "len(d['shortText'])>0 and len(d['detailedText'])>len(d['shortText']) and d['localization']['isFallback'] is False" 31-tom-tat-vi.json
step 32-tom-tat-ko         200 GET "/api/v1/public/heritages/$SLUG/summary?lang=ko"
check 32-ban-may "d['localization']['language']=='ko' and d['localization']['isMachineTranslated'] is True and d['shortText'].startswith('[ko] ')" 32-tom-tat-ko.json
step 33-tom-tat-en-cho     200 GET "/api/v1/public/heritages/$SLUG/summary?lang=en"
check 33-lui-ve-tieng-viet "d['localization']['isFallback'] is True and d['localization']['language']=='vi'" 33-tom-tat-en-cho.json
step 34-co-nut-tom-tat     200 GET "/api/v1/public/heritages/$SLUG"
check 34-has-summary "d['hasSummary'] is True" 34-co-nut-tom-tat.json

# ---- Duyệt tiếng Anh: bản Hàn, Trung, Nhật được dịch lại từ bản đã duyệt ----
step 40-duyet-en           200 POST "/api/v1/admin/heritages/$H/translations/en/approve" approve-en-both.json "$ADMIN"
check 40-dich-lai-ban-may "d['machineTranslationsRefreshed'] is True and d['warning'] is None" 40-duyet-en.json
step 41-en-da-duyet        200 GET "/api/v1/public/heritages/$SLUG?lang=en"
check 41-hien-tieng-anh "d['localization']['isFallback'] is False and d['localization']['isMachineTranslated'] is False and d['name'].startswith('[en] ')" 41-en-da-duyet.json
step 42-ko-tu-ban-en       200 GET "/api/v1/public/heritages/$SLUG?lang=ko"
check 42-dich-tu-tieng-anh "d['name'].startswith('[ko] [en] ')" 42-ko-tu-ban-en.json
step 43-tom-tat-en         200 GET "/api/v1/public/heritages/$SLUG/summary?lang=en"
check 43-tom-tat-tieng-anh "d['localization']['language']=='en' and d['localization']['isFallback'] is False" 43-tom-tat-en.json
step 44-duyet-ko-bi-chan   422 POST "/api/v1/admin/heritages/$H/translations/ko/approve" approve-ko.json "$ADMIN"
step 45-duyet-lai          422 POST "/api/v1/admin/heritages/$H/translations/en/approve" approve-en-translation.json "$ADMIN"

# ---- Tìm kiếm theo bản dịch ----
# Bản dịch giả lập gắn tiền tố mã ngôn ngữ vào chữ ("[zh-Hans] ..."), nên tìm "[zh-Hans]" chỉ ra được di sản có bản tiếng Trung hiện hành, và chỉ khi chọn đúng ngôn ngữ đó.
step 50-tim-theo-ban-dich  200 GET "/api/v1/public/heritages?keyword=%5Bzh-Hans%5D&pageSize=100&lang=zh-Hans"
check 50-thay-ban-trung "any(i['id']==$H and i['isFallback'] is False and i['isMachineTranslated'] is True for i in d['items'])" 50-tim-theo-ban-dich.json
step 51-tim-khong-chon-ngon-ngu 200 GET "/api/v1/public/heritages?keyword=%5Bzh-Hans%5D&pageSize=100"
check 51-khong-doi-ngon-ngu-khong-khop "all(i['id']!=$H for i in d['items'])" 51-tim-khong-chon-ngon-ngu.json
KW=$(python -c "
import sys,urllib.parse
w=[x for x in sys.argv[1].split() if len(x)>=3]
print(urllib.parse.quote(w[0]))" "$NAME")
step 52-tim-tieng-viet     200 GET "/api/v1/public/heritages?keyword=$KW&pageSize=100&lang=ko"
check 52-van-khop-tieng-viet "any(i['id']==$H for i in d['items'])" 52-tim-tieng-viet.json

# ---- Sửa tại chỗ rồi từ chối ----
step 60-sua-ban-dich-en    200 PUT "/api/v1/admin/heritages/$H/translations/en/translation" edit-en.json "$ADMIN"
check 60-ve-cho-duyet "d['status']=='PENDING' and d['translated']['name']=='Imperial Citadel (đã sửa)'" 60-sua-ban-dich-en.json
step 61-tu-choi-en         200 POST "/api/v1/admin/heritages/$H/translations/en/reject" reject-en.json "$ADMIN"
check 61-bi-tu-choi "next(c for c in d['overview']['translations'] if c['language']=='en')['status']=='REJECTED'" 61-tu-choi-en.json
step 62-en-khong-hien      200 GET "/api/v1/public/heritages/$SLUG?lang=en"
check 62-lui-ve-tieng-viet "d['localization']['isFallback'] is True" 62-en-khong-hien.json
step 63-nhat-ky            200 GET "/api/v1/admin/audit-logs?action=TRANSLATION_REJECTED&targetType=heritage_translations&pageSize=5" "" "$ADMIN"
check 63-co-ly-do "any('Dịch sai tên riêng' in (i.get('newValue') or '') for i in d['items'])" 63-nhat-ky.json
step 64-nhat-ky-duyet      200 GET "/api/v1/admin/audit-logs?action=TRANSLATION_APPROVED&pageSize=20" "" "$ADMIN"
check 64-co-duyet "d['totalItems']>=2" 64-nhat-ky-duyet.json

# ---- Sửa nội dung tiếng Việt: cần dịch lại, vẫn hiện bản cũ ----
step 70-chi-tiet-quan-tri  200 GET "/api/v1/admin/heritages/$H" "" "$ADMIN"
python -c "
import json
d=json.load(open('70-chi-tiet-quan-tri.json',encoding='utf-8'))['data']
body={k:d.get(k) for k in ('name','slug','shortDescription','content','heritageType','recognitionLevel','recognizedYear','address','latitude','longitude','placeId')}
body['provinceId']=(d.get('province') or {}).get('id')
body['shortDescription']=(d.get('shortDescription') or '')+' (đã sửa để thử cần dịch lại)'
open('sua-di-san.json','w',encoding='utf-8').write(json.dumps(body,ensure_ascii=False))"
step 71-sua-mo-ta-ngan     200 PUT "/api/v1/admin/heritages/$H" sua-di-san.json "$ADMIN"
step 72-can-dich-lai       200 GET "/api/v1/admin/heritages/$H/translations" "" "$ADMIN"
check 72-chin-ban "d['outdatedCount']==9 and 'cần dịch lại' in d['outdatedNotice']" 72-can-dich-lai.json
step 73-danh-sach-loc      200 GET "/api/v1/admin/heritages?translationState=OUTDATED&pageSize=100" "" "$ADMIN"
check 73-co-di-san "any(i['id']==$H and i['translationState']=='OUTDATED' for i in d['items'])" 73-danh-sach-loc.json
step 74-ko-van-hien        200 GET "/api/v1/public/heritages/$SLUG?lang=ko"
check 74-ban-cu "d['localization']['isFallback'] is False and d['localization']['isMachineTranslated'] is True" 74-ko-van-hien.json
step 75-dich-lai           200 POST "/api/v1/admin/heritages/$H/translations/generate" empty.json "$ADMIN"
check 75-het-co "d['outdatedCount']==0 and next(c for c in d['translations'] if c['language']=='en')['status']=='PENDING'" 75-dich-lai.json
step 76-en-cho-duyet-lai   200 GET "/api/v1/public/heritages/$SLUG?lang=en"
check 76-ban-cu-ngung-hien "d['localization']['isFallback'] is True" 76-en-cho-duyet-lai.json

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
