#!/usr/bin/env bash
# Smoke test luồng Học tập của học sinh theo cấp (làm bài kiểm tra, chấm từng phần, kết quả theo cấp, lịch sử, tiến độ, trang học) trên API đang chạy
# (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-bai-kiem-tra.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file), liên kết đặt mật khẩu lấy từ log của API
# (Email:Mode = Log; nếu User Secrets đang để Smtp thì đặt Email__Mode=Log khi chạy API để không gửi thư thật). Dịch vụ AI chạy bản giả lập. Nên chạy trên database tạm
# (Development tự áp migration và nạp di sản từ doc/du-lieu-di-san), vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_tam_wt_abc123;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5180 > api.log 2>&1 &
#   BASE=http://localhost:5180 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-hoc-tap-theo-cap.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
# Quá hạn nộp và tự nộp khi hết giờ cần chỉnh giờ bắt đầu của lượt nên được kiểm trong bộ test tích hợp, không kiểm ở đây.
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-hoc-tap-theo-cap-out}
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

# answer <tệp lượt làm bài> <thứ tự câu từ 0> <đúng|sai> <tệp ra>: dựng câu trả lời đúng (hoặc sai) theo những gì em thấy trên màn hình.
answer() { python -c "
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))['data']
q=d['questions'][int(sys.argv[2])]; right=sys.argv[3]=='đúng'; t=q['questionType']
def idx(items,text): return [i for i,x in enumerate(items) if (x['content'] if isinstance(x,dict) else x)==text][0]
body={}
if t=='SINGLE_CHOICE': body={'selectedIndex':idx(q['options'],'Năm 2010' if right else 'Năm 1999')}
elif t=='TRUE_FALSE': body={'selectedIndex':idx(q['options'],'Đúng' if right else 'Sai')}
elif t=='IMAGE_CHOICE': body={'selectedIndex':idx(q['options'],'Cổng Đoan Môn' if right else 'Điện Kính Thiên')}
elif t=='SOURCE_READING': body={'selectedIndex':idx(q['options'],'Lý Thái Tổ' if right else 'Trần Thái Tông')}
elif t=='ORDERING':
    steps=['Năm 1010: dời đô','Năm 1070: lập Văn Miếu','Năm 1428: Lê Lợi lên ngôi','Năm 2010: UNESCO ghi danh']
    steps=steps if right else steps[::-1]
    body={'order':[idx(q['items'],s) for s in steps]}
elif t=='FILL_BLANK': body={'blanks':['Lý Thái Tổ' if right else 'sai hoàn toàn']}
elif t=='MULTI_TRUE_FALSE':
    truth=lambda s: ('Huế' not in s)
    body={'statements':[truth(s)==right for s in q['statements']]}
elif t=='MATCHING':
    names={'Điện Kính Thiên':0,'Cổng Đoan Môn':1,'Khu khảo cổ':2}
    # hình bên trái theo thứ tự ảnh gửi lên: đúng tên của hình = tên cùng chỉ số trong danh sách ảnh của câu
    order=json.load(open('matching-pairs.json',encoding='utf-8'))
    m=[]
    for l in q['leftItems']:
        want=order[l['imageUrl']]
        j=idx(q['rightItems'],want) if right else idx(q['rightItems'],[n for n in order.values() if n!=want][0])
        m.append(j)
    body={'matches':m}
json.dump(body,open(sys.argv[4],'w',encoding='utf-8'),ensure_ascii=False)" "$1" "$2" "$3" "$4"; }

