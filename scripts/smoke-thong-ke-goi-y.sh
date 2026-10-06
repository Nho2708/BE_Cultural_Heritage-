#!/usr/bin/env bash
# Smoke test luồng Thống kê và gợi ý (gợi ý cho em của học sinh, kết quả bài kiểm tra của lớp, tiến độ, mức thành thạo, hồ sơ học tập, gợi ý ôn tập cho lớp)
# trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-hoc-tap-theo-cap.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file), liên kết đặt mật khẩu lấy từ log của API
# (Email:Mode = Log; nếu User Secrets đang để Smtp thì đặt Email__Mode=Log khi chạy API để không gửi thư thật). Dịch vụ AI chạy bản giả lập (Ai:Mode = Simulated).
# Nên chạy trên database tạm (Development tự áp migration và nạp di sản từ doc/du-lieu-di-san), vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_tam_wt_abc123;Trusted_Connection=True;TrustServerCertificate=True" 
#   Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development 
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5180 > api.log 2>&1 &
#   BASE=http://localhost:5180 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-thong-ke-goi-y.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
# Bước đồng hồ (hết giờ tự nộp, lọc theo khoảng thời gian nộp) và lỗi Dịch vụ AI cần chỉnh giờ hoặc ép lỗi nên được kiểm trong bộ test tích hợp, không kiểm ở đây.
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-thong-ke-goi-y-out}
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
json org.json "{\"name\":\"Trường THCS Nguyễn Du $S\",\"orgAdmin\":{\"fullName\":\"Lê Thị Thu\",\"username\":\"thu.$S\",\"email\":\"thu.$S@nguyendu.edu.vn\"}}"
json login-thu.json "{\"emailOrUsername\":\"thu.$S\",\"password\":\"$PW\"}"
json teacher1.json "{\"fullName\":\"Đặng Thị Mỹ Linh\",\"username\":\"linh.$S\",\"email\":\"linh.$S@nguyendu.edu.vn\"}"
json login-linh.json "{\"emailOrUsername\":\"linh.$S\",\"password\":\"$PW\"}"
json class1.json '{"name":"8A1","grade":8,"schoolYear":"2026-2027"}'
json class2.json '{"name":"9A2","grade":9,"schoolYear":"2026-2027"}'
json student.json '{"fullName":"Nguyễn Văn Đạt"}'
json length-detailed.json '{"length":"DETAILED"}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# ---- Hai di sản đã xuất bản (có dòng thời gian) ----
step 02-ds-di-san-nhap     200 GET "/api/v1/admin/heritages?status=DRAFT&pageSize=100" "" "$ADMIN"
CANDIDATES=$(python -c "
import json
d=json.load(open('02-ds-di-san-nhap.json',encoding='utf-8'))['data']['items']
print(' '.join(str(i['id']) for i in d if i['timelineCount']>0))")
HS_IDS=()
for id in $CANDIDATES; do
  code=$(curl -s -o "pub-$id.json" -w "%{http_code}" -X POST "$BASE/api/v1/admin/heritages/$id/publish" -H "Authorization: Bearer $ADMIN")
  if [ "$code" = "200" ]; then HS_IDS+=("$id"); fi
  [ "${#HS_IDS[@]}" = "2" ] && break
done
if [ "${#HS_IDS[@]}" != "2" ]; then echo "SAI: không xuất bản được hai di sản có dòng thời gian (cần doc/du-lieu-di-san)"; exit 1; fi
H1=${HS_IDS[0]}; H2=${HS_IDS[1]}
PASS=$((PASS+1)); printf '| 03-xuat-ban-hai-di-san | POST /api/v1/admin/heritages/{id}/publish | 200 | 200 | ĐÚNG | %s %s |\n' "$H1" "$H2"
for h in $H1 $H2; do
  json "media-$h.json" "{\"mediaType\":\"IMAGE\",\"url\":\"https://upload.wikimedia.org/smoke-tk-$S-$h.jpg\",\"caption\":\"Ảnh thêm\",\"author\":\"Nguyễn Văn An\",\"license\":\"CC BY-SA 4.0\",\"source\":\"Wikimedia Commons\"}"
  step "03-them-anh-$h"    201 POST "/api/v1/admin/heritages/$h/media" "media-$h.json" "$ADMIN"
done

# ---- Trường, giáo viên, hai lớp cấp 2 (lớp thứ hai để thử quyền) ----
step 04-tao-truong         201 POST /api/v1/admin/organizations org.json "$ADMIN"
json set-thu.json "{\"token\":\"$(mail_token "thu.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 05-qt-truong-dat-mk   200 POST /api/v1/auth/set-password set-thu.json
step 06-qt-truong-login    200 POST /api/v1/auth/login login-thu.json
THU=$(val 06-qt-truong-login.json accessToken)
step 07-tao-giao-vien      201 POST /api/v1/org/teachers teacher1.json "$THU"
json set-linh.json "{\"token\":\"$(mail_token "linh.$S@nguyendu.edu.vn")\",\"newPassword\":\"$PW\"}"
step 08-gv-dat-mk          200 POST /api/v1/auth/set-password set-linh.json
step 09-gv-login           200 POST /api/v1/auth/login login-linh.json
LINH=$(val 09-gv-login.json accessToken)
step 10-lop-1              201 POST /api/v1/teacher/classes class1.json "$LINH"; C1=$(val 10-lop-1.json id)
step 11-lop-2              201 POST /api/v1/teacher/classes class2.json "$LINH"; C2=$(val 11-lop-2.json id)

# ---- Hai bài học đã xuất bản, mỗi bài một di sản ----
LES=(); CODES=(); IMG=()
n=0
for H in $H1 $H2; do
  n=$((n+1))
  json "lesson-$n.json" "{\"classId\":$C1,\"title\":\"Bài về di sản số $n\",\"heritageIds\":[$H]}"
  step "12-tao-bai-$n"      201 POST /api/v1/teacher/lessons "lesson-$n.json" "$LINH"
  L=$(val "12-tao-bai-$n.json" id); LES+=("$L")
  step "12-ai-soan-$n"      200 POST "/api/v1/teacher/lessons/$L/ai/compose" length-detailed.json "$LINH"
  draft_to_update "12-ai-soan-$n.json" "update-$n.json"
  step "12-luu-bai-$n"      200 PUT "/api/v1/teacher/lessons/$L" "update-$n.json" "$LINH"
  step "12-xuat-ban-$n"     200 POST "/api/v1/teacher/lessons/$L/publish" "" "$LINH"
  CODES+=("$(val "12-xuat-ban-$n.json" accessCode)")
  step "12-thu-vien-anh-$n" 200 GET "/api/v1/teacher/lessons/$L/materials" "" "$LINH"
  IMG+=("$(val "12-thu-vien-anh-$n.json" images.0.url)")
done

# ---- Bốn câu cho mỗi di sản và một bài kiểm tra đang mở cho mỗi bài học ----
mkquestions() { # <di sản> <ảnh> <tiền tố>
  local H=$1 U=$2 p=$3
  json "$p-single.json" "{\"questionType\":\"SINGLE_CHOICE\",\"heritageId\":$H,\"content\":\"Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào?\",\"explanation\":\"UNESCO ghi danh năm 2010.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Năm 1999\",\"isCorrect\":false},{\"content\":\"Năm 2010\",\"isCorrect\":true},{\"content\":\"Năm 2003\",\"isCorrect\":false},{\"content\":\"Năm 2015\",\"isCorrect\":false}]}"
  json "$p-truefalse.json" "{\"questionType\":\"TRUE_FALSE\",\"heritageId\":$H,\"content\":\"Em hãy xem hình: khu di tích nằm giữa lòng Hà Nội.\",\"explanation\":\"Khu di tích ở trung tâm Hà Nội.\",\"difficultyLevel\":\"MEDIUM\",\"imageUrl\":\"$U\",\"answerIsTrue\":true}"
  json "$p-ordering.json" "{\"questionType\":\"ORDERING\",\"heritageId\":$H,\"content\":\"Hãy sắp xếp các sự kiện theo thứ tự thời gian.\",\"explanation\":\"Xếp theo năm.\",\"difficultyLevel\":\"HARD\",\"steps\":[\"Năm 1010: dời đô\",\"Năm 1070: lập Văn Miếu\",\"Năm 1428: Lê Lợi lên ngôi\",\"Năm 2010: UNESCO ghi danh\"]}"
  json "$p-fill.json" "{\"questionType\":\"FILL_BLANK\",\"heritageId\":$H,\"content\":\"Vua ___ dời đô từ Hoa Lư về Thăng Long vào năm 1010.\",\"explanation\":\"Lý Thái Tổ dời đô.\",\"difficultyLevel\":\"HARD\",\"blanks\":[{\"accepted\":[\"Lý Thái Tổ\",\"Lý Công Uẩn\"]}],\"distractors\":[\"Lê Lợi\",\"Trần Hưng Đạo\"]}"
  local ids=()
  for t in single truefalse ordering fill; do
    step "13-tao-cau-$p-$t" 201 POST /api/v1/teacher/questions "$p-$t.json" "$LINH"
    ids+=("$(val "13-tao-cau-$p-$t.json" id)")
  done
  IDS_JSON="[${ids[0]},${ids[1]},${ids[2]},${ids[3]}]"
}
mkquiz() { # <tên rút gọn> <bài học> <tên> <mảng id câu>
  json "quiz-$1.json" "{\"lessonId\":$2,\"name\":\"$3\",\"difficultyLevel\":\"MEDIUM\",\"openAt\":\"$(at -1)\",\"closeAt\":\"$(at 240)\",\"showAnswersAfterSubmit\":true}"
  step "14-tao-quiz-$1"    201 POST /api/v1/teacher/quizzes "quiz-$1.json" "$LINH"
  local id; id=$(val "14-tao-quiz-$1.json" id)
  json "add-$1.json" "{\"questionIds\":$4,\"point\":2}"
  step "14-them-cau-$1"    200 POST "/api/v1/teacher/quizzes/$id/questions" "add-$1.json" "$LINH"
  step "14-phat-hanh-$1"   200 POST "/api/v1/teacher/quizzes/$id/publish" "" "$LINH"
  eval "QUIZ_$1=$id"
}
mkquestions "$H1" "${IMG[0]}" h1; mkquiz 1 "${LES[0]}" "Kiểm tra di sản thứ nhất" "$IDS_JSON"
mkquestions "$H2" "${IMG[1]}" h2; mkquiz 2 "${LES[1]}" "Kiểm tra di sản thứ hai" "$IDS_JSON"

# ---- Ba học sinh của lớp ----
HS=(); SID=()
for n in 1 2 3; do
  json "student-$n.json" "{\"fullName\":\"Học sinh số $n\"}"
  step "20-hs-them-$n"     201 POST "/api/v1/teacher/classes/$C1/students" "student-$n.json" "$LINH"
  SID+=("$(val "20-hs-them-$n.json" students.0.id)")
  code=$(val "20-hs-them-$n.json" sheet.entries.0.studentCode); tmp=$(val "20-hs-them-$n.json" sheet.entries.0.temporaryPassword)
  json "hs-login-$n.json" "{\"studentCode\":\"$code\",\"password\":\"$tmp\"}"
  step "21-hs-login-$n"    200 POST /api/v1/student/auth/login "hs-login-$n.json"
  json "hs-pw-$n.json" "{\"newPassword\":\"$NEWPW\",\"confirmNewPassword\":\"$NEWPW\"}"
  step "22-hs-doi-mk-$n"   200 PUT /api/v1/student/me/password "hs-pw-$n.json" "$(val "21-hs-login-$n.json" accessToken)"
  HS+=("$(val "22-hs-doi-mk-$n.json" accessToken)")
done

attempt() { # <chỉ số học sinh 0..2> <mã quiz> <đúng|sai> <nhãn>: bắt đầu, trả lời cả bốn câu rồi nộp; mã lượt vào ATTEMPT_<nhãn>
  local tok=${HS[$1]} quiz=$2 mode=$3 label=$4
  step "30-bat-dau-$label"  201 POST "/api/v1/student/quizzes/$quiz/attempts" "" "$tok"
  local A; A=$(val "30-bat-dau-$label.json" id)
  for k in 0 1 2 3; do
    local qid; qid=$(val "30-bat-dau-$label.json" "questions.$k.questionId")
    answer "30-bat-dau-$label.json" "$k" "$mode" "ans-$label-$k.json"
    step "31-luu-$label-$k" 200 PUT "/api/v1/student/attempts/$A/answers/$qid" "ans-$label-$k.json" "$tok"
  done
  step "32-nop-$label"      200 POST "/api/v1/student/attempts/$A/submit" "" "$tok"
  eval "ATTEMPT_$label=$A"
}

# ---- Gợi ý cho em: học sinh chưa làm gì ----
step 40-goi-y-hs3-moi      200 GET /api/v1/student/recommendations "" "${HS[2]}"
check 40-loi-moi-nhap-ma "d['message'] and 'mã bài học' in d['message'] and d['level']==2 and len(d['nextQuizzes'])==2 and all(q['needsLessonCode'] and q['label']=='Cần mã bài học' for q in d['nextQuizzes']) and len(d['newLessons'])==2 and all(l['accessCode'] is None and l['hint']=='Nhờ thầy cô đưa mã bài học' for l in d['newLessons']) and not d['lessonsToReview'] and not d['heritagesToReview'] and not d['relatedHeritages']" 40-goi-y-hs3-moi.json

# ---- Làm bài: học sinh 1 đúng bài 1 và sai bài 2; học sinh 2 sai bài 1; học sinh 3 không làm ----
attempt 0 "$QUIZ_1" đúng a1
attempt 0 "$QUIZ_2" sai a2
attempt 1 "$QUIZ_1" sai a3

step 41-goi-y-hs1-trang-hoc 200 GET /api/v1/student/recommendations "" "${HS[0]}"
check 41-nen-on-di-san-2 "[h['heritageId'] for h in d['heritagesToReview']]==[$H2] and [l['lessonId'] for l in d['lessonsToReview']]==[${LES[1]}] and d['lessonsToReview'][0]['accessCode']==\"${CODES[1]}\" and not d['nextQuizzes'] and not d['newLessons'] and d['message'] is None and d['reviewPraise'] is None and d['targetDifficulty']=='MEDIUM'" 41-goi-y-hs1-trang-hoc.json
step 42-goi-y-trang-ket-qua-sai 200 GET "/api/v1/student/attempts/$ATTEMPT_a2/recommendations" "" "${HS[0]}"
check 42-chi-tinh-luot-dang-xem "d['attemptId']==$ATTEMPT_a2 and [h['heritageId'] for h in d['heritagesToReview']]==[$H2] and 'câu' in d['heritagesToReview'][0]['reason']" 42-goi-y-trang-ket-qua-sai.json
step 43-goi-y-trang-ket-qua-dung 200 GET "/api/v1/student/attempts/$ATTEMPT_a1/recommendations" "" "${HS[0]}"
check 43-khen-khi-dung-het "not d['heritagesToReview'] and d['reviewPraise'] and d['attemptId']==$ATTEMPT_a1" 43-goi-y-trang-ket-qua-dung.json
step 44-goi-y-hs2           200 GET /api/v1/student/recommendations "" "${HS[1]}"
check 44-hs2-sai-bai-1 "[h['heritageId'] for h in d['heritagesToReview']]==[$H1] and [q['quizId'] for q in d['nextQuizzes']]==[$QUIZ_2] and d['nextQuizzes'][0]['needsLessonCode'] is True and d['targetDifficulty']=='EASY'" 44-goi-y-hs2.json

step 45-hs-khac-khong-xem-luot 404 GET "/api/v1/student/attempts/$ATTEMPT_a1/recommendations" "" "${HS[2]}"
step 46-gv-khong-vao-khu-hoc 403 GET /api/v1/student/recommendations "" "$LINH"
step 47-chua-dang-nhap     401 GET /api/v1/student/recommendations ""

# ---- Giáo viên: kết quả bài kiểm tra ----
step 50-tong-quan          200 GET "/api/v1/teacher/quizzes/$QUIZ_1/results" "" "$LINH"
check 50-so-lieu "d['enrolled']==3 and d['submitted']==2 and d['finished']==2 and d['notStarted']==1 and d['averageScore']==4 and d['highestScore']==8 and d['lowestScore']==0 and d['averagePercent']==50 and len(d['distribution'])==10 and d['distribution'][0]['count']==1 and d['distribution'][9]['count']==1" 50-tong-quan.json
step 51-tung-cau           200 GET "/api/v1/teacher/quizzes/$QUIZ_1/results/questions" "" "$LINH"
check 51-ti-le-dung "len(d['questions'])==4 and all(q['answered']==2 and q['correct']==1 and q['ratePercent']==50 for q in d['questions']) and len(d['byQuestionType'])==4" 51-tung-cau.json
check 51-loi-hay-gap "any(q['questionType']=='FILL_BLANK' and q['commonErrors'] and q['commonErrors'][0]['mostCommonWrong']=='sai hoàn toàn' for q in d['questions']) and any(q['questionType']=='SINGLE_CHOICE' and q['mostChosen'] is not None for q in d['questions'])" 51-tung-cau.json
step 52-tung-hoc-sinh      200 GET "/api/v1/teacher/quizzes/$QUIZ_1/results/students" "" "$LINH"
check 52-trang-thai "sorted(r['status'] for r in d['students'])==['NOT_STARTED','SUBMITTED','SUBMITTED'] and all(r['score'] is not None for r in d['students'] if r['status']=='SUBMITTED') and all(r['stars'] is None for r in d['students'])" 52-tung-hoc-sinh.json
step 53-chu-de             200 GET "/api/v1/teacher/quizzes/$QUIZ_1/results/topics" "" "$LINH"
check 53-theo-di-san "len(d['byHeritage'])==1 and d['byHeritage'][0]['heritageId']==$H1 and d['byHeritage'][0]['answered']==8 and d['byHeritage'][0]['correct']==4 and d['byHeritage'][0]['ratePercent']==50" 53-chu-de.json
step 54-chi-tiet-bai-lam   200 GET "/api/v1/teacher/attempts/$ATTEMPT_a3" "" "$LINH"
check 54-sai-het "d['score']==0 and d['totalPoint']==8 and d['correctCount']==0 and len(d['questions'])==4 and all(q['isCorrect'] is False and q['correctAnswer'] and q['explanation'] for q in d['questions'])" 54-chi-tiet-bai-lam.json
step 55-goi-y-bai-kiem-tra 200 GET "/api/v1/teacher/quizzes/$QUIZ_1/results/recommendations" "" "$LINH"
check 55-chua-du-10-cau "d['quizId']==$QUIZ_1 and d['answeredQuestions']==8 and not d['heritagesToReview'] and d['message'] is None" 55-goi-y-bai-kiem-tra.json

# ---- Giáo viên: lớp ----
step 60-tien-do-lop        200 GET "/api/v1/teacher/classes/$C1/progress" "" "$LINH"
check 60-tien-do "d['enrolled']==3 and d['quizzesPublished']==2 and d['submittedTotal']==3 and d['progressPercent']==50 and len(d['students'])==3 and len(d['cells'])==3 and d['neverLoggedIn']==0" 60-tien-do-lop.json
step 61-thanh-thao-lop     200 GET "/api/v1/teacher/classes/$C1/mastery" "" "$LINH"
check 61-theo-di-san "len(d['byHeritage'])==2 and len(d['byQuestionType'])==4 and len(d['heatmapStudents'])==3 and len(d['heatmapCells'])==3 and {r['id'] for r in d['byHeritage']}=={$H1,$H2}" 61-thanh-thao-lop.json
step 62-thanh-thao-loc-loai 200 GET "/api/v1/teacher/classes/$C1/mastery?heritageType=INTANGIBLE" "" "$LINH"
step 63-thanh-thao-loai-sai 400 GET "/api/v1/teacher/classes/$C1/mastery?heritageType=KHONG_CO" "" "$LINH"
step 64-ho-so-hoc-sinh-1   200 GET "/api/v1/teacher/classes/$C1/students/${SID[0]}/profile" "" "$LINH"
check 64-ho-so "d['finishedAttempts']==2 and d['averagePercent']==50 and len(d['history'])==2 and len(d['scoreTrend'])==2 and len(d['completedLessons'])==2 and len(d['masteryByHeritage'])==2 and {b['code'] for b in d['badges']}=={'PERFECT_SCORE','ON_TIME'}" 64-ho-so-hoc-sinh-1.json
step 65-ho-so-hoc-sinh-3   200 GET "/api/v1/teacher/classes/$C1/students/${SID[2]}/profile" "" "$LINH"
check 65-chua-nop "d['finishedAttempts']==0 and d['averagePercent'] is None and d['message']" 65-ho-so-hoc-sinh-3.json
step 66-goi-y-on-tap-lop   200 GET "/api/v1/teacher/classes/$C1/recommendations" "" "$LINH"
check 66-so-lieu "d['submittedAttempts']==3 and d['answeredQuestions']==12 and d['message'] is None and not d['heritagesToReview'] and all('reason' in h for h in d['relatedHeritages']) and len(d['relatedHeritages'])<=5" 66-goi-y-on-tap-lop.json
step 67-goi-y-sai-khoang   400 GET "/api/v1/teacher/classes/$C1/recommendations?from=2026-10-10T00:00:00Z&to=2026-10-01T00:00:00Z" "" "$LINH"
step 68-lop-chua-co-ket-qua 200 GET "/api/v1/teacher/classes/$C2/recommendations" "" "$LINH"
check 68-loi-bao "d['message'] and 'Chưa có học sinh nào nộp bài' in d['message'] and not d['heritagesToReview'] and not d['relatedHeritages']" 68-lop-chua-co-ket-qua.json

# ---- Phân quyền ----
step 70-hs-khong-vao-lop-khac 404 GET "/api/v1/teacher/classes/$C2/students/${SID[0]}/profile" "" "$LINH"
step 71-quan-tri-truong-bi-chan 403 GET "/api/v1/teacher/classes/$C1/progress" "" "$THU"
step 72-hs-khong-xem-thong-ke 403 GET "/api/v1/teacher/quizzes/$QUIZ_1/results" "" "${HS[0]}"
step 73-chua-dang-nhap     401 GET "/api/v1/teacher/classes/$C1/mastery" ""
step 74-bai-khong-ton-tai  404 GET "/api/v1/teacher/quizzes/99999999/results" "" "$LINH"
step 75-luot-khong-ton-tai 404 GET "/api/v1/teacher/attempts/99999999" "" "$LINH"

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
