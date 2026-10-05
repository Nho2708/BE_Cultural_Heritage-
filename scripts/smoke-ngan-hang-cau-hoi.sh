#!/usr/bin/env bash
# Smoke test luồng Ngân hàng câu hỏi theo dạng, AI sinh câu hỏi trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-bai-hoc-theo-cap.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file), liên kết đặt mật khẩu lấy từ log của API
# (Email:Mode = Log; nếu User Secrets đang để Smtp thì đặt Email__Mode=Log khi chạy API để không gửi thư thật). Dịch vụ AI chạy bản giả lập (Ai:Mode = Simulated,
# mặc định), không gọi nhà cung cấp nào. Cần CSDL có sẵn các di sản nạp từ doc/du-lieu-di-san ở trạng thái nháp: script tự xuất bản một di sản đủ điều kiện và thêm ảnh
# vào thư viện ảnh của di sản đó. Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5092 > api.log 2>&1 &
#   BASE=http://localhost:5092 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-ngan-hang-cau-hoi.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
# Bài kiểm tra chưa có API ở nhánh này nên script không kiểm phần "câu nằm trong bài kiểm tra thì chỉ đọc" (có trong bộ test tích hợp).
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-cau-hoi-out}
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

# checkerr <tên> <điều kiện python trên e = trường errors của tệp> <tệp phản hồi>
checkerr() {
  local name=$1 cond=$2 file=$3
  local ok; ok=$(python -c "
import json,sys
e=json.load(open(sys.argv[2],encoding='utf-8')).get('errors') or {}
print('1' if eval(sys.argv[1]) else '0')" "$cond" "$file" 2>/dev/null)
  if [ "$ok" = "1" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | kiểm lỗi theo trường %s | đúng | %s | %s | %s |\n' "$name" "$file" "$ok" "$r" "$cond"
}

json() { printf '%s' "$2" > "$1"; }

json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json org.json "{\"name\":\"Trường THCS Chu Văn An $S\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@chuvanan.edu.vn\"}}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S\",\"password\":\"$PW\"}"
json teacher1.json "{\"fullName\":\"Đặng Thị Mỹ Linh\",\"username\":\"linh.$S\",\"email\":\"linh.$S@chuvanan.edu.vn\"}"
json teacher2.json "{\"fullName\":\"Phạm Văn Hải\",\"username\":\"hai.$S\",\"email\":\"hai.$S@chuvanan.edu.vn\"}"
json login-linh.json "{\"emailOrUsername\":\"linh.$S\",\"password\":\"$PW\"}"
json login-hai.json "{\"emailOrUsername\":\"hai.$S\",\"password\":\"$PW\"}"
json class1.json '{"name":"3A","grade":3,"schoolYear":"2026-2027"}'
json class2.json '{"name":"8A1","grade":8,"schoolYear":"2026-2027"}'
json class3.json '{"name":"11A2","grade":11,"schoolYear":"2026-2027"}'
json student.json '{"fullName":"Nguyễn Văn Đạt"}'
json empty.json '{}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# Xuất bản một di sản đủ điều kiện (có giai đoạn, ảnh đại diện, nguồn) rồi thêm ảnh vào thư viện ảnh của di sản để dựng được câu có hình.
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
for i in 1 2 3; do
  json "media-$i.json" "{\"mediaType\":\"IMAGE\",\"url\":\"https://upload.wikimedia.org/smoke-$S-$i.jpg\",\"caption\":\"Ảnh thêm số $i\",\"author\":\"Nguyễn Văn An\",\"license\":\"CC BY-SA 4.0\",\"source\":\"Wikimedia Commons\"}"
  step "03-them-anh-$i"      201 POST "/api/v1/admin/heritages/$H/media" "media-$i.json" "$ADMIN"
done

step 04-tao-truong         201 POST /api/v1/admin/organizations org.json "$ADMIN"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@chuvanan.edu.vn")\",\"newPassword\":\"$PW\"}"
step 05-qt-truong-dat-mk   200 POST /api/v1/auth/set-password set-thu.json
step 06-qt-truong-login    200 POST /api/v1/auth/login login-thu.json
THU=$(val 06-qt-truong-login.json accessToken)
step 07-tao-giao-vien-1    201 POST /api/v1/org/teachers teacher1.json "$THU"
json set-linh.json "{\"token\":\"$(mail_token "linh.$S@chuvanan.edu.vn")\",\"newPassword\":\"$PW\"}"
step 08-gv1-dat-mk         200 POST /api/v1/auth/set-password set-linh.json
step 09-gv1-login          200 POST /api/v1/auth/login login-linh.json
LINH=$(val 09-gv1-login.json accessToken); LINH_ID=$(val 09-gv1-login.json user.id)
step 10-tao-giao-vien-2    201 POST /api/v1/org/teachers teacher2.json "$THU"
json set-hai.json "{\"token\":\"$(mail_token "hai.$S@chuvanan.edu.vn")\",\"newPassword\":\"$PW\"}"
step 11-gv2-dat-mk         200 POST /api/v1/auth/set-password set-hai.json
step 12-gv2-login          200 POST /api/v1/auth/login login-hai.json
HAI=$(val 12-gv2-login.json accessToken)

step 13-tao-lop-cap-1      201 POST /api/v1/teacher/classes class1.json "$LINH"; C1=$(val 13-tao-lop-cap-1.json id)
step 14-tao-lop-cap-2      201 POST /api/v1/teacher/classes class2.json "$LINH"; C2=$(val 14-tao-lop-cap-2.json id)
step 15-tao-lop-cap-3      201 POST /api/v1/teacher/classes class3.json "$LINH"; C3=$(val 15-tao-lop-cap-3.json id)
LES=()
n=0
for C in "$C1" "$C2" "$C3"; do
  n=$((n+1))
  json "lesson-$n.json" "{\"classId\":$C,\"title\":\"Di sản qua câu hỏi cấp $n\",\"heritageIds\":[$H]}"
  step "16-tao-bai-cap-$n"   201 POST /api/v1/teacher/lessons "lesson-$n.json" "$LINH"
  LES+=("$(val "16-tao-bai-cap-$n.json" id)")
done
step 17-thu-vien-anh       200 GET "/api/v1/teacher/lessons/${LES[1]}/materials" "" "$LINH"
check 17-co-du-anh "len(d['images'])>=4" 17-thu-vien-anh.json
U0=$(val 17-thu-vien-anh.json images.0.url); U1=$(val 17-thu-vien-anh.json images.1.url); U2=$(val 17-thu-vien-anh.json images.2.url)

# ---- Bảng dạng câu theo cấp ----
step 20-bang-dang-cau      200 GET /api/v1/teacher/questions/types "" "$LINH"
check 20-4-4-3-dang "len(d['types'])==8 and [len(d['byLevel'][k]) for k in ('1','2','3')]==[4,4,3]" 20-bang-dang-cau.json

# ---- Tạo thủ công đủ 8 dạng (duyệt ngay) ----
json q-single.json "{\"questionType\":\"SINGLE_CHOICE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào?\",\"explanation\":\"UNESCO ghi danh năm 2010.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Năm 1999\",\"isCorrect\":false},{\"content\":\"Năm 2010\",\"isCorrect\":true},{\"content\":\"Năm 2003\",\"isCorrect\":false},{\"content\":\"Năm 2015\",\"isCorrect\":false}]}"
json q-truefalse.json "{\"questionType\":\"TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Em hãy xem hình: khu di tích nằm giữa lòng Hà Nội.\",\"explanation\":\"Khu di tích ở trung tâm Hà Nội.\",\"difficultyLevel\":\"EASY\",\"imageUrl\":\"$U0\",\"answerIsTrue\":true}"
json q-imagechoice.json "{\"questionType\":\"IMAGE_CHOICE\",\"heritageId\":$H,\"content\":\"Em hãy chọn hình có tên: Cổng Đoan Môn.\",\"explanation\":\"Hình thứ hai là cổng.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Điện Kính Thiên\",\"imageUrl\":\"$U0\",\"isCorrect\":false},{\"content\":\"Cổng Đoan Môn\",\"imageUrl\":\"$U1\",\"isCorrect\":true},{\"content\":\"Khu khảo cổ\",\"imageUrl\":\"$U2\",\"isCorrect\":false}]}"
json q-matching.json "{\"questionType\":\"MATCHING\",\"heritageId\":$H,\"content\":\"Em hãy ghép mỗi hình với tên của hình đó.\",\"explanation\":\"Mỗi tên là chú thích của hình.\",\"difficultyLevel\":\"MEDIUM\",\"pairs\":[{\"leftImageUrl\":\"$U0\",\"right\":\"Điện Kính Thiên\"},{\"leftImageUrl\":\"$U1\",\"right\":\"Cổng Đoan Môn\"},{\"leftImageUrl\":\"$U2\",\"right\":\"Khu khảo cổ\"}]}"
json q-ordering.json "{\"questionType\":\"ORDERING\",\"heritageId\":$H,\"content\":\"Hãy sắp xếp các sự kiện theo thứ tự thời gian.\",\"explanation\":\"Xếp theo năm.\",\"difficultyLevel\":\"MEDIUM\",\"steps\":[\"Năm 1010: dời đô\",\"Năm 1070: lập Văn Miếu\",\"Năm 1428: Lê Lợi lên ngôi\",\"Năm 2010: UNESCO ghi danh\"]}"
json q-fill.json "{\"questionType\":\"FILL_BLANK\",\"heritageId\":$H,\"content\":\"Vua ___ dời đô từ Hoa Lư về Thăng Long vào năm 1010.\",\"explanation\":\"Lý Thái Tổ dời đô.\",\"difficultyLevel\":\"MEDIUM\",\"blanks\":[{\"accepted\":[\"Lý Thái Tổ\",\"Lý Công Uẩn\"]}],\"distractors\":[\"Lê Lợi\",\"Trần Hưng Đạo\"]}"
json q-multi.json "{\"questionType\":\"MULTI_TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến.\",\"explanation\":\"Nhận định hai sai.\",\"difficultyLevel\":\"HARD\",\"statements\":[{\"text\":\"Khu di tích được UNESCO ghi danh năm 2010.\",\"isTrue\":true},{\"text\":\"Khu di tích nằm ở thành phố Huế.\",\"isTrue\":false},{\"text\":\"Khu di tích có nhiều lớp văn hóa chồng lên nhau.\",\"isTrue\":true}]}"
json q-reading.json "{\"questionType\":\"SOURCE_READING\",\"heritageId\":$H,\"content\":\"Theo đoạn tư liệu, vua nào dời đô về Thăng Long?\",\"explanation\":\"Tư liệu nêu rõ Lý Thái Tổ.\",\"difficultyLevel\":\"MEDIUM\",\"passage\":\"Mùa thu năm Canh Tuất, Lý Thái Tổ dời đô từ Hoa Lư về thành Đại La và đổi tên là Thăng Long.\",\"passageSource\":\"Đại Việt sử ký toàn thư, Ngô Sĩ Liên\",\"options\":[{\"content\":\"Lý Thái Tổ\",\"isCorrect\":true},{\"content\":\"Trần Thái Tông\",\"isCorrect\":false},{\"content\":\"Lê Thái Tổ\",\"isCorrect\":false},{\"content\":\"Đinh Tiên Hoàng\",\"isCorrect\":false}]}"
declare -A QID
i=30
for t in single truefalse imagechoice matching ordering fill multi reading; do
  i=$((i+1))
  step "$i-tao-$t"         201 POST /api/v1/teacher/questions "q-$t.json" "$LINH"
  QID[$t]=$(val "$i-tao-$t.json" id)
  check "$i-duyet-ngay-$t" "d['reviewStatus']=='APPROVED' and d['source']=='MANUAL' and d['reviewedBy']==$LINH_ID and d['partsTotal']>=1 and d['access']['canEdit'] is True" "$i-tao-$t.json"
done
check 40-cap-mac-dinh-sap-xep "(d['levelMin'],d['levelMax'])==(1,2)" 35-tao-ordering.json
check 40-cap-mac-dinh-dien-tu "(d['levelMin'],d['levelMax'])==(2,2)" 36-tao-fill.json
check 40-kho-tu "d['wordBank']==['Lý Thái Tổ','Lê Lợi','Trần Hưng Đạo']" 36-tao-fill.json
check 40-so-phan-ghep-cap "d['partsTotal']==3" 34-tao-matching.json

# ---- Kiểm hợp lệ trả lỗi theo trường ----
json bad-single.json "{\"questionType\":\"SINGLE_CHOICE\",\"heritageId\":$H,\"content\":\"Ngắn\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"A\",\"isCorrect\":true},{\"content\":\"A\",\"isCorrect\":true},{\"content\":\"C\",\"isCorrect\":false},{\"content\":\"D\",\"isCorrect\":false}]}"
step 41-sai-cau-truc       400 POST /api/v1/teacher/questions bad-single.json "$LINH"
checkerr 41-loi-theo-truong "'content' in e and 'explanation' in e and 'options[1].content' in e" 41-sai-cau-truc.json
json bad-image.json "{\"questionType\":\"TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Em hãy xem hình và cho biết đúng hay sai.\",\"explanation\":\"Giải thích.\",\"difficultyLevel\":\"EASY\",\"imageUrl\":\"https://nguoi-la.example/anh.jpg\",\"answerIsTrue\":true}"
step 42-anh-ngoai-thu-vien 400 POST /api/v1/teacher/questions bad-image.json "$LINH"
checkerr 42-loi-anh "'imageUrl' in e" 42-anh-ngoai-thu-vien.json
json bad-level.json "{\"questionType\":\"IMAGE_CHOICE\",\"heritageId\":$H,\"content\":\"Em hãy chọn hình có tên: Cổng.\",\"explanation\":\"Giải thích.\",\"difficultyLevel\":\"EASY\",\"levelMin\":2,\"levelMax\":3,\"options\":[{\"content\":\"Một\",\"imageUrl\":\"$U0\",\"isCorrect\":true},{\"content\":\"Hai\",\"imageUrl\":\"$U1\",\"isCorrect\":false},{\"content\":\"Ba\",\"imageUrl\":\"$U2\",\"isCorrect\":false}]}"
step 43-sai-cap            400 POST /api/v1/teacher/questions bad-level.json "$LINH"
checkerr 43-loi-cap "'levelMin' in e" 43-sai-cap.json
json validate.json "{\"question\":$(cat q-matching.json),\"previewLevel\":1,\"submission\":{\"matches\":[0,2,1]}}"
step 44-kiem-khong-luu     200 POST /api/v1/teacher/questions/validate validate.json "$LINH"
check 44-cham-thu "d['isValid'] is True and d['partsTotal']==3 and d['trial']['partsCorrect']==1 and d['preview']['layout']=='LARGE_CARDS'" 44-kiem-khong-luu.json

# ---- Chấm thử và xem trước câu đã lưu ----
json try-fill.json '{"blanks":["  LÝ  công uẩn "]}'
step 45-cham-thu-dien-tu   200 POST "/api/v1/teacher/questions/${QID[fill]}/try-grade" try-fill.json "$LINH"
check 45-dung-hoa-thuong "d['isCorrect'] is True" 45-cham-thu-dien-tu.json
json try-fill2.json '{"blanks":["Ly Cong Uan"]}'
step 46-cham-thu-sai-dau   200 POST "/api/v1/teacher/questions/${QID[fill]}/try-grade" try-fill2.json "$LINH"
check 46-sai-dau "d['isCorrect'] is False" 46-cham-thu-sai-dau.json
step 47-xem-truoc-cap-1    200 GET "/api/v1/teacher/questions/${QID[truefalse]}/preview?level=1" "" "$LINH"
check 47-cap-1-doc-to "d['readAloud'] is True and d['layout']=='LARGE_CARDS' and d['imageUrl']" 47-xem-truoc-cap-1.json
step 48-xem-truoc-ngoai-cap 400 GET "/api/v1/teacher/questions/${QID[single]}/preview?level=1" "" "$LINH"

# ---- AI sinh câu hỏi theo dạng và cấp của từng lớp ----
step 50-han-muc-ai         200 GET "/api/v1/teacher/lessons/${LES[0]}/ai/questions/quota" "" "$LINH"
check 50-chua-dung-luot "d['used']==0 and d['max']>=4 and d['remaining']==d['max']" 50-han-muc-ai.json
MAXAI=$(val 50-han-muc-ai.json max)
json gen-12.json '{"count":12}'
n=0
for L in "${LES[@]}"; do
  n=$((n+1))
  step "5$n-ai-sinh-cap-$n"  200 POST "/api/v1/teacher/lessons/$L/ai/questions" gen-12.json "$LINH"
  check "5$n-dung-dang-cap-$n" "d['level']==$n and d['received']==12 and all(q['reviewStatus']=='PENDING' and q['source']=='AI' and q['levels']==[$n] for q in d['questions']) and {q['questionType'] for q in d['questions']}=={'1':{'TRUE_FALSE','IMAGE_CHOICE','MATCHING','ORDERING'},'2':{'SINGLE_CHOICE','TRUE_FALSE','ORDERING','FILL_BLANK'},'3':{'SINGLE_CHOICE','MULTI_TRUE_FALSE','SOURCE_READING'}}['$n']" "5$n-ai-sinh-cap-$n.json"
done
check 54-chi-dung-anh-co-san "all(i['url'].startswith('http') for q in d['questions'] for i in q['images'])" 51-ai-sinh-cap-1.json
step 55-han-muc-sau-3-luot 200 GET "/api/v1/teacher/lessons/${LES[0]}/ai/questions/quota" "" "$LINH"
check 55-da-dung-3-luot "d['used']==3 and d['remaining']==$MAXAI-3" 55-han-muc-sau-3-luot.json
json gen-bad-type.json '{"types":["IMAGE_CHOICE"]}'
step 56-dang-khong-hop-cap 400 POST "/api/v1/teacher/lessons/${LES[2]}/ai/questions" gen-bad-type.json "$LINH"
checkerr 56-loi-dang "'types' in e and 'cấp 3' in e['types']" 56-dang-khong-hop-cap.json
json gen-bad-count.json '{"count":21}'
step 57-so-cau-qua-lon     400 POST "/api/v1/teacher/lessons/${LES[2]}/ai/questions" gen-bad-count.json "$LINH"
step 58-bai-cua-nguoi-khac 404 POST "/api/v1/teacher/lessons/${LES[2]}/ai/questions" gen-12.json "$HAI"
step 59-khong-ton-han-muc  200 GET "/api/v1/teacher/lessons/${LES[0]}/ai/questions/quota" "" "$LINH"
check 59-loi-khong-tinh-luot "d['used']==3" 59-khong-ton-han-muc.json

# ---- Duyệt, loại, sửa câu chờ duyệt ----
step 60-cho-duyet          200 GET "/api/v1/teacher/questions?scope=PENDING&pageSize=100" "" "$LINH"
check 60-36-cau-cho-duyet "d['totalItems']==36" 60-cho-duyet.json
step 61-gv-khac-khong-thay 200 GET "/api/v1/teacher/questions?scope=PENDING" "" "$HAI"
check 61-rong "d['totalItems']==0" 61-gv-khac-khong-thay.json
P1=$(val 51-ai-sinh-cap-1.json questions.0.id); P2=$(val 51-ai-sinh-cap-1.json questions.1.id); P3=$(val 51-ai-sinh-cap-1.json questions.2.id); P4=$(val 52-ai-sinh-cap-2.json questions.0.id)
step 62-gv-khac-404        404 GET "/api/v1/teacher/questions/$P1" "" "$HAI"
step 63-duyet-1-cau        200 POST "/api/v1/teacher/questions/$P1/approve" "" "$LINH"
check 63-da-duyet "d['reviewStatus']=='APPROVED' and d['reviewedBy']==$LINH_ID and d['aiGenerationId'] is not None" 63-duyet-1-cau.json
step 64-loai-1-cau         200 POST "/api/v1/teacher/questions/$P2/reject" "" "$LINH"
check 64-bi-loai "d['reviewStatus']=='REJECTED'" 64-loai-1-cau.json
step 65-loai-lan-hai       422 POST "/api/v1/teacher/questions/$P2/reject" "" "$LINH"
json approve-many.json "{\"questionIds\":[$P2,$P3,$P4,99999999]}"
step 66-duyet-nhieu-cau    200 POST /api/v1/teacher/questions/approve approve-many.json "$LINH"
check 66-3-duyet-1-bo-qua "len(d['approved'])==3 and len(d['skipped'])==1 and d['skipped'][0]['questionId']==99999999" 66-duyet-nhieu-cau.json
step 67-gv-khac-thay-chung 200 GET "/api/v1/teacher/questions?scope=SHARED&heritageId=$H&pageSize=100" "" "$HAI"
check 67-thay-cau-da-duyet "{q['id'] for q in d['items']} >= {$P1,$P2,$P3,$P4} and all(q['creatorName'] and q['organizationName'] for q in d['items'])" 67-gv-khac-thay-chung.json
step 68-gv-khac-sua        403 PUT "/api/v1/teacher/questions/$P1" q-single.json "$HAI"
step 69-gv-khac-xoa        403 DELETE "/api/v1/teacher/questions/$P1" "" "$HAI"
step 70-doi-dang-cau-ai    422 PUT "/api/v1/teacher/questions/$P1" q-single.json "$LINH"

# ---- Tìm và lọc ----
step 71-loc-dang-cap       200 GET "/api/v1/teacher/questions?scope=SHARED&questionType=FILL_BLANK&level=2&difficultyLevel=MEDIUM&heritageId=$H&pageSize=100" "" "$HAI"
check 71-chi-dien-tu-cap-2 "d['totalItems']>=1 and all(q['questionType']=='FILL_BLANK' and q['levelMin']<=2<=q['levelMax'] for q in d['items'])" 71-loc-dang-cap.json
step 72-loc-nguoi-tao      200 GET "/api/v1/teacher/questions?scope=SHARED&createdBy=$LINH_ID&keyword=UNESCO&pageSize=100" "" "$HAI"
check 72-tu-khoa-nguoi-tao "d['totalItems']>=1 and all(q['createdBy']==$LINH_ID for q in d['items'])" 72-loc-nguoi-tao.json
step 73-cua-toi-theo-trang-thai 200 GET "/api/v1/teacher/questions?scope=MINE&status=APPROVED&pageSize=100" "" "$LINH"
step 74-so-dem-tab         200 GET /api/v1/teacher/questions/counts "" "$LINH"
check 74-dem-dung "d['pending']==32 and d['mine']==44 and d['shared']>=12" 74-so-dem-tab.json
step 75-phan-trang-sai     400 GET "/api/v1/teacher/questions?pageSize=101" "" "$LINH"

# ---- Nhân bản ----
step 76-nhan-ban           201 POST "/api/v1/teacher/questions/${QID[fill]}/duplicate" "" "$HAI"
check 76-ban-cua-minh "d['source']=='MANUAL' and d['reviewStatus']=='APPROVED' and d['createdBy']!=$LINH_ID and d['blanks'][0]['accepted']==['Lý Thái Tổ','Lý Công Uẩn'] and d['access']['canEdit'] is True" 76-nhan-ban.json
step 77-nhan-ban-cho-duyet 404 POST "/api/v1/teacher/questions/$(val 51-ai-sinh-cap-1.json questions.5.id)/duplicate" "" "$HAI"

# ---- Quản trị hệ thống ẩn câu kém chất lượng ----
step 80-qt-xem-ngan-hang   200 GET "/api/v1/admin/questions?heritageId=$H&pageSize=100" "" "$ADMIN"
check 80-chi-thay-cau-da-duyet "d['totalItems']>=13 and all(q['reviewStatus']=='APPROVED' for q in d['items'])" 80-qt-xem-ngan-hang.json
step 81-qt-xem-chi-doc     200 GET "/api/v1/admin/questions/${QID[single]}" "" "$ADMIN"
check 81-co-the-an "d['access']['canHide'] is True and d['access']['canEdit'] is False" 81-qt-xem-chi-doc.json
step 82-qt-khong-thay-cho-duyet 404 GET "/api/v1/admin/questions/$(val 51-ai-sinh-cap-1.json questions.6.id)" "" "$ADMIN"
json hide.json '{"reason":"Đáp án chưa chính xác"}'
step 83-qt-an-cau          200 POST "/api/v1/admin/questions/${QID[single]}/hide" hide.json "$ADMIN"
check 83-bi-loai-boi-qt "d['reviewStatus']=='REJECTED' and d['reviewedBy']!=$LINH_ID" 83-qt-an-cau.json
step 84-qt-an-lan-hai      422 POST "/api/v1/admin/questions/${QID[single]}/hide" hide.json "$ADMIN"
step 85-nhat-ky            200 GET "/api/v1/admin/audit-logs?action=QUESTION_HIDDEN&targetId=${QID[single]}" "" "$ADMIN"
check 85-co-nhat-ky "d['totalItems']==1 and d['items'][0]['targetType']=='questions'" 85-nhat-ky.json
step 86-gv-khac-het-thay   404 GET "/api/v1/teacher/questions/${QID[single]}" "" "$HAI"
step 87-nguoi-tao-chi-doc  200 GET "/api/v1/teacher/questions/${QID[single]}" "" "$LINH"
check 87-khong-nhan-ban "d['access']['reason']=='HIDDEN_BY_ADMIN' and d['access']['canDuplicate'] is False and d['access']['canEdit'] is False" 87-nguoi-tao-chi-doc.json
step 88-nguoi-tao-nhan-ban 422 POST "/api/v1/teacher/questions/${QID[single]}/duplicate" "" "$LINH"
step 89-nguoi-tao-sua      422 PUT "/api/v1/teacher/questions/${QID[single]}" q-single.json "$LINH"

# ---- Phân quyền ----
step 90-chua-dang-nhap     401 GET /api/v1/teacher/questions ""
step 91-qt-truong-bi-chan  403 GET /api/v1/teacher/questions "" "$THU"
step 92-admin-khong-vao-gv 403 GET /api/v1/teacher/questions "" "$ADMIN"
step 93-gv-khong-vao-admin 403 GET /api/v1/admin/questions "" "$LINH"
step 94-gv-khong-an-cau    403 POST "/api/v1/admin/questions/${QID[fill]}/hide" "" "$LINH"
step 95-hs-them            201 POST "/api/v1/teacher/classes/$C2/students" student.json "$LINH"
SC=$(val 95-hs-them.json sheet.entries.0.studentCode); ST=$(val 95-hs-them.json sheet.entries.0.temporaryPassword)
json hs-login.json "{\"studentCode\":\"$SC\",\"password\":\"$ST\"}"
step 96-hs-login           200 POST /api/v1/student/auth/login hs-login.json
HS1=$(val 96-hs-login.json accessToken)
json hs-pw.json "{\"newPassword\":\"$NEWPW\",\"confirmNewPassword\":\"$NEWPW\"}"
step 97-hs-doi-mk          200 PUT /api/v1/student/me/password hs-pw.json "$HS1"
HS=$(val 97-hs-doi-mk.json accessToken)
step 98-hs-khong-vao-ngan-hang 403 GET /api/v1/teacher/questions "" "$HS"

# ---- Sửa, xóa câu của mình ----
json q-single-edit.json "{\"questionType\":\"SINGLE_CHOICE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào vậy?\",\"explanation\":\"Đã sửa giải thích.\",\"difficultyLevel\":\"MEDIUM\",\"levelMin\":3,\"levelMax\":3,\"options\":[{\"content\":\"Năm 1999\",\"isCorrect\":false},{\"content\":\"Năm 2010\",\"isCorrect\":true},{\"content\":\"Năm 2003\",\"isCorrect\":false},{\"content\":\"Năm 2015\",\"isCorrect\":false}]}"
step 100-sua-cau-cua-minh  200 PUT "/api/v1/teacher/questions/${QID[reading]}" q-reading.json "$LINH"
step 101-doi-dang-thu-cong 200 PUT "/api/v1/teacher/questions/${QID[ordering]}" q-single-edit.json "$LINH"
check 101-thanh-mot-dap-an "d['questionType']=='SINGLE_CHOICE' and d['levels']==[3] and d['steps']==[] and len(d['options'])==4" 101-doi-dang-thu-cong.json
step 102-xoa-mem           200 DELETE "/api/v1/teacher/questions/${QID[matching]}" "" "$LINH"
step 103-xoa-roi-404       404 GET "/api/v1/teacher/questions/${QID[matching]}" "" "$LINH"

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