json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json org.json "{\"name\":\"Trường THCS Lê Quý Đôn $S\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@lequydon.edu.vn\"}}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S\",\"password\":\"$PW\"}"
json teacher1.json "{\"fullName\":\"Đặng Thị Mỹ Linh\",\"username\":\"linh.$S\",\"email\":\"linh.$S@lequydon.edu.vn\"}"
json login-linh.json "{\"emailOrUsername\":\"linh.$S\",\"password\":\"$PW\"}"
json class1.json '{"name":"3A","grade":3,"schoolYear":"2026-2027"}'
json class2.json '{"name":"8A1","grade":8,"schoolYear":"2026-2027"}'
json class3.json '{"name":"11A2","grade":11,"schoolYear":"2026-2027"}'
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
  json "media-$i.json" "{\"mediaType\":\"IMAGE\",\"url\":\"https://upload.wikimedia.org/smoke-ht-$S-$i.jpg\",\"caption\":\"Ảnh thêm số $i\",\"author\":\"Nguyễn Văn An\",\"license\":\"CC BY-SA 4.0\",\"source\":\"Wikimedia Commons\"}"
  step "03-them-anh-$i"      201 POST "/api/v1/admin/heritages/$H/media" "media-$i.json" "$ADMIN"
done

step 04-tao-truong         201 POST /api/v1/admin/organizations org.json "$ADMIN"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@lequydon.edu.vn")\",\"newPassword\":\"$PW\"}"
step 05-qt-truong-dat-mk   200 POST /api/v1/auth/set-password set-thu.json
step 06-qt-truong-login    200 POST /api/v1/auth/login login-thu.json
THU=$(val 06-qt-truong-login.json accessToken)
step 07-tao-giao-vien      201 POST /api/v1/org/teachers teacher1.json "$THU"
json set-linh.json "{\"token\":\"$(mail_token "linh.$S@lequydon.edu.vn")\",\"newPassword\":\"$PW\"}"
step 08-gv-dat-mk          200 POST /api/v1/auth/set-password set-linh.json
step 09-gv-login           200 POST /api/v1/auth/login login-linh.json
LINH=$(val 09-gv-login.json accessToken)

step 13-lop-cap-1          201 POST /api/v1/teacher/classes class1.json "$LINH"; C1=$(val 13-lop-cap-1.json id)
step 14-lop-cap-2          201 POST /api/v1/teacher/classes class2.json "$LINH"; C2=$(val 14-lop-cap-2.json id)
step 15-lop-cap-3          201 POST /api/v1/teacher/classes class3.json "$LINH"; C3=$(val 15-lop-cap-3.json id)

LES=(); CODES=(); n=0
for C in "$C1" "$C2" "$C3"; do
  n=$((n+1))
  json "lesson-$n.json" "{\"classId\":$C,\"title\":\"Di sản qua bài làm cấp $n\",\"heritageIds\":[$H]}"
  step "17-tao-bai-cap-$n"   201 POST /api/v1/teacher/lessons "lesson-$n.json" "$LINH"
  L=$(val "17-tao-bai-cap-$n.json" id); LES+=("$L")
  step "17-ai-soan-cap-$n"   200 POST "/api/v1/teacher/lessons/$L/ai/compose" length-detailed.json "$LINH"
  draft_to_update "17-ai-soan-cap-$n.json" "update-$n.json"
  step "17-luu-bai-cap-$n"   200 PUT "/api/v1/teacher/lessons/$L" "update-$n.json" "$LINH"
  step "17-xuat-ban-cap-$n"  200 POST "/api/v1/teacher/lessons/$L/publish" "" "$LINH"
  CODES+=("$(val "17-xuat-ban-cap-$n.json" accessCode)")
done

