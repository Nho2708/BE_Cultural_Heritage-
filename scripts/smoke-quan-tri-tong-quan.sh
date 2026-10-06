#!/usr/bin/env bash
# Smoke test luồng Quản trị và tổng quan (tổng quan trường, tổng quan hệ thống, biểu đồ theo thời gian, kiểm duyệt ngân hàng câu hỏi: danh sách chất lượng, ẩn, hiện lại)
# trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Cách chạy giống smoke-thong-ke-goi-y.sh: body JSON có chữ tiếng Việt đi qua FILE (curl --data-binary @file), liên kết đặt mật khẩu lấy từ log của API
# (Email:Mode = Log; nếu User Secrets đang để Smtp thì đặt Email__Mode=Log khi chạy API để không gửi thư thật). Dịch vụ AI chạy bản giả lập (Ai:Mode = Simulated).
# Nên chạy trên database tạm (Development tự áp migration và nạp di sản từ doc/du-lieu-di-san), vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_tam_wt_abc123;Trusted_Connection=True;TrustServerCertificate=True"
#   Email__Mode=Log Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5180 > api.log 2>&1 &
#   BASE=http://localhost:5180 API_LOG="$PWD/api.log" ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-quan-tri-tong-quan.sh
# Database tạm mới chưa có di sản: chạy trước một lần `dotnet run --project src/DSVHVN.Api --no-build --no-launch-profile -- --chi-khoi-tao-csdl` với cùng biến môi trường; khi chạy từ worktree
# (thư mục cha không phải gốc kho) đặt thêm Seed__HeritageData__ProvincesFile và Seed__HeritageData__HeritagesFile trỏ tới doc/du-lieu-di-san/provinces.json và seed-dot-1.json (đường dẫn tuyệt đối).
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
# Số liệu sau khi học sinh làm bài (điểm trung bình theo lớp, câu cần xem lại từ 10 lượt trả lời trở lên), gói sắp hết hạn và biểu đồ theo ngày giờ Việt Nam cần dựng lượt làm bài
# hoặc chỉnh ngày nên được kiểm trong bộ test tích hợp (OverviewApiTests, QuestionQualityApiTests, OverviewSqlServerTests), không kiểm ở đây.
set -u
BASE=${BASE:-http://localhost:5080}
API_LOG=${API_LOG:?Cần API_LOG: tệp log của API để đọc liên kết đặt mật khẩu}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-quan-tri-tong-quan-out}
PW=MatKhau2026
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

json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json org.json "{\"name\":\"Trường THCS Lê Quý Đôn $S\",\"orgAdmin\":{\"fullName\":\"Phạm Thị Hồng\",\"username\":\"hong.$S\",\"email\":\"hong.$S@lequydon.edu.vn\"}}"
json login-hong.json "{\"emailOrUsername\":\"hong.$S\",\"password\":\"$PW\"}"
json teacher1.json "{\"fullName\":\"Vũ Minh Tuấn\",\"username\":\"tuan.$S\",\"email\":\"tuan.$S@lequydon.edu.vn\"}"
json login-tuan.json "{\"emailOrUsername\":\"tuan.$S\",\"password\":\"$PW\"}"
json class1.json '{"name":"7A1","grade":7,"schoolYear":"2026-2027"}'
json class2.json '{"name":"2B","grade":2,"schoolYear":"2026-2027"}'
json student1.json '{"fullName":"Trần Quốc Bảo"}'
json student2.json '{"fullName":"Lê Ngọc Anh"}'
json hide.json '{"reason":"Câu hỏi dễ gây hiểu nhầm"}'
json restore.json '{"reason":"Đã kiểm tra lại, nội dung đúng"}'
json reason-long.json "{\"reason\":\"$(python -c "print('x'*501)")\"}"

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# ---- Số liệu hệ thống trước khi thêm trường ----
step 02-tong-quan-he-thong-truoc 200 GET /api/v1/admin/overview "" "$ADMIN"
SCHOOLS_BEFORE=$(val 02-tong-quan-he-thong-truoc.json schools.total)
STUDENTS_BEFORE=$(val 02-tong-quan-he-thong-truoc.json activeStudents)
check 02-phan-doanh-thu "d['revenue']['available'] is True and d['revenue']['total']>=0 and 'VNPay' in d['revenue']['message'] and 'Google' in d['revenue']['message'] and len(d['plans'])>=1 and d['content']['questions']['approved']>=0" 02-tong-quan-he-thong-truoc.json

