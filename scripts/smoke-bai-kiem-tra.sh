#!/usr/bin/env bash
# Smoke test luồng Bài kiểm tra của bài học, nhân bản bài học kèm bài kiểm tra, độ khó của bài học trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-ngan-hang-cau-hoi.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file), liên kết đặt mật khẩu lấy từ log của API
# (Email:Mode = Log; nếu User Secrets đang để Smtp thì đặt Email__Mode=Log khi chạy API để không gửi thư thật). Dịch vụ AI chạy bản giả lập (Ai:Mode = Simulated,
# mặc định), không gọi nhà cung cấp nào. Cần CSDL có sẵn các di sản nạp từ doc/du-lieu-di-san ở trạng thái nháp: script tự xuất bản một di sản đủ điều kiện và thêm ảnh
# vào thư viện ảnh của di sản đó. Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_kiem_thu_smoke;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5092 > api.log 2>&1 &
#   BASE=http://localhost:5092 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-bai-kiem-tra.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
# Tác vụ tự đóng bài kiểm tra quá giờ đóng và lượt làm bài đang dở của học sinh (luồng làm bài chưa có API) được kiểm trong bộ test tích hợp, không kiểm ở đây.
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-bai-kiem-tra-out}
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


draft_to_update() { python -c "
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))['data']
body={'summary':d['summary'],'keyPoints':d['keyPoints'],'sections':[
  {'type':s['type'],'title':s['title'],'body':s['body'],'imageUrl':s['imageUrl'],'milestoneYear':s['milestoneYear'],'eventId':s['eventId'],'sourceCitation':s['sourceCitation']}
  for s in d['sections']]}
json.dump(body,open(sys.argv[2],'w',encoding='utf-8'),ensure_ascii=False)" "$1" "$2"; }