# ---- Câu hỏi mẫu của từng cấp ----
step 19-thu-vien-anh       200 GET "/api/v1/teacher/lessons/${LES[1]}/materials" "" "$LINH"
U0=$(val 19-thu-vien-anh.json images.0.url); U1=$(val 19-thu-vien-anh.json images.1.url); U2=$(val 19-thu-vien-anh.json images.2.url)
python -c "
import json,sys
json.dump({sys.argv[1]:'Điện Kính Thiên',sys.argv[2]:'Cổng Đoan Môn',sys.argv[3]:'Khu khảo cổ'},open('matching-pairs.json','w',encoding='utf-8'),ensure_ascii=False)" "$U0" "$U1" "$U2"
json q-single.json "{\"questionType\":\"SINGLE_CHOICE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào?\",\"explanation\":\"UNESCO ghi danh năm 2010.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Năm 1999\",\"isCorrect\":false},{\"content\":\"Năm 2010\",\"isCorrect\":true},{\"content\":\"Năm 2003\",\"isCorrect\":false},{\"content\":\"Năm 2015\",\"isCorrect\":false}]}"
json q-truefalse.json "{\"questionType\":\"TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Em hãy xem hình: khu di tích nằm giữa lòng Hà Nội.\",\"explanation\":\"Khu di tích ở trung tâm Hà Nội.\",\"difficultyLevel\":\"MEDIUM\",\"imageUrl\":\"$U0\",\"answerIsTrue\":true}"
json q-ordering.json "{\"questionType\":\"ORDERING\",\"heritageId\":$H,\"content\":\"Hãy sắp xếp các sự kiện theo thứ tự thời gian.\",\"explanation\":\"Xếp theo năm.\",\"difficultyLevel\":\"HARD\",\"steps\":[\"Năm 1010: dời đô\",\"Năm 1070: lập Văn Miếu\",\"Năm 1428: Lê Lợi lên ngôi\",\"Năm 2010: UNESCO ghi danh\"]}"
json q-fill.json "{\"questionType\":\"FILL_BLANK\",\"heritageId\":$H,\"content\":\"Vua ___ dời đô từ Hoa Lư về Thăng Long vào năm 1010.\",\"explanation\":\"Lý Thái Tổ dời đô.\",\"difficultyLevel\":\"HARD\",\"blanks\":[{\"accepted\":[\"Lý Thái Tổ\",\"Lý Công Uẩn\"]}],\"distractors\":[\"Lê Lợi\",\"Trần Hưng Đạo\"]}"
json q-imagechoice.json "{\"questionType\":\"IMAGE_CHOICE\",\"heritageId\":$H,\"content\":\"Em hãy chọn hình có tên: Cổng Đoan Môn.\",\"explanation\":\"Hình thứ hai là cổng.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Điện Kính Thiên\",\"imageUrl\":\"$U0\",\"isCorrect\":false},{\"content\":\"Cổng Đoan Môn\",\"imageUrl\":\"$U1\",\"isCorrect\":true},{\"content\":\"Khu khảo cổ\",\"imageUrl\":\"$U2\",\"isCorrect\":false}]}"
json q-matching.json "{\"questionType\":\"MATCHING\",\"heritageId\":$H,\"content\":\"Em hãy ghép mỗi hình với tên của hình đó.\",\"explanation\":\"Mỗi tên là chú thích của hình.\",\"difficultyLevel\":\"MEDIUM\",\"pairs\":[{\"leftImageUrl\":\"$U0\",\"right\":\"Điện Kính Thiên\"},{\"leftImageUrl\":\"$U1\",\"right\":\"Cổng Đoan Môn\"},{\"leftImageUrl\":\"$U2\",\"right\":\"Khu khảo cổ\"}]}"
json q-multi.json "{\"questionType\":\"MULTI_TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến.\",\"explanation\":\"Nhận định hai sai.\",\"difficultyLevel\":\"HARD\",\"statements\":[{\"text\":\"Khu di tích được UNESCO ghi danh năm 2010.\",\"isTrue\":true},{\"text\":\"Khu di tích nằm ở thành phố Huế.\",\"isTrue\":false},{\"text\":\"Khu di tích có nhiều lớp văn hóa chồng lên nhau.\",\"isTrue\":true}]}"
json q-source.json "{\"questionType\":\"SOURCE_READING\",\"heritageId\":$H,\"content\":\"Theo đoạn tư liệu, vua nào dời đô về Thăng Long?\",\"explanation\":\"Đoạn tư liệu nêu rõ Lý Thái Tổ.\",\"difficultyLevel\":\"MEDIUM\",\"passage\":\"Mùa thu năm Canh Tuất, Lý Thái Tổ dời đô từ Hoa Lư về thành Đại La và đổi tên là Thăng Long.\",\"passageSource\":\"Đại Việt sử ký toàn thư, Ngô Sĩ Liên, thế kỷ 15\",\"options\":[{\"content\":\"Lý Thái Tổ\",\"isCorrect\":true},{\"content\":\"Trần Thái Tông\",\"isCorrect\":false},{\"content\":\"Lê Thái Tổ\",\"isCorrect\":false},{\"content\":\"Đinh Tiên Hoàng\",\"isCorrect\":false}]}"
declare -A QID
i=20
for t in single truefalse ordering fill imagechoice matching multi source; do
  i=$((i+1))
  step "$i-tao-cau-$t"     201 POST /api/v1/teacher/questions "q-$t.json" "$LINH"
  QID[$t]=$(val "$i-tao-cau-$t.json" id)
