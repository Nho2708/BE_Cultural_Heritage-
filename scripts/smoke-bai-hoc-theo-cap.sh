#!/usr/bin/env bash
# Smoke test luồng Bài học cho lớp theo cấp, AI soạn nội dung, mã bài học trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-lop-hoc-sinh.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file), liên kết đặt mật khẩu lấy từ log của API
# (Email:Mode = Log; nếu User Secrets đang để Smtp thì đặt Email__Mode=Log khi chạy API để không gửi thư thật). Dịch vụ AI chạy bản giả lập (Ai:Mode = Simulated,
# mặc định), không gọi nhà cung cấp nào. Cần CSDL có sẵn các di sản nạp từ doc/du-lieu-di-san ở trạng thái nháp: script tự xuất bản hai di sản đủ điều kiện.
# Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5091 > api.log 2>&1 &
#   BASE=http://localhost:5091 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-bai-hoc-theo-cap.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
# Lưu ý: script cố ý nhập sai mã bài học 12 lần; chạy lần thứ ba trong vòng 10 phút trên cùng một API thì giới hạn 30 lần sai mỗi IP (cả lớp chung IP)
# chặn nhập mã 15 phút và các bước "sai mã" trả 429 thay vì 404. Khởi động lại API (bộ đếm nằm trong bộ nhớ) hoặc chờ rồi chạy lại.
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-bai-hoc-out}
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

json() { printf '%s' "$2" > "$1"; }

# Biến phản hồi tạo lời mời dựng từ bản nháp AI thành nội dung lưu: tóm tắt, ý chính, các phần.
draft_to_update() { python -c "
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))['data']
body={'summary':d['summary'],'keyPoints':d['keyPoints'],'sections':[
  {'type':s['type'],'title':s['title'],'body':s['body'],'imageUrl':s['imageUrl'],'milestoneYear':s['milestoneYear'],'eventId':s['eventId'],'sourceCitation':s['sourceCitation']}
  for s in d['sections']]}
json.dump(body,open(sys.argv[2],'w',encoding='utf-8'),ensure_ascii=False)" "$1" "$2"; }