# Giờ UTC dạng ISO cách hiện tại <phút> phút (âm là đã qua).
at() { python -c "
import datetime,sys
print((datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(minutes=int(sys.argv[1]))).strftime('%Y-%m-%dT%H:%M:%SZ'))" "$1"; }

json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json org.json "{\"name\":\"Trường THCS Lê Quý Đôn $S\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@lequydon.edu.vn\"}}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S\",\"password\":\"$PW\"}"
json teacher1.json "{\"fullName\":\"Đặng Thị Mỹ Linh\",\"username\":\"linh.$S\",\"email\":\"linh.$S@lequydon.edu.vn\"}"
json teacher2.json "{\"fullName\":\"Phạm Văn Hải\",\"username\":\"hai.$S\",\"email\":\"hai.$S@lequydon.edu.vn\"}"
json login-linh.json "{\"emailOrUsername\":\"linh.$S\",\"password\":\"$PW\"}"
json login-hai.json "{\"emailOrUsername\":\"hai.$S\",\"password\":\"$PW\"}"
json class1.json '{"name":"3A","grade":3,"schoolYear":"2026-2027"}'
json class2.json '{"name":"8A1","grade":8,"schoolYear":"2026-2027"}'
json class3.json '{"name":"11A2","grade":11,"schoolYear":"2026-2027"}'
json class4.json '{"name":"9A3","grade":9,"schoolYear":"2026-2027"}'
json student.json '{"fullName":"Nguyễn Văn Đạt"}'
json length-detailed.json '{"length":"DETAILED"}'
json empty.json '{}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

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
  json "media-$i.json" "{\"mediaType\":\"IMAGE\",\"url\":\"https://upload.wikimedia.org/smoke-kt-$S-$i.jpg\",\"caption\":\"Ảnh thêm số $i\",\"author\":\"Nguyễn Văn An\",\"license\":\"CC BY-SA 4.0\",\"source\":\"Wikimedia Commons\"}"
  step "03-them-anh-$i"      201 POST "/api/v1/admin/heritages/$H/media" "media-$i.json" "$ADMIN"
done

step 04-tao-truong         201 POST /api/v1/admin/organizations org.json "$ADMIN"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@lequydon.edu.vn")\",\"newPassword\":\"$PW\"}"
step 05-qt-truong-dat-mk   200 POST /api/v1/auth/set-password set-thu.json
step 06-qt-truong-login    200 POST /api/v1/auth/login login-thu.json
THU=$(val 06-qt-truong-login.json accessToken)
step 07-tao-giao-vien-1    201 POST /api/v1/org/teachers teacher1.json "$THU"
json set-linh.json "{\"token\":\"$(mail_token "linh.$S@lequydon.edu.vn")\",\"newPassword\":\"$PW\"}"
step 08-gv1-dat-mk         200 POST /api/v1/auth/set-password set-linh.json
step 09-gv1-login          200 POST /api/v1/auth/login login-linh.json
LINH=$(val 09-gv1-login.json accessToken)
step 10-tao-giao-vien-2    201 POST /api/v1/org/teachers teacher2.json "$THU"
json set-hai.json "{\"token\":\"$(mail_token "hai.$S@lequydon.edu.vn")\",\"newPassword\":\"$PW\"}"
step 11-gv2-dat-mk         200 POST /api/v1/auth/set-password set-hai.json
step 12-gv2-login          200 POST /api/v1/auth/login login-hai.json
HAI=$(val 12-gv2-login.json accessToken)

step 13-lop-cap-1          201 POST /api/v1/teacher/classes class1.json "$LINH"; C1=$(val 13-lop-cap-1.json id)
step 14-lop-cap-2          201 POST /api/v1/teacher/classes class2.json "$LINH"; C2=$(val 14-lop-cap-2.json id)
step 15-lop-cap-3          201 POST /api/v1/teacher/classes class3.json "$LINH"; C3=$(val 15-lop-cap-3.json id)
step 16-lop-cap-2-khac     201 POST /api/v1/teacher/classes class4.json "$LINH"; C4=$(val 16-lop-cap-2-khac.json id)

# Mỗi lớp một bài học đã xuất bản (AI soạn bản giả lập) để có mã bài học.
LES=(); CODES=()
n=0
for C in "$C1" "$C2" "$C3"; do
  n=$((n+1))
  json "lesson-$n.json" "{\"classId\":$C,\"title\":\"Di sản qua bài kiểm tra cấp $n\",\"heritageIds\":[$H]}"
  step "17-tao-bai-cap-$n"   201 POST /api/v1/teacher/lessons "lesson-$n.json" "$LINH"
  L=$(val "17-tao-bai-cap-$n.json" id); LES+=("$L")
  step "17-ai-soan-cap-$n"   200 POST "/api/v1/teacher/lessons/$L/ai/compose" length-detailed.json "$LINH"
  draft_to_update "17-ai-soan-cap-$n.json" "update-$n.json"
  step "17-luu-bai-cap-$n"   200 PUT "/api/v1/teacher/lessons/$L" "update-$n.json" "$LINH"
  step "17-xuat-ban-cap-$n"  200 POST "/api/v1/teacher/lessons/$L/publish" "" "$LINH"
  CODES+=("$(val "17-xuat-ban-cap-$n.json" accessCode)")
done
json lesson-draft.json "{\"classId\":$C2,\"title\":\"Bài còn nháp\",\"heritageIds\":[$H]}"
step 18-bai-nhap           201 POST /api/v1/teacher/lessons lesson-draft.json "$LINH"; LD=$(val 18-bai-nhap.json id)

# ---- Câu hỏi mẫu cho từng cấp ----
step 19-thu-vien-anh       200 GET "/api/v1/teacher/lessons/${LES[1]}/materials" "" "$LINH"
U0=$(val 19-thu-vien-anh.json images.0.url); U1=$(val 19-thu-vien-anh.json images.1.url); U2=$(val 19-thu-vien-anh.json images.2.url)
json q-single.json "{\"questionType\":\"SINGLE_CHOICE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào?\",\"explanation\":\"UNESCO ghi danh năm 2010.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Năm 1999\",\"isCorrect\":false},{\"content\":\"Năm 2010\",\"isCorrect\":true},{\"content\":\"Năm 2003\",\"isCorrect\":false},{\"content\":\"Năm 2015\",\"isCorrect\":false}]}"
json q-truefalse.json "{\"questionType\":\"TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Em hãy xem hình: khu di tích nằm giữa lòng Hà Nội.\",\"explanation\":\"Khu di tích ở trung tâm Hà Nội.\",\"difficultyLevel\":\"MEDIUM\",\"imageUrl\":\"$U0\",\"answerIsTrue\":true}"
json q-ordering.json "{\"questionType\":\"ORDERING\",\"heritageId\":$H,\"content\":\"Hãy sắp xếp các sự kiện theo thứ tự thời gian.\",\"explanation\":\"Xếp theo năm.\",\"difficultyLevel\":\"HARD\",\"steps\":[\"Năm 1010: dời đô\",\"Năm 1070: lập Văn Miếu\",\"Năm 1428: Lê Lợi lên ngôi\",\"Năm 2010: UNESCO ghi danh\"]}"
json q-fill.json "{\"questionType\":\"FILL_BLANK\",\"heritageId\":$H,\"content\":\"Vua ___ dời đô từ Hoa Lư về Thăng Long vào năm 1010.\",\"explanation\":\"Lý Thái Tổ dời đô.\",\"difficultyLevel\":\"HARD\",\"blanks\":[{\"accepted\":[\"Lý Thái Tổ\",\"Lý Công Uẩn\"]}]}"
json q-imagechoice.json "{\"questionType\":\"IMAGE_CHOICE\",\"heritageId\":$H,\"content\":\"Em hãy chọn hình có tên: Cổng Đoan Môn.\",\"explanation\":\"Hình thứ hai là cổng.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Điện Kính Thiên\",\"imageUrl\":\"$U0\",\"isCorrect\":false},{\"content\":\"Cổng Đoan Môn\",\"imageUrl\":\"$U1\",\"isCorrect\":true},{\"content\":\"Khu khảo cổ\",\"imageUrl\":\"$U2\",\"isCorrect\":false}]}"
json q-multi.json "{\"questionType\":\"MULTI_TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến.\",\"explanation\":\"Nhận định hai sai.\",\"difficultyLevel\":\"HARD\",\"statements\":[{\"text\":\"Khu di tích được UNESCO ghi danh năm 2010.\",\"isTrue\":true},{\"text\":\"Khu di tích nằm ở thành phố Huế.\",\"isTrue\":false},{\"text\":\"Khu di tích có nhiều lớp văn hóa chồng lên nhau.\",\"isTrue\":true}]}"
declare -A QID
i=20
for t in single truefalse ordering fill imagechoice multi; do
  i=$((i+1))
  step "$i-tao-cau-$t"     201 POST /api/v1/teacher/questions "q-$t.json" "$LINH"
  QID[$t]=$(val "$i-tao-cau-$t.json" id)
done

# ---- Tạo bài kiểm tra: kiểm dữ liệu vào ----
json quiz-bad-name.json "{\"lessonId\":${LES[1]},\"name\":\"\"}"
step 30-thieu-ten          400 POST /api/v1/teacher/quizzes quiz-bad-name.json "$LINH"
checkerr 30-loi-ten "'name' in e" 30-thieu-ten.json
json quiz-bad-order.json "{\"lessonId\":${LES[1]},\"name\":\"Sai giờ\",\"openAt\":\"$(at 120)\",\"closeAt\":\"$(at 60)\"}"
step 31-gio-dong-truoc-mo  400 POST /api/v1/teacher/quizzes quiz-bad-order.json "$LINH"
checkerr 31-loi-gio-dong "'closeAt' in e" 31-gio-dong-truoc-mo.json
json quiz-l1-limit.json "{\"lessonId\":${LES[0]},\"name\":\"Cấp 1 có giờ\",\"timeLimitMinutes\":15}"
step 32-cap-1-co-gio-lam   400 POST /api/v1/teacher/quizzes quiz-l1-limit.json "$LINH"
checkerr 32-loi-gio-lam "'timeLimitMinutes' in e" 32-cap-1-co-gio-lam.json
json quiz-limit-181.json "{\"lessonId\":${LES[2]},\"name\":\"Quá giờ\",\"timeLimitMinutes\":181}"
step 33-gio-lam-qua-181    400 POST /api/v1/teacher/quizzes quiz-limit-181.json "$LINH"
json quiz-other.json "{\"lessonId\":${LES[1]},\"name\":\"Của người khác\"}"
step 34-bai-cua-nguoi-khac 404 POST /api/v1/teacher/quizzes quiz-other.json "$HAI"

# ---- Bài kiểm tra cấp 2 (lớp 8A1): soạn, thêm câu, đặt điểm và thứ tự ----
json quiz2.json "{\"lessonId\":${LES[1]},\"name\":\"Kiểm tra 15 phút về Hoàng thành\",\"description\":\"Em đọc kỹ đề rồi trả lời.\",\"difficultyLevel\":\"MEDIUM\",\"timeLimitMinutes\":15}"
step 40-tao-quiz-cap-2     201 POST /api/v1/teacher/quizzes quiz2.json "$LINH"; QUIZ_C2=$(val 40-tao-quiz-cap-2.json id)
check 40-nhap-dung-lop "d['status']=='DRAFT' and d['schoolLevel']==2 and d['className']=='8A1' and d['lessonAccessCode']==\"${CODES[1]}\" and d['timeLimitRule']=='OPTIONAL' and d['readiness']['isReady'] is False" 40-tao-quiz-cap-2.json
json add-bad.json "{\"questionIds\":[${QID[single]},${QID[imagechoice]}]}"
step 41-cau-khong-hop-cap  422 POST "/api/v1/teacher/quizzes/$QUIZ_C2/questions" add-bad.json "$LINH"
json add-ok.json "{\"questionIds\":[${QID[single]},${QID[truefalse]},${QID[ordering]},${QID[fill]}],\"point\":1.5}"
step 42-them-4-cau         200 POST "/api/v1/teacher/quizzes/$QUIZ_C2/questions" add-ok.json "$LINH"
check 42-tong-diem-6 "d['totalPoint']==6 and d['questionCount']==4 and [q['sortOrder'] for q in d['questions']]==[1,2,3,4]" 42-them-4-cau.json
step 43-them-lai-cung-cau  400 POST "/api/v1/teacher/quizzes/$QUIZ_C2/questions" add-ok.json "$LINH"
step 44-hop-chon-cau       200 GET "/api/v1/teacher/quizzes/$QUIZ_C2/question-candidates?pageSize=100" "" "$LINH"
check 44-chi-cau-hop-cap-2 "d['level']==2 and all(q['question']['levelMin']<=2<=q['question']['levelMax'] for q in d['items']) and {q['question']['id'] for q in d['items'] if q['inQuiz']}=={${QID[single]},${QID[truefalse]},${QID[ordering]},${QID[fill]}} and ${QID[imagechoice]} not in {q['question']['id'] for q in d['items']}" 44-hop-chon-cau.json
json replace.json "{\"items\":[{\"questionId\":${QID[fill]},\"point\":3},{\"questionId\":${QID[single]},\"point\":1},{\"questionId\":${QID[truefalse]},\"point\":0.5}]}"
step 45-doi-thu-tu-bo-cau  200 PUT "/api/v1/teacher/quizzes/$QUIZ_C2/questions" replace.json "$LINH"
check 45-3-cau-4-diem-ruoi "d['questionCount']==3 and d['totalPoint']==4.5 and [q['questionId'] for q in d['questions']]==[${QID[fill]},${QID[single]},${QID[truefalse]}]" 45-doi-thu-tu-bo-cau.json
json add-back.json "{\"questionIds\":[${QID[ordering]}],\"point\":2}"
step 46-them-lai-sap-xep   200 POST "/api/v1/teacher/quizzes/$QUIZ_C2/questions" add-back.json "$LINH"
check 46-tong-diem-6-5 "d['totalPoint']==6.5 and d['questionCount']==4" 46-them-lai-sap-xep.json
step 47-gv-khac-khong-thay 404 GET "/api/v1/teacher/quizzes/$QUIZ_C2" "" "$HAI"
step 48-cau-trong-quiz-chi-doc 200 GET "/api/v1/teacher/questions/${QID[single]}" "" "$LINH"
check 48-chi-doc "d['access']['isReadOnly'] is True and d['access']['reason']=='USED_IN_QUIZ' and d['quizCount']==1" 48-cau-trong-quiz-chi-doc.json

# ---- Phát hành ----
step 50-chua-du-gio        422 POST "/api/v1/teacher/quizzes/$QUIZ_C2/publish" "" "$LINH"
json settings2.json "{\"name\":\"Kiểm tra 15 phút về Hoàng thành\",\"description\":\"Em đọc kỹ đề rồi trả lời.\",\"difficultyLevel\":\"MEDIUM\",\"timeLimitMinutes\":15,\"openAt\":\"$(at -10)\",\"closeAt\":\"$(at 180)\",\"showAnswersAfterSubmit\":true}"
step 51-dat-gio-mo-dong    200 PUT "/api/v1/teacher/quizzes/$QUIZ_C2" settings2.json "$LINH"
check 51-san-sang "d['readiness']['isReady'] is True and d['access']['canPublish'] is True and d['publishNotice'].startswith('Sau khi phát hành')" 51-dat-gio-mo-dong.json
step 52-gv-khac-phat-hanh  404 POST "/api/v1/teacher/quizzes/$QUIZ_C2/publish" "" "$HAI"
step 53-phat-hanh          200 POST "/api/v1/teacher/quizzes/$QUIZ_C2/publish" "" "$LINH"
check 53-khoa-va-ghi-tong "d['quiz']['status']=='PUBLISHED' and d['quiz']['totalPoint']==6.5 and d['quiz']['publishedAt'] and d['quiz']['openState']=='OPEN' and d['quiz']['access']['canEditQuestions'] is False" 53-phat-hanh.json
step 54-phat-hanh-lan-2    200 POST "/api/v1/teacher/quizzes/$QUIZ_C2/publish" "" "$LINH"
json add-late.json "{\"questionIds\":[${QID[truefalse]}]}"
step 55-khoa-them-cau      422 POST "/api/v1/teacher/quizzes/$QUIZ_C2/questions" add-late.json "$LINH"
step 56-khoa-bo-cau        422 DELETE "/api/v1/teacher/quizzes/$QUIZ_C2/questions/${QID[fill]}" "" "$LINH"
step 57-khoa-xoa-quiz      422 DELETE "/api/v1/teacher/quizzes/$QUIZ_C2" "" "$LINH"
step 59-doc-bai-da-phat-hanh 200 GET "/api/v1/teacher/quizzes/$QUIZ_C2" "" "$LINH"
OPEN2=$(val 59-doc-bai-da-phat-hanh.json openAt)
json settings2-locked.json "{\"name\":\"Kiểm tra 15 phút về Hoàng thành\",\"difficultyLevel\":\"MEDIUM\",\"timeLimitMinutes\":30,\"openAt\":\"$OPEN2\",\"closeAt\":\"$(at 180)\",\"showAnswersAfterSubmit\":true}"
step 58-khoa-doi-gio-lam   422 PUT "/api/v1/teacher/quizzes/$QUIZ_C2" settings2-locked.json "$LINH"
json settings2-edit.json "{\"name\":\"Kiểm tra 15 phút (đã sửa tên)\",\"description\":\"Mô tả mới.\",\"difficultyLevel\":\"MEDIUM\",\"timeLimitMinutes\":15,\"openAt\":\"$OPEN2\",\"closeAt\":\"$(at 240)\",\"showAnswersAfterSubmit\":true}"
step 60-sua-ten-mo-ta-gio-dong 200 PUT "/api/v1/teacher/quizzes/$QUIZ_C2" settings2-edit.json "$LINH"
check 60-da-sua "d['name']=='Kiểm tra 15 phút (đã sửa tên)' and d['description']=='Mô tả mới.' and d['timeLimitMinutes']==15" 60-sua-ten-mo-ta-gio-dong.json
step 61-bai-co-quiz-mo-khong-luu-tru 422 POST "/api/v1/teacher/lessons/${LES[1]}/archive" "" "$LINH"
step 62-bai-co-quiz-khong-xoa 422 DELETE "/api/v1/teacher/lessons/${LES[1]}" "" "$LINH"

# ---- Học sinh thấy bài kiểm tra qua mã bài học ----
step 63-hs-them            201 POST "/api/v1/teacher/classes/$C2/students" student.json "$LINH"
SC=$(val 63-hs-them.json sheet.entries.0.studentCode); ST=$(val 63-hs-them.json sheet.entries.0.temporaryPassword)
json hs-login.json "{\"studentCode\":\"$SC\",\"password\":\"$ST\"}"
step 64-hs-login           200 POST /api/v1/student/auth/login hs-login.json
HS1=$(val 64-hs-login.json accessToken)
json hs-pw.json "{\"newPassword\":\"$NEWPW\",\"confirmNewPassword\":\"$NEWPW\"}"
step 65-hs-doi-mk          200 PUT /api/v1/student/me/password hs-pw.json "$HS1"
HS=$(val 65-hs-doi-mk.json accessToken)
step 66-hs-mo-bai-bang-ma  200 GET "/api/v1/student/lessons/${CODES[1]}" "" "$HS"
check 66-thay-quiz-dang-mo "len(d['quizzes'])==1 and d['quizzes'][0]['state']=='OPEN' and d['quizzes'][0]['questionCount']==4 and d['quizzes'][0]['timeLimitMinutes']==15" 66-hs-mo-bai-bang-ma.json
step 67-hs-khong-vao-gv    403 GET /api/v1/teacher/quizzes "" "$HS"

# ---- Đóng, nhân bản ----
step 70-nhan-ban-quiz      201 POST "/api/v1/teacher/quizzes/$QUIZ_C2/duplicate" "" "$LINH"
check 70-ban-nhap-khong-gio "d['quiz']['status']=='DRAFT' and d['quiz']['openAt'] is None and d['quiz']['closeAt'] is None and d['quiz']['questionCount']==4 and d['quiz']['totalPoint']==6.5 and d['quiz']['name'].endswith('(bản sao)')" 70-nhan-ban-quiz.json
QUIZ_C2_COPY=$(val 70-nhan-ban-quiz.json quiz.id)
step 71-dong-quiz          200 POST "/api/v1/teacher/quizzes/$QUIZ_C2/close" "" "$LINH"
check 71-da-dong "d['quiz']['status']=='CLOSED' and d['quiz']['openState']=='ENDED'" 71-dong-quiz.json
step 72-dong-lan-hai       200 POST "/api/v1/teacher/quizzes/$QUIZ_C2/close" "" "$LINH"
step 73-khong-sua-bai-da-dong 422 PUT "/api/v1/teacher/quizzes/$QUIZ_C2" settings2-edit.json "$LINH"
step 74-hs-thay-da-dong    200 GET "/api/v1/student/lessons/${CODES[1]}" "" "$HS"
check 74-trang-thai-dong "d['quizzes'][0]['state']=='CLOSED'" 74-hs-thay-da-dong.json
step 75-dong-bai-nhap      422 POST "/api/v1/teacher/quizzes/$QUIZ_C2_COPY/close" "" "$LINH"
step 76-nhat-ky-phat-hanh  200 GET "/api/v1/admin/audit-logs?action=QUIZ_PUBLISHED&targetId=$QUIZ_C2" "" "$ADMIN"
check 76-1-dong "d['totalItems']==1 and d['items'][0]['targetType']=='quizzes'" 76-nhat-ky-phat-hanh.json
step 77-nhat-ky-dong       200 GET "/api/v1/admin/audit-logs?action=QUIZ_CLOSED&targetId=$QUIZ_C2" "" "$ADMIN"
check 77-1-dong "d['totalItems']==1" 77-nhat-ky-dong.json
step 78-xoa-quiz-nhap      200 DELETE "/api/v1/teacher/quizzes/$QUIZ_C2_COPY" "" "$LINH"
step 79-xoa-roi-404        404 GET "/api/v1/teacher/quizzes/$QUIZ_C2_COPY" "" "$LINH"

# ---- Cấp 1 và cấp 3: thời gian làm theo cấp ----
json quiz1.json "{\"lessonId\":${LES[0]},\"name\":\"Trò chơi về Hoàng thành\",\"difficultyLevel\":\"EASY\",\"openAt\":\"$(at 5)\",\"closeAt\":\"$(at 300)\"}"
step 80-tao-quiz-cap-1     201 POST /api/v1/teacher/quizzes quiz1.json "$LINH"; QUIZ_C1=$(val 80-tao-quiz-cap-1.json id)
check 80-cap-1-khong-dong-ho "d['schoolLevel']==1 and d['timeLimitRule']=='NOT_ALLOWED' and d['timeLimitMinutes'] is None" 80-tao-quiz-cap-1.json
json add-l1.json "{\"questionIds\":[${QID[imagechoice]},${QID[truefalse]}]}"
step 81-them-cau-cap-1     200 POST "/api/v1/teacher/quizzes/$QUIZ_C1/questions" add-l1.json "$LINH"
json add-l1-bad.json "{\"questionIds\":[${QID[fill]}]}"
step 82-dien-tu-khong-vao-cap-1 422 POST "/api/v1/teacher/quizzes/$QUIZ_C1/questions" add-l1-bad.json "$LINH"
step 83-phat-hanh-cap-1    200 POST "/api/v1/teacher/quizzes/$QUIZ_C1/publish" "" "$LINH"
check 83-chua-toi-gio-mo "d['quiz']['openState']=='NOT_OPEN' and d['quiz']['timeLimitMinutes'] is None" 83-phat-hanh-cap-1.json

json quiz3.json "{\"lessonId\":${LES[2]},\"name\":\"Bài kiểm tra có giờ\",\"difficultyLevel\":\"HARD\",\"openAt\":\"$(at 5)\",\"closeAt\":\"$(at 300)\"}"
step 84-tao-quiz-cap-3     201 POST /api/v1/teacher/quizzes quiz3.json "$LINH"; QUIZ_C3=$(val 84-tao-quiz-cap-3.json id)
check 84-bat-buoc-gio-lam "d['timeLimitRule']=='REQUIRED' and d['timeLimitMinutes'] is None" 84-tao-quiz-cap-3.json
json add-l3.json "{\"questionIds\":[${QID[single]},${QID[multi]}]}"
step 85-them-cau-cap-3     200 POST "/api/v1/teacher/quizzes/$QUIZ_C3/questions" add-l3.json "$LINH"
step 86-cap-3-thieu-gio-lam 422 POST "/api/v1/teacher/quizzes/$QUIZ_C3/publish" "" "$LINH"
json settings3.json "{\"name\":\"Bài kiểm tra có giờ\",\"difficultyLevel\":\"HARD\",\"timeLimitMinutes\":45,\"openAt\":\"$(at 5)\",\"closeAt\":\"$(at 300)\"}"
step 87-dat-gio-lam        200 PUT "/api/v1/teacher/quizzes/$QUIZ_C3" settings3.json "$LINH"
step 88-phat-hanh-cap-3    200 POST "/api/v1/teacher/quizzes/$QUIZ_C3/publish" "" "$LINH"

# ---- Danh sách và lọc ----
step 90-danh-sach          200 GET "/api/v1/teacher/quizzes?pageSize=100" "" "$LINH"
check 90-3-bai "d['totalItems']==3 and {q['status'] for q in d['items']}=={'PUBLISHED','CLOSED'}" 90-danh-sach.json
step 91-loc-trang-thai     200 GET "/api/v1/teacher/quizzes?status=CLOSED" "" "$LINH"
check 91-1-bai-dong "d['totalItems']==1 and d['items'][0]['id']==$QUIZ_C2" 91-loc-trang-thai.json
step 92-loc-theo-lop       200 GET "/api/v1/teacher/quizzes?classId=$C3" "" "$LINH"
check 92-1-bai "d['totalItems']==1 and d['items'][0]['schoolLevel']==3" 92-loc-theo-lop.json
step 93-gv-khac-rong       200 GET "/api/v1/teacher/quizzes" "" "$HAI"
check 93-rong "d['totalItems']==0" 93-gv-khac-rong.json

# ---- Nhân bản bài học kèm bài kiểm tra ----
json dup-no.json "{\"targetClassId\":$C4}"
step 100-nhan-ban-khong-kem 201 POST "/api/v1/teacher/lessons/${LES[1]}/duplicate" dup-no.json "$LINH"
check 100-khong-chep-quiz "d['copiedQuizzes']==0 and d['lesson']['quizCount']==0" 100-nhan-ban-khong-kem.json
json dup-yes.json "{\"targetClassId\":$C4,\"includeQuizzes\":true}"
step 101-nhan-ban-kem-quiz 201 POST "/api/v1/teacher/lessons/${LES[1]}/duplicate" dup-yes.json "$LINH"
check 101-chep-1-quiz "d['copiedQuizzes']==1 and d['skippedQuestions']==0 and d['lesson']['quizCount']==1 and d['lesson']['status']=='DRAFT' and d['lesson']['accessCode'] is None" 101-nhan-ban-kem-quiz.json
check 101-thong-bao "'Đã chép 1 bài kiểm tra thành bản nháp' in d['message']" 101-nhan-ban-kem-quiz.json
LDUP=$(val 101-nhan-ban-kem-quiz.json lesson.id)
step 102-quiz-cua-ban-sao  200 GET "/api/v1/teacher/quizzes?lessonId=$LDUP" "" "$LINH"
check 102-nhap-khong-gio "d['totalItems']==1 and d['items'][0]['status']=='DRAFT' and d['items'][0]['openAt'] is None and d['items'][0]['closeAt'] is None and d['items'][0]['questionCount']==4 and d['items'][0]['className']=='9A3'" 102-quiz-cua-ban-sao.json
json dup-l1.json "{\"targetClassId\":$C1,\"includeQuizzes\":true}"
step 103-nhan-ban-sang-cap-1 201 POST "/api/v1/teacher/lessons/${LES[1]}/duplicate" dup-l1.json "$LINH"
check 103-bo-cau-khong-hop-cap "d['copiedQuizzes']==1 and d['skippedQuestions']==2 and d['levelNotice'] is not None" 103-nhan-ban-sang-cap-1.json
step 104-gv-khac-nhan-ban  404 POST "/api/v1/teacher/lessons/${LES[1]}/duplicate" dup-yes.json "$HAI"

# ---- Độ khó của bài học ----
step 110-danh-sach-bai     200 GET "/api/v1/teacher/lessons?pageSize=100" "" "$LINH"
check 110-do-kho "{l['id']:l['difficulty'] for l in d['items']}[${LES[1]}]=='HARD' and {l['id']:l['difficultyLabel'] for l in d['items']}[$LD]=='Chưa có bài kiểm tra' and {l['id']:l['difficulty'] for l in d['items']}[${LES[0]}]=='MEDIUM'" 110-danh-sach-bai.json
step 111-loc-kho           200 GET "/api/v1/teacher/lessons?difficulty=HARD&pageSize=100" "" "$LINH"
check 111-chi-bai-kho "${LES[1]} in [l['id'] for l in d['items']] and all(l['difficulty']=='HARD' for l in d['items'])" 111-loc-kho.json
step 112-loc-trung-binh    200 GET "/api/v1/teacher/lessons?difficulty=MEDIUM&pageSize=100" "" "$LINH"
check 112-chi-bai-trung-binh "${LES[0]} in [l['id'] for l in d['items']] and all(l['difficulty']=='MEDIUM' for l in d['items'])" 112-loc-trung-binh.json
step 113-loc-kho-sai       400 GET "/api/v1/teacher/lessons?difficulty=IMPOSSIBLE" "" "$LINH"

# ---- Phân quyền ----
step 120-chua-dang-nhap    401 GET /api/v1/teacher/quizzes ""
step 121-qt-truong-bi-chan 403 GET /api/v1/teacher/quizzes "" "$THU"
step 122-admin-bi-chan     403 GET /api/v1/teacher/quizzes "" "$ADMIN"
step 123-gv-khac-phat-hanh 404 POST "/api/v1/teacher/quizzes/$QUIZ_C1/publish" "" "$HAI"

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