done

# ---- Ba bài kiểm tra đang mở: cấp 1 (4 dạng), cấp 2 (4 dạng), cấp 3 (3 dạng) ----
# Giờ mở có thể ở quá khứ khi phát hành (chỉ giờ đóng phải ở tương lai): bài mở ngay.
mkquiz() { # <tên rút gọn> <bài học> <tên> <thời gian làm hoặc null> <mảng id câu>
  json "quiz-$1.json" "{\"lessonId\":$2,\"name\":\"$3\",\"difficultyLevel\":\"MEDIUM\",\"timeLimitMinutes\":$4,\"openAt\":\"$(at -1)\",\"closeAt\":\"$(at 240)\",\"showAnswersAfterSubmit\":true}"
  step "30-tao-quiz-$1"    201 POST /api/v1/teacher/quizzes "quiz-$1.json" "$LINH"
  local id; id=$(val "30-tao-quiz-$1.json" id)
  json "add-$1.json" "{\"questionIds\":$5,\"point\":2}"
  step "31-them-cau-$1"    200 POST "/api/v1/teacher/quizzes/$id/questions" "add-$1.json" "$LINH"
  step "32-phat-hanh-$1"   200 POST "/api/v1/teacher/quizzes/$id/publish" "" "$LINH"
  eval "QUIZ_$1=$id"
}
mkquiz c1 "${LES[0]}" "Trò chơi Hoàng thành" null "[${QID[truefalse]},${QID[imagechoice]},${QID[matching]},${QID[ordering]}]"
mkquiz c2 "${LES[1]}" "Kiểm tra Hoàng thành" 15 "[${QID[single]},${QID[truefalse]},${QID[ordering]},${QID[fill]}]"
mkquiz c3 "${LES[2]}" "Bài kiểm tra có giờ" 45 "[${QID[single]},${QID[multi]},${QID[source]}]"

# ---- Học sinh ba cấp ----
HS=(); SC=()
n=0
for C in "$C1" "$C2" "$C3"; do
  n=$((n+1))
  step "40-hs-them-$n"     201 POST "/api/v1/teacher/classes/$C/students" student.json "$LINH"
  code=$(val "40-hs-them-$n.json" sheet.entries.0.studentCode); tmp=$(val "40-hs-them-$n.json" sheet.entries.0.temporaryPassword)
  json "hs-login-$n.json" "{\"studentCode\":\"$code\",\"password\":\"$tmp\"}"
  step "41-hs-login-$n"    200 POST /api/v1/student/auth/login "hs-login-$n.json"
  json "hs-pw-$n.json" "{\"currentPassword\":\"$tmp\",\"newPassword\":\"$NEWPW\",\"confirmNewPassword\":\"$NEWPW\"}"   # lớp cấp 1 tự đổi (có mật khẩu hiện tại); cấp 2, 3 bị buộc đổi và bỏ qua trường này
  step "42-hs-doi-mk-$n"   200 PUT /api/v1/student/me/password "hs-pw-$n.json" "$(val "41-hs-login-$n.json" accessToken)"
  HS+=("$(val "42-hs-doi-mk-$n.json" accessToken)")