# ---- Một di sản đã xuất bản (có dòng thời gian) ----
step 03-ds-di-san-nhap     200 GET "/api/v1/admin/heritages?status=DRAFT&pageSize=100" "" "$ADMIN"
H1=$(python -c "
import json
d=json.load(open('03-ds-di-san-nhap.json',encoding='utf-8'))['data']['items']
print(next(str(i['id']) for i in d if i['timelineCount']>0))")
step 04-xuat-ban-di-san    200 POST "/api/v1/admin/heritages/$H1/publish" "" "$ADMIN"

# ---- Trường, quản trị trường, giáo viên, hai lớp, hai học sinh ----
step 05-tao-truong         201 POST /api/v1/admin/organizations org.json "$ADMIN"
ORG=$(val 05-tao-truong.json id)
json set-hong.json "{\"token\":\"$(mail_token "hong.$S@lequydon.edu.vn")\",\"newPassword\":\"$PW\"}"
step 06-qt-truong-dat-mk   200 POST /api/v1/auth/set-password set-hong.json
step 07-qt-truong-login    200 POST /api/v1/auth/login login-hong.json
HONG=$(val 07-qt-truong-login.json accessToken)

step 08-tong-quan-truong-rong 200 GET /api/v1/org/overview "" "$HONG"
check 08-truong-moi "d['teachers']['used']==0 and d['teachers']['max']>=1 and d['classCount']==0 and d['students']['active']==0 and d['attempts']['finished']==0 and d['attempts']['averagePercent'] is None and d['planNotice']['kind']=='NONE' and d['plan']['isEffective'] is True and d['ai']['hasEffectivePlan'] is True and len(d['levels'])==3 and d['classes']==[]" 08-tong-quan-truong-rong.json

step 09-tao-giao-vien      201 POST /api/v1/org/teachers teacher1.json "$HONG"
json set-tuan.json "{\"token\":\"$(mail_token "tuan.$S@lequydon.edu.vn")\",\"newPassword\":\"$PW\"}"
step 10-gv-dat-mk          200 POST /api/v1/auth/set-password set-tuan.json
step 11-gv-login           200 POST /api/v1/auth/login login-tuan.json
TUAN=$(val 11-gv-login.json accessToken)
step 12-lop-cap-2          201 POST /api/v1/teacher/classes class1.json "$TUAN"; C1=$(val 12-lop-cap-2.json id)
step 13-lop-cap-1          201 POST /api/v1/teacher/classes class2.json "$TUAN"
step 14-hoc-sinh-1         201 POST "/api/v1/teacher/classes/$C1/students" student1.json "$TUAN"
step 15-hoc-sinh-2         201 POST "/api/v1/teacher/classes/$C1/students" student2.json "$TUAN"

step 16-tong-quan-truong   200 GET /api/v1/org/overview "" "$HONG"
check 16-so-lieu "d['teachers']['used']==1 and d['teachers']['max']>=1 and d['classCount']==2 and d['students']['active']==2 and d['students']['neverLoggedIn']==2 and d['students']['loggedIn']==0 and [l['classCount'] for l in d['levels']]==[1,1,0] and d['levels'][1]['activeStudents']==2 and d['lessons']['total']==0 and d['quizzes']['published']==0 and len(d['classes'])==2 and d['classes'][0]['grade']==2 and d['classes'][1]['className']=='7A1'" 16-tong-quan-truong.json
check 16-khong-lo-ten "'Trần Quốc Bảo' not in open('16-tong-quan-truong.json',encoding='utf-8').read() and 'Vũ Minh Tuấn' not in open('16-tong-quan-truong.json',encoding='utf-8').read()" 16-tong-quan-truong.json

step 17-tong-quan-he-thong-sau 200 GET /api/v1/admin/overview "" "$ADMIN"
check 17-them-mot-truong "d['schools']['total']==$SCHOOLS_BEFORE+1 and d['activeStudents']==$STUDENTS_BEFORE+2 and d['schools']['withEffectivePlan']>=1 and any(p['activeSchools']>=1 for p in d['plans'])" 17-tong-quan-he-thong-sau.json

# ---- Biểu đồ theo thời gian ----
step 20-bieu-do-mac-dinh   200 GET /api/v1/admin/overview/trend "" "$ADMIN"
check 20-30-ngay "d['granularity']=='DAY' and len(d['points'])==30 and d['points'][-1]['newStudents']>=2 and d['points'][-1]['newSchools']>=1 and d['totals']['newStudents']>=2" 20-bieu-do-mac-dinh.json
step 21-bieu-do-thang      200 GET "/api/v1/admin/overview/trend?from=2026-01-01&to=2026-12-31" "" "$ADMIN"
check 21-gom-theo-thang "d['granularity']=='MONTH' and len(d['points'])==12 and d['points'][0]['period']=='2026-01'" 21-bieu-do-thang.json
step 22-bieu-do-nguoc      400 GET "/api/v1/admin/overview/trend?from=2026-03-10&to=2026-03-09" "" "$ADMIN"
step 23-bieu-do-qua-dai    400 GET "/api/v1/admin/overview/trend?from=2024-01-01&to=2026-03-09" "" "$ADMIN"
step 24-bieu-do-ngay-qua-dai 400 GET "/api/v1/admin/overview/trend?from=2026-01-01&to=2026-12-31&granularity=DAY" "" "$ADMIN"

# ---- Kiểm duyệt ngân hàng câu hỏi ----
json question.json "{\"questionType\":\"SINGLE_CHOICE\",\"heritageId\":$H1,\"content\":\"Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào?\",\"explanation\":\"UNESCO ghi danh năm 2010.\",\"difficultyLevel\":\"EASY\",\"options\":[{\"content\":\"Năm 1999\",\"isCorrect\":false},{\"content\":\"Năm 2010\",\"isCorrect\":true},{\"content\":\"Năm 2003\",\"isCorrect\":false},{\"content\":\"Năm 2015\",\"isCorrect\":false}]}"
step 30-gv-tao-cau-hoi     201 POST /api/v1/teacher/questions question.json "$TUAN"
Q=$(val 30-gv-tao-cau-hoi.json id)
step 31-kiem-duyet-danh-sach 200 GET "/api/v1/admin/questions/quality?heritageId=$H1&pageSize=100" "" "$ADMIN"
check 31-co-cau-vua-tao "any(i['question']['id']==$Q and i['answered']==0 and i['correct']==0 and i['correctRatePercent'] is None and i['lowQuality'] is False and i['hiddenAt'] is None for i in d['items']) and d['minAnswers']==10 and d['lowCorrectPercent']==30 and d['approvedCount']>=1" 31-kiem-duyet-danh-sach.json
step 32-loc-cau-can-xem-lai 200 GET "/api/v1/admin/questions/quality?heritageId=$H1&lowQualityOnly=true" "" "$ADMIN"
check 32-chua-co-cau-nao "d['items']==[] and d['totalItems']==0" 32-loc-cau-can-xem-lai.json
step 33-sap-xep-ti-le-dung  200 GET "/api/v1/admin/questions/quality?sort=LOWEST_RATE&organizationId=$ORG" "" "$ADMIN"
check 33-theo-truong "[i['question']['id'] for i in d['items']]==[$Q]" 33-sap-xep-ti-le-dung.json
step 34-trang-thai-sai      400 GET "/api/v1/admin/questions/quality?status=PENDING" "" "$ADMIN"
step 35-hien-khi-chua-an    422 POST "/api/v1/admin/questions/$Q/restore" restore.json "$ADMIN"
step 36-an-ly-do-qua-dai    400 POST "/api/v1/admin/questions/$Q/hide" reason-long.json "$ADMIN"
step 37-an-cau              200 POST "/api/v1/admin/questions/$Q/hide" hide.json "$ADMIN"
check 37-da-an "d['reviewStatus']=='REJECTED' and d['access']['canRestore'] is True and d['access']['canHide'] is False" 37-an-cau.json
step 38-kiem-duyet-tab-da-an 200 GET "/api/v1/admin/questions/quality?heritageId=$H1&status=REJECTED" "" "$ADMIN"
check 38-hien-trong-tab-da-an "[i['question']['id'] for i in d['items']]==[$Q] and d['items'][0]['question']['isHidden'] is True and d['items'][0]['hiddenAt'] and d['hiddenCount']>=1" 38-kiem-duyet-tab-da-an.json
step 39-tong-quan-dem-cau-an 200 GET /api/v1/admin/overview "" "$ADMIN"
check 39-so-cau-da-an "d['content']['questions']['hidden']>=1" 39-tong-quan-dem-cau-an.json
step 40-hien-lai-cau        200 POST "/api/v1/admin/questions/$Q/restore" restore.json "$ADMIN"
check 40-da-duyet-lai "d['reviewStatus']=='APPROVED' and d['reviewedBy']==d['createdBy'] and d['access']['canHide'] is True and d['access']['canRestore'] is False" 40-hien-lai-cau.json
step 41-hien-lai-lan-hai    422 POST "/api/v1/admin/questions/$Q/restore" restore.json "$ADMIN"
step 42-nhat-ky-hien-lai    200 GET "/api/v1/admin/audit-logs?action=QUESTION_RESTORED&targetId=$Q" "" "$ADMIN"
check 42-nhan-tieng-viet "len(d['items'])==1 and d['items'][0]['actionLabel']=='Hiện lại câu hỏi' and 'Đã kiểm tra lại' in d['items'][0]['newValue']" 42-nhat-ky-hien-lai.json
step 43-nhat-ky-an          200 GET "/api/v1/admin/audit-logs?action=QUESTION_HIDDEN&targetId=$Q" "" "$ADMIN"
check 43-ly-do-trong-nhat-ky "len(d['items'])==1 and 'Câu hỏi dễ gây hiểu nhầm' in d['items'][0]['newValue']" 43-nhat-ky-an.json

# ---- Phân quyền ----
step 50-gv-khong-xem-tq-truong 403 GET /api/v1/org/overview "" "$TUAN"
step 51-admin-khong-xem-tq-truong 403 GET /api/v1/org/overview "" "$ADMIN"
step 52-qt-truong-khong-xem-tq-he-thong 403 GET /api/v1/admin/overview "" "$HONG"
step 53-gv-khong-xem-tq-he-thong 403 GET /api/v1/admin/overview "" "$TUAN"
step 54-qt-truong-khong-vao-bieu-do 403 GET /api/v1/admin/overview/trend "" "$HONG"
step 55-gv-khong-vao-kiem-duyet 403 GET /api/v1/admin/questions/quality "" "$TUAN"
step 56-gv-khong-hien-lai   403 POST "/api/v1/admin/questions/$Q/restore" restore.json "$TUAN"
step 57-chua-dang-nhap-tq-truong 401 GET /api/v1/org/overview ""
step 58-chua-dang-nhap-tq-he-thong 401 GET /api/v1/admin/overview ""
step 59-cau-khong-ton-tai   404 POST /api/v1/admin/questions/99999999/restore restore.json "$ADMIN"

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