json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json org.json "{\"name\":\"Trường THCS Nguyễn Du $S\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@nguyendu.edu.vn\"}}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S\",\"password\":\"$PW\"}"
json teacher.json "{\"fullName\":\"Đặng Thị Mỹ Linh\",\"username\":\"linh.$S\",\"email\":\"linh.$S@nguyendu.edu.vn\"}"
json login-linh.json "{\"emailOrUsername\":\"linh.$S\",\"password\":\"$PW\"}"
json class1.json '{"name":"3A","grade":3,"schoolYear":"2026-2027"}'
json class2.json '{"name":"8A1","grade":8,"schoolYear":"2026-2027"}'
json class3.json '{"name":"11A2","grade":11,"schoolYear":"2026-2027"}'
json student.json '{"fullName":"Nguyễn Văn Đạt"}'
json length-short.json '{"length":"SHORT"}'
json length-detailed.json '{"length":"DETAILED"}'
json length-bad.json '{"length":"HUGE"}'
json empty.json '{}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# Xuất bản hai di sản đủ điều kiện (có giai đoạn, ảnh đại diện, nguồn) để bài học dựng từ di sản đã xuất bản.
step 02-ds-di-san-nhap     200 GET "/api/v1/admin/heritages?status=DRAFT&pageSize=100" "" "$ADMIN"
CANDIDATES=$(python -c "
import json
d=json.load(open('02-ds-di-san-nhap.json',encoding='utf-8'))['data']['items']
print(' '.join(str(i['id']) for i in d if i['timelineCount']>0))")
H=()
for id in $CANDIDATES; do
  [ ${#H[@]} -ge 2 ] && break
  code=$(curl -s -o "pub-$id.json" -w "%{http_code}" -X POST "$BASE/api/v1/admin/heritages/$id/publish" -H "Authorization: Bearer $ADMIN")
  if [ "$code" = "200" ]; then H+=("$id"); fi
done
if [ ${#H[@]} -lt 2 ]; then echo "SAI: không xuất bản được hai di sản có dòng thời gian (cần doc/du-lieu-di-san)"; exit 1; fi
PASS=$((PASS+1)); printf '| 03-xuat-ban-2-di-san | POST /api/v1/admin/heritages/{id}/publish | 200 | 200 | ĐÚNG | %s %s |\n' "${H[0]}" "${H[1]}"

step 04-tao-truong         201 POST /api/v1/admin/organizations org.json "$ADMIN"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 05-qt-truong-dat-mk   200 POST /api/v1/auth/set-password set-thu.json
step 06-qt-truong-login    200 POST /api/v1/auth/login login-thu.json
THU=$(val 06-qt-truong-login.json accessToken)
step 07-tao-giao-vien      201 POST /api/v1/org/teachers teacher.json "$THU"
json set-linh.json "{\"token\":\"$(mail_token "linh.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 08-gv-dat-mk          200 POST /api/v1/auth/set-password set-linh.json
step 09-gv-login           200 POST /api/v1/auth/login login-linh.json
LINH=$(val 09-gv-login.json accessToken)

step 10-tao-lop-cap-1      201 POST /api/v1/teacher/classes class1.json "$LINH"
C1=$(val 10-tao-lop-cap-1.json id)
step 11-tao-lop-cap-2      201 POST /api/v1/teacher/classes class2.json "$LINH"
C2=$(val 11-tao-lop-cap-2.json id)
step 12-tao-lop-cap-3      201 POST /api/v1/teacher/classes class3.json "$LINH"
C3=$(val 12-tao-lop-cap-3.json id)

# ---- Giáo viên soạn bài cho từng cấp ----
step 20-han-muc-ai         200 GET /api/v1/teacher/lessons/ai-quota "" "$LINH"
LESSONS=()
CODES=()
STUDENT_TOKENS=()
n=0
for C in "$C1" "$C2" "$C3"; do
  n=$((n+1))
  json "create-$n.json" "{\"classId\":$C,\"title\":\"Di sản qua lời kể lớp cấp $n\",\"heritageIds\":[${H[0]},${H[1]}]}"
  step "3$n-tao-bai-cap-$n"   201 POST /api/v1/teacher/lessons "create-$n.json" "$LINH"
  LES=$(val "3$n-tao-bai-cap-$n.json" id); LESSONS+=("$LES")
  check "3$n-tao-bai-nhap-cap-$n" "d['status']=='DRAFT' and d['accessCode'] is None and d['schoolLevel']==$n and d['readiness']['isReady'] is False" "3$n-tao-bai-cap-$n.json"
  step "3$n-chua-xuat-ban-$n"  422 POST "/api/v1/teacher/lessons/$LES/publish" "" "$LINH"
  step "3$n-ai-soan-cap-$n"    200 POST "/api/v1/teacher/lessons/$LES/ai/compose" length-detailed.json "$LINH"
  check "3$n-ai-dung-cap-$n" "d['level']==$n and d['wordCount']>=[150,300,600][$n-1] and d['wordCount']<=[250,500,900][$n-1] and len(d['sections'])>0" "3$n-ai-soan-cap-$n.json"
  draft_to_update "3$n-ai-soan-cap-$n.json" "update-$n.json"
  step "3$n-luu-ban-nhap-$n"   200 PUT "/api/v1/teacher/lessons/$LES" "update-$n.json" "$LINH"
  check "3$n-san-sang-xuat-ban-$n" "d['readiness']['isReady'] is True" "3$n-luu-ban-nhap-$n.json"
  step "3$n-xuat-ban-$n"       200 POST "/api/v1/teacher/lessons/$LES/publish" "" "$LINH"
  CODE=$(val "3$n-xuat-ban-$n.json" accessCode); CODES+=("$CODE")
  check "3$n-ma-dung-dang-$n" "d['codeCreated'] is True and d['accessCode'].startswith('BH') and len(d['accessCode'])==8" "3$n-xuat-ban-$n.json"

  # Học sinh của lớp: thêm, đăng nhập, đổi mật khẩu lần đầu.
  step "4$n-them-hoc-sinh-$n"  201 POST "/api/v1/teacher/classes/$C/students" student.json "$LINH"
  SC=$(val "4$n-them-hoc-sinh-$n.json" sheet.entries.0.studentCode)
  ST=$(val "4$n-them-hoc-sinh-$n.json" sheet.entries.0.temporaryPassword)
  json "hs-login-$n.json" "{\"studentCode\":\"$SC\",\"password\":\"$ST\"}"
  step "4$n-hs-login-$n"       200 POST /api/v1/student/auth/login "hs-login-$n.json"
  HS1=$(val "4$n-hs-login-$n.json" accessToken)
  json "hs-pw-$n.json" "{\"newPassword\":\"$NEWPW\",\"confirmNewPassword\":\"$NEWPW\"}"
  step "4$n-hs-doi-mk-$n"      200 PUT /api/v1/student/me/password "hs-pw-$n.json" "$HS1"
  STUDENT_TOKENS+=("$(val "4$n-hs-doi-mk-$n.json" accessToken)")
done

# ---- Học sinh mở bài bằng mã ----
n=0
for T in "${STUDENT_TOKENS[@]}"; do
  n=$((n+1)); CODE=${CODES[$((n-1))]}
  lower=$(echo "$CODE" | tr 'A-Z' 'a-z')
  typed="${lower:0:2}%20${lower:2:3}-${lower:5:3}"
  step "5$n-hs-mo-bai-$n"      200 GET "/api/v1/student/lessons/$CODE" "" "$T"
  check "5$n-bai-dung-cap-$n" "d['schoolLevel']==$n and d['layout']['layout']==['STEP_BY_STEP','FREE_STEPS','DOCUMENT'][$n-1] and len(d['heritages'])==2 and len(d['sections'])>0" "5$n-hs-mo-bai-$n.json"
  step "5$n-hs-go-ma-thuong-$n" 200 GET "/api/v1/student/lessons/$typed" "" "$T"
done
check "55-cap-1-co-chuyen-va-hinh" "all(s['type'] in ('STORY','IMAGE') for s in d['sections']) and any(s['type']=='STORY' and s['imageUrl'] for s in d['sections'])" 51-hs-mo-bai-1.json
check "55-cap-3-co-trich-nguon" "any(s['type']=='SOURCE_EXCERPT' and s['sourceCitation'] for s in d['sections']) and not any(s['type'] in ('STORY','IMAGE') for s in d['sections'])" 53-hs-mo-bai-3.json
# Mã của lớp khác hoặc không tồn tại: cùng một thông báo 404.
step 56-ma-lop-khac        404 GET "/api/v1/student/lessons/${CODES[1]}" "" "${STUDENT_TOKENS[0]}"
step 57-ma-khong-ton-tai   404 GET "/api/v1/student/lessons/BH222222" "" "${STUDENT_TOKENS[0]}"
step 58-chua-dang-nhap     401 GET "/api/v1/student/lessons/${CODES[0]}" ""
step 59-gv-khong-mo-ma     403 GET "/api/v1/student/lessons/${CODES[0]}" "" "$LINH"

# ---- Sửa, lưu trữ, xuất bản lại, nhân bản, xóa ----
FIRST_LESSON=${LESSONS[0]}
json rename.json '{"title":"Di sản qua lời kể (đã sửa)"}'
step 60-sua-tieu-de        200 PUT "/api/v1/teacher/lessons/$FIRST_LESSON" rename.json "$LINH"
check 61-giu-ma-khi-sua "d['accessCode']=='${CODES[0]}'" 60-sua-tieu-de.json
json empty-summary.json '{"summary":"  "}'
step 62-bo-tom-tat-bi-chan 422 PUT "/api/v1/teacher/lessons/$FIRST_LESSON" empty-summary.json "$LINH"
step 63-ds-bai-hoc         200 GET "/api/v1/teacher/lessons?pageSize=50" "" "$LINH"
step 64-qt-truong-bi-chan  403 GET "/api/v1/teacher/lessons/$FIRST_LESSON" "" "$THU"
step 65-kho-anh-su-kien    200 GET "/api/v1/teacher/lessons/$FIRST_LESSON/materials" "" "$LINH"
step 66-luu-tru            200 POST "/api/v1/teacher/lessons/$FIRST_LESSON/archive" "" "$LINH"
step 67-luu-tru-hs-het-mo  404 GET "/api/v1/student/lessons/${CODES[0]}" "" "${STUDENT_TOKENS[0]}"
step 68-sua-bai-luu-tru    422 PUT "/api/v1/teacher/lessons/$FIRST_LESSON" rename.json "$LINH"
step 69-ai-bai-luu-tru     422 POST "/api/v1/teacher/lessons/$FIRST_LESSON/ai/compose" empty.json "$LINH"
step 70-xuat-ban-lai       200 POST "/api/v1/teacher/lessons/$FIRST_LESSON/publish" "" "$LINH"
check 71-ma-giu-nguyen "d['accessCode']=='${CODES[0]}' and d['codeCreated'] is False" 70-xuat-ban-lai.json
step 72-hs-mo-lai          200 GET "/api/v1/student/lessons/${CODES[0]}" "" "${STUDENT_TOKENS[0]}"
json dup.json "{\"targetClassId\":$C2}"
json dup-same.json "{\"targetClassId\":$C1}"
step 73-nhan-ban-cung-lop  400 POST "/api/v1/teacher/lessons/$FIRST_LESSON/duplicate" dup-same.json "$LINH"
step 74-nhan-ban-lop-khac  201 POST "/api/v1/teacher/lessons/$FIRST_LESSON/duplicate" dup.json "$LINH"
check 75-ban-chep-nhap-co-nhac-cap "d['lesson']['status']=='DRAFT' and d['lesson']['accessCode'] is None and d['levelNotice'] is not None" 74-nhan-ban-lop-khac.json
COPY=$(val 74-nhan-ban-lop-khac.json lesson.id)
step 76-xoa-ban-chep       200 DELETE "/api/v1/teacher/lessons/$COPY" "" "$LINH"
step 77-ai-do-dai-sai      400 POST "/api/v1/teacher/lessons/${LESSONS[1]}/ai/compose" length-bad.json "$LINH"
step 78-ai-tao-lai         200 POST "/api/v1/teacher/lessons/${LESSONS[1]}/ai/compose" length-short.json "$LINH"
step 79-han-muc-sau-ai     200 GET /api/v1/teacher/lessons/ai-quota "" "$LINH"
check 80-da-dung-4-luot "d['used']==4 and d['remaining']==d['max']-4" 79-han-muc-sau-ai.json

# ---- Giới hạn nhập sai mã: 10 lần sai trong 10 phút thì bị chặn nhập mã ----
for LES in A B C D E F G H J; do step "90-sai-$LES" 404 GET "/api/v1/student/lessons/BH22222$LES" "" "${STUDENT_TOKENS[2]}"; done
step 91-sai-lan-10-bi-chan 429 GET "/api/v1/student/lessons/BH22222Z" "" "${STUDENT_TOKENS[2]}"
step 92-ma-dung-van-chan   429 GET "/api/v1/student/lessons/${CODES[2]}" "" "${STUDENT_TOKENS[2]}"
step 93-hs-khac-van-mo-duoc 200 GET "/api/v1/student/lessons/${CODES[1]}" "" "${STUDENT_TOKENS[1]}"
step 94-chan-ma-van-xem-lop 200 GET /api/v1/student/class "" "${STUDENT_TOKENS[2]}"

# ---- Nhật ký: lượt gọi AI nằm ở ai_generations, không vào audit_logs; mật khẩu và mã không lộ trong nhật ký thao tác ----
step 95-nhat-ky            200 GET "/api/v1/admin/audit-logs?pageSize=100" "" "$ADMIN"
for code in "${CODES[@]}"; do
  if grep -q "$code" 95-nhat-ky.json; then echo "SAI: mã bài học xuất hiện trong nhật ký thao tác"; FAIL=$((FAIL+1)); fi
done
if grep -q "$NEWPW" 95-nhat-ky.json; then echo "SAI: mật khẩu xuất hiện trong nhật ký"; FAIL=$((FAIL+1)); fi

echo
echo "Hậu tố dữ liệu: $S · lớp id=$C1,$C2,$C3 · bài học id=${LESSONS[*]} · mã bài học=${CODES[*]}"
echo "Kết quả: $PASS/$((PASS+FAIL)) bước đúng. Phản hồi từng bước: $(pwd)"
[ "$FAIL" -eq 0 ]