done

run_level() { # <cấp 1..3> <mã quiz> <số câu> <đúng|sai>
  local lv=$1 quiz=$2 count=$3 mode=$4 tok=${HS[$(( $1 - 1 ))]}
  step "50-trang-hoc-c$lv"   200 GET /api/v1/student/home "" "$tok"
  check "50-can-ma-c$lv" "len(d['openQuizzes'])==1 and d['openQuizzes'][0]['needsLessonCode'] is True and d['openQuizzes'][0]['label']=='Cần mã bài học' and d['level']==$lv" "50-trang-hoc-c$lv.json"
  step "51-gioi-thieu-c$lv"  200 GET "/api/v1/student/quizzes/$quiz" "" "$tok"
  check "51-bat-dau-c$lv" "d['action']=='START' and d['questionCount']==$count and (d['totalPoint'] is None)==($lv==1) and (d['timeLimitMinutes'] is None)==($lv==1)" "51-gioi-thieu-c$lv.json"
  step "52-bat-dau-c$lv"     201 POST "/api/v1/student/quizzes/$quiz/attempts" "" "$tok"
  A=$(val "52-bat-dau-c$lv.json" id)
  step "52-bat-dau-lai-c$lv" 200 POST "/api/v1/student/quizzes/$quiz/attempts" "" "$tok"
  check "52-cung-luot-c$lv" "d['id']==$A and d['answeredCount']==0 and d['level']==$lv" "52-bat-dau-lai-c$lv.json"
  for ((k=0; k<count; k++)); do
    qid=$(val "52-bat-dau-c$lv.json" "questions.$k.questionId")
    answer "52-bat-dau-c$lv.json" "$k" "$mode" "ans-c$lv-$k.json"
    step "53-luu-c$lv-$k"    200 PUT "/api/v1/student/attempts/$A/answers/$qid" "ans-c$lv-$k.json" "$tok"
  done
  json bad-answer.json '{"selectedIndex":99,"order":[0,0],"matches":[0,0,0,0,0,0]}'
  qid0=$(val "52-bat-dau-c$lv.json" "questions.0.questionId")
  step "54-sai-cau-truc-c$lv" 400 PUT "/api/v1/student/attempts/$A/answers/$qid0" bad-answer.json "$tok"
  step "55-vao-tiep-c$lv"    200 GET "/api/v1/student/attempts/$A" "" "$tok"
  check "55-da-luu-c$lv" "d['answeredCount']==$count and all(q['answered'] for q in d['questions'])" "55-vao-tiep-c$lv.json"
  step "56-chua-xem-ket-qua-c$lv" 409 GET "/api/v1/student/attempts/$A/result" "" "$tok"
  step "57-nop-c$lv"         200 POST "/api/v1/student/attempts/$A/submit" "" "$tok"
  step "57-nop-lai-c$lv"     200 POST "/api/v1/student/attempts/$A/submit" "" "$tok"
  step "58-het-quyen-sua-c$lv" 409 PUT "/api/v1/student/attempts/$A/answers/$qid0" "ans-c$lv-0.json" "$tok"
  step "59-ket-qua-c$lv"     200 GET "/api/v1/student/attempts/$A/result" "" "$tok"
  eval "ATTEMPT_$lv=$A"
}

run_level 1 "$QUIZ_c1" 4 đúng
check 60-cap-1-sao-khong-diem "d['resultView']=='STARS' and d['stars']==3 and d['score'] is None and d['percent'] is None and d['correctCount'] is None and d['praise'] and {b['code'] for b in d['badges']}=={'PERFECT_SCORE','ON_TIME'} and d['analysis'] is None and all(r['isCorrect'] is None and r['pointEarned'] is None for r in d['questions'])" 59-ket-qua-c1.json
run_level 2 "$QUIZ_c2" 4 sai
check 61-cap-2-diem "d['resultView']=='SCORE' and d['score']==0 and d['totalPoint']==8 and d['correctCount']==0 and d['wrongCount']==4 and d['stars'] is None and len(d['questions'])==4 and all(r['isCorrect'] is False and r['explanation'] for r in d['questions']) and {b['code'] for b in d['badges']}=={'ON_TIME'}" 59-ket-qua-c2.json
run_level 3 "$QUIZ_c3" 3 đúng
check 62-cap-3-phan-tich "d['resultView']=='ANALYSIS' and d['score']==6 and d['percent']==100 and d['analysis'] is not None and len(d['analysis']['byHeritage'])==1 and d['analysis']['byHeritage'][0]['ratePercent']==100 and len(d['analysis']['byQuestionType'])==3 and d['analysis']['partsCorrect']==3 and d['analysis']['partsTotal']==3" 59-ket-qua-c3.json

# ---- Lịch sử, tiến độ, trang học ----
for lv in 1 2 3; do
  tok=${HS[$((lv-1))]}
  step "70-lich-su-c$lv"     200 GET /api/v1/student/attempts "" "$tok"
  check "70-1-luot-c$lv" "d['totalItems']==1 and d['items'][0]['status']=='SUBMITTED' and (d['items'][0]['score'] is None)==($lv==1) and (d['items'][0]['stars'] is None)==($lv!=1)" "70-lich-su-c$lv.json"
  step "71-tien-do-c$lv"     200 GET /api/v1/student/progress "" "$tok"
  check "71-da-nop-c$lv" "d['quizzesSubmitted']==1 and d['quizzesPublished']==1 and d['progressPercent']==100 and d['lessonsCompleted']==1 and len(d['mastery'])==1 and (d['totalStars'] is not None)==($lv==1)" "71-tien-do-c$lv.json"
  step "72-trang-hoc-sau-c$lv" 200 GET /api/v1/student/home "" "$tok"
  check "72-bai-hoc-cua-em-c$lv" "len(d['openQuizzes'])==0 and len(d['inProgress'])==0 and len(d['myLessons'])==1 and d['myLessons'][0]['accessCode']==\"${CODES[$((lv-1))]}\" and d['myLessons'][0]['quizzesDone']==1 and len(d['recentBadges'])>=1" "72-trang-hoc-sau-c$lv.json"
done
check 73-cap-1-mastery-sao "d['mastery'][0]['percent'] is None and d['mastery'][0]['stars']==3" 71-tien-do-c1.json
check 73-cap-2-mastery-phan-tram "d['mastery'][0]['percent']==0 and d['mastery'][0]['label']=='Cần ôn'" 71-tien-do-c2.json

# ---- Phân quyền ----
step 80-hs-khac-lop-khong-thay 404 GET "/api/v1/student/attempts/$ATTEMPT_2" "" "${HS[0]}"
step 81-hs-khac-lop-khong-vao-quiz 404 GET "/api/v1/student/quizzes/$QUIZ_c2" "" "${HS[0]}"
step 82-hs-khac-khong-xem-ket-qua 404 GET "/api/v1/student/attempts/$ATTEMPT_2/result" "" "${HS[2]}"
step 83-gv-khong-vao-khu-hoc 403 GET /api/v1/student/home "" "$LINH"
step 84-chua-dang-nhap     401 GET /api/v1/student/home ""
step 85-da-nop-khong-bat-dau-lai 409 POST "/api/v1/student/quizzes/$QUIZ_c2/attempts" "" "${HS[1]}"
step 86-xem-lai-sau-khi-dong 200 POST "/api/v1/teacher/quizzes/$QUIZ_c2/close" "" "$LINH"
step 87-ket-qua-sau-dong   200 GET "/api/v1/student/attempts/$ATTEMPT_2/result" "" "${HS[1]}"
check 87-van-diem-cu "d['score']==0 and d['status']=='SUBMITTED'" 87-ket-qua-sau-dong.json

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
