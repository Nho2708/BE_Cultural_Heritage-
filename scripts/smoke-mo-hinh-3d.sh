#!/usr/bin/env bash
# Smoke test luồng Mô hình 3D di sản trên API đang chạy (Kestrel thật, CSDL thật), mặc định http://localhost:5080.
#
# Luồng này chỉ cần tài khoản quản trị hệ thống (không đọc log thư). Tệp .glb thử do script tự dựng (một tệp glTF 2.0 tối thiểu hợp lệ). Kho tệp dùng bản Local
# (Storage:Provider = Local, mặc định). Cần CSDL có sẵn các di sản nạp từ doc/du-lieu-di-san ở trạng thái nháp: script tự xuất bản một di sản đủ điều kiện, rồi ẩn và hiện lại
# di sản đó để thử việc mô hình đi theo di sản. Nên chạy trên database tạm, vd:
#   ConnectionStrings__Default="Server=(localdb)\MSSQLLocalDB;Database=dsvhvn_tam_a2_smoke3d;Trusted_Connection=True;TrustServerCertificate=True" \
#   Email__Mode=Log Ai__Mode=Simulated Storage__Provider=Local Seed__AdminPassword='<mật khẩu thử>' ASPNETCORE_ENVIRONMENT=Development \
#   dotnet run --project src/DSVHVN.Api --no-launch-profile --urls http://localhost:5092 > api.log 2>&1 &
#   BASE=http://localhost:5092 ADMIN_PASSWORD='<mật khẩu thử>' bash scripts/smoke-mo-hinh-3d.sh
# Biến tùy chọn: BASE, ADMIN_LOGIN (mặc định admin.nho), SMOKE_OUT (thư mục ghi kết quả từng bước).
set -u
BASE=${BASE:-http://localhost:5080}
ADMIN_LOGIN=${ADMIN_LOGIN:-admin.nho}
ADMIN_PASSWORD=${ADMIN_PASSWORD:?Cần ADMIN_PASSWORD: mật khẩu seed của ADMIN}
OUT=${SMOKE_OUT:-smoke-mo-hinh-3d-out}
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

# checkfile <tên> <mã HTTP> <loại nội dung> <kích thước byte> <đường dẫn công khai> : tải tệp mô hình như trình xem làm, không đăng nhập
checkfile() {
  local name=$1 want=$2 type=$3 size=$4 path=$5
  local out; out=$(curl -s -o "$name.bin" -w "%{http_code} %{content_type} %{size_download}" "$BASE$path")
  if [ "$out" = "$want $type $size" ]; then PASS=$((PASS+1)); r=ĐÚNG; else FAIL=$((FAIL+1)); r=SAI; fi
  printf '| %s | GET %s | %s | %s | %s | |\n' "$name" "$path" "$want $type $size" "$out" "$r"
}

json() { printf '%s' "$2" > "$1"; }

# Tệp .glb tối thiểu hợp lệ (glTF 2.0, một khối JSON và một khối nhị phân nhỏ, không tham chiếu tệp ngoài) và một tệp không phải .glb.
python - <<'PY'
import json, struct
doc = json.dumps({"asset": {"version": "2.0"}, "buffers": [{"byteLength": 4}]}).encode()
doc += b" " * ((4 - len(doc) % 4) % 4)
binary = bytes([1, 2, 3, 4])
total = 12 + 8 + len(doc) + 8 + len(binary)
glb = struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(doc), 0x4E4F534A) + doc + struct.pack("<II", len(binary), 0x004E4942) + binary
open("mo-hinh.glb", "wb").write(glb)
open("khong-phai-glb.glb", "wb").write(b"\x89PNG\r\n\x1a\n" + b"0" * 64)
PY
SIZE=$(wc -c < mo-hinh.glb | tr -d ' ')

# Giá trị chữ tiếng Việt của biểu mẫu đi qua FILE (-F "trường=<tệp"): tham số dòng lệnh trên Windows làm hỏng dấu.
json ten-mo-hinh.txt 'Khuê Văn Các'
json tac-gia.txt 'Nguyễn Văn An'
json giay-phep.txt 'CC BY 4.0'
json giay-phep-han-che.txt 'CC BY-NC 4.0'
json admin.json "{\"emailOrUsername\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}"
json empty.json '{}'

echo "| Bước | Yêu cầu | Mong đợi | Thực tế | Kết quả | errorCode, message |"
echo "|---|---|---|---|---|---|"
step 01-login-admin        200 POST /api/v1/auth/login admin.json
ADMIN=$(val 01-login-admin.json accessToken)

# Xuất bản một di sản đủ điều kiện (có ảnh đại diện, nguồn).
step 02-ds-di-san-nhap     200 GET "/api/v1/admin/heritages?status=DRAFT&pageSize=100" "" "$ADMIN"
CANDIDATES=$(python -c "
import json
d=json.load(open('02-ds-di-san-nhap.json',encoding='utf-8'))['data']['items']
print(' '.join(str(i['id']) for i in d))")
H=""
for id in $CANDIDATES; do
  code=$(curl -s -o "pub-$id.json" -w "%{http_code}" -X POST "$BASE/api/v1/admin/heritages/$id/publish" -H "Authorization: Bearer $ADMIN")
  if [ "$code" = "200" ]; then H=$id; break; fi
done
if [ -z "$H" ]; then echo "SAI: không xuất bản được di sản (cần doc/du-lieu-di-san)"; exit 1; fi
PASS=$((PASS+1)); printf '| 03-xuat-ban-di-san | POST /api/v1/admin/heritages/{id}/publish | 200 | 200 | ĐÚNG | %s |\n' "$H"
SLUG=$(val "pub-$H.json" slug)

# ---- Chưa có mô hình ----
step 04-chua-co-mo-hinh    404 GET "/api/v1/public/heritages/$SLUG/model"
checkcode 04-ma-khong-tai-duoc MODEL_UNAVAILABLE 04-chua-co-mo-hinh.json
step 05-chi-tiet-chua-co   200 GET "/api/v1/public/heritages/$SLUG"
check 05-khong-co-nut-xem-3d "d['hasModel'] is False" 05-chi-tiet-chua-co.json

# ---- Quyền ----
step 06-chua-dang-nhap     401 POST "/api/v1/admin/heritages/$H/models/upload" "" "" -F "file=@mo-hinh.glb;type=model/gltf-binary" -F author=An -F license=CC0 -F sourceUrl=https://sketchfab.com/m/1
step 07-danh-sach-chua-dn  401 GET "/api/v1/admin/heritages/$H/models"

# ---- Tệp và ghi công bị từ chối ----
step 10-khong-phai-glb     400 POST "/api/v1/admin/heritages/$H/models/upload" "" "$ADMIN" -F "file=@khong-phai-glb.glb;type=model/gltf-binary" -F author=An -F license=CC0 -F sourceUrl=https://sketchfab.com/m/1
checkcode 10-ma-invalid INVALID 10-khong-phai-glb.json
step 11-giay-phep-han-che  400 POST "/api/v1/admin/heritages/$H/models/upload" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary" -F author=An -F "license=<giay-phep-han-che.txt" -F sourceUrl=https://sketchfab.com/m/1
checkcode 11-ma-invalid INVALID 11-giay-phep-han-che.json
step 12-thieu-tac-gia      400 POST "/api/v1/admin/heritages/$H/models/upload" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary" -F license=CC0 -F sourceUrl=https://sketchfab.com/m/1
checkcode 12-ma-required REQUIRED 12-thieu-tac-gia.json
step 13-thieu-nguon        400 POST "/api/v1/admin/heritages/$H/models/upload" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary" -F author=An -F "license=<giay-phep.txt"
checkcode 13-ma-required REQUIRED 13-thieu-nguon.json
step 14-chua-co-gi-sau-loi 200 GET "/api/v1/admin/heritages/$H/models" "" "$ADMIN"
check 14-danh-sach-trong "d==[]" 14-chua-co-gi-sau-loi.json

# ---- Tải lên và hiện luôn ----
step 20-tai-len-va-hien    201 POST "/api/v1/admin/heritages/$H/models/upload" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary" -F "title=<ten-mo-hinh.txt" -F "author=<tac-gia.txt" -F "license=<giay-phep.txt" -F sourceUrl=https://sketchfab.com/m/khue-van-cac -F show=true
M1=$(val 20-tai-len-va-hien.json id)
FILE1=$(val 20-tai-len-va-hien.json fileUrl)
check 20-du-lieu "d['status']=='PUBLISHED' and d['isVisibleToPublic'] is True and d['fileSizeBytes']==$SIZE and d['fileFormat']=='GLB' and d['credit']=='Nguyễn Văn An · CC BY 4.0 · https://sketchfab.com/m/khue-van-cac' and d['fileUrl'].startswith('/files/models/')" 20-tai-len-va-hien.json
step 21-chi-tiet-co-nut    200 GET "/api/v1/public/heritages/$SLUG"
check 21-co-nut-xem-3d "d['hasModel'] is True" 21-chi-tiet-co-nut.json
step 22-mo-hinh-cong-khai  200 GET "/api/v1/public/heritages/$SLUG/model"
check 22-trinh-xem "d['id']==$M1 and d['contentType']=='model/gltf-binary' and d['fileSizeBytes']==$SIZE and d['author']=='Nguyễn Văn An' and d['license']=='CC BY 4.0' and d['sourceUrl']=='https://sketchfab.com/m/khue-van-cac'" 22-mo-hinh-cong-khai.json
checkfile 23-tai-tep-khong-dang-nhap 200 model/gltf-binary "$SIZE" "$FILE1"
step 24-ds-quan-tri-co-3d  200 GET "/api/v1/admin/heritages?hasModel=true&pageSize=100" "" "$ADMIN"
check 24-loc-co-3d "any(i['id']==$H and i['hasModel'] is True for i in d['items'])" 24-ds-quan-tri-co-3d.json

# ---- Một mô hình hiển thị mỗi di sản ----
step 30-hien-them-bi-chan  422 POST "/api/v1/admin/heritages/$H/models/upload" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary" -F author=An -F license=CC0 -F sourceUrl=https://sketchfab.com/m/2 -F show=true
checkcode 30-ma-blocked BLOCKED 30-hien-them-bi-chan.json
step 31-tai-ban-nhap       201 POST "/api/v1/admin/heritages/$H/models/upload" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary" -F author=An -F license=CC0 -F sourceUrl=https://sketchfab.com/m/2
M2=$(val 31-tai-ban-nhap.json id)
check 31-la-ban-nhap "d['status']=='DRAFT' and d['isVisibleToPublic'] is False" 31-tai-ban-nhap.json
step 32-hien-ban-nhap-chan 422 POST "/api/v1/admin/models/$M2/show" empty.json "$ADMIN"
step 33-an-mo-hinh-dau     200 POST "/api/v1/admin/models/$M1/hide" empty.json "$ADMIN"
check 33-da-an "d['status']=='ARCHIVED' and d['statusLabel']=='Đã ẩn' and d['isVisibleToPublic'] is False" 33-an-mo-hinh-dau.json
step 34-khach-khong-thay   404 GET "/api/v1/public/heritages/$SLUG/model"
step 35-hien-mo-hinh-hai   200 POST "/api/v1/admin/models/$M2/show" empty.json "$ADMIN"
step 36-khach-thay-mo-hinh-hai 200 GET "/api/v1/public/heritages/$SLUG/model"
check 36-la-mo-hinh-hai "d['id']==$M2 and d['license']=='CC0'" 36-khach-thay-mo-hinh-hai.json
step 37-an-roi-an-nua      422 POST "/api/v1/admin/models/$M1/hide" empty.json "$ADMIN"

# ---- Sửa ghi công, thay tệp ----
json sua-ghi-cong.json '{"title":"Khuê Văn Các (bản sửa)","author":"Trần Thị Bình","license":"CC BY-SA 4.0","sourceUrl":"https://example.org/mo-hinh","sortOrder":1}'
step 40-sua-ghi-cong       200 PUT "/api/v1/admin/models/$M2" sua-ghi-cong.json "$ADMIN"
check 40-ghi-cong-moi "d['credit']=='Trần Thị Bình · CC BY-SA 4.0 · https://example.org/mo-hinh' and d['fileUrl'].startswith('/files/models/')" 40-sua-ghi-cong.json
json giay-phep-xau.json '{"author":"An","license":"CC BY-ND 4.0","sourceUrl":"https://example.org/mo-hinh"}'
step 41-sua-giay-phep-xau  400 PUT "/api/v1/admin/models/$M2" giay-phep-xau.json "$ADMIN"
step 42-thay-tep           200 PUT "/api/v1/admin/models/$M2/file" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary"
FILE2=$(val 42-thay-tep.json fileUrl)
check 42-tep-moi "d['fileUrl']!='$FILE1' and d['credit'].startswith('Trần Thị Bình')" 42-thay-tep.json
step 43-thay-tep-hong      400 PUT "/api/v1/admin/models/$M2/file" "" "$ADMIN" -F "file=@khong-phai-glb.glb;type=model/gltf-binary"
checkfile 44-tep-cu-con-trong-kho 200 model/gltf-binary "$SIZE" "$FILE1"

# ---- Khai báo bằng đường dẫn đã lưu ----
step 50-tai-vao-kho        201 POST "/api/v1/admin/files/models" "" "$ADMIN" -F "file=@mo-hinh.glb;type=model/gltf-binary"
json trang-ngoai.json '{"fileUrl":"https://evil.example/mo-hinh.glb","author":"An","license":"CC0","sourceUrl":"https://example.org/m"}'
step 51-trang-ngoai-bi-tu-choi 400 POST "/api/v1/admin/heritages/$H/models" trang-ngoai.json "$ADMIN"
# Đường dẫn trong kho đi thẳng từ tệp phản hồi sang tệp body (Git Bash trên Windows đổi tham số hoặc biến môi trường dạng "/files/..." thành đường dẫn tệp).
python -c "
import json
url=json.load(open('50-tai-vao-kho.json',encoding='utf-8'))['data']['url']
json.dump({'fileUrl':url,'fileSizeBytes':1,'author':'An','license':'Public domain','sourceUrl':'https://example.org/m','title':'Theo đường dẫn'},open('theo-duong-dan.json','w',encoding='utf-8'),ensure_ascii=False)"
step 52-khai-bao-duong-dan 201 POST "/api/v1/admin/heritages/$H/models" theo-duong-dan.json "$ADMIN"
M3=$(val 52-khai-bao-duong-dan.json id)
check 52-dung-luong-do-o-may-chu "d['fileSizeBytes']==$SIZE and d['status']=='DRAFT'" 52-khai-bao-duong-dan.json

# ---- Mô hình đi theo di sản ----
step 60-an-di-san          200 POST "/api/v1/admin/heritages/$H/archive" empty.json "$ADMIN"
step 61-di-san-an-mo-hinh-an 404 GET "/api/v1/public/heritages/$SLUG/model"
step 62-danh-sach-van-con  200 GET "/api/v1/admin/heritages/$H/models" "" "$ADMIN"
check 62-hai-ban "any(m['id']==$M2 and m['status']=='PUBLISHED' and m['isVisibleToPublic'] is False for m in d)" 62-danh-sach-van-con.json
step 63-hien-lai-di-san    200 POST "/api/v1/admin/heritages/$H/restore" empty.json "$ADMIN"
step 64-mo-hinh-hien-lai   200 GET "/api/v1/public/heritages/$SLUG/model"
check 64-la-mo-hinh-hai "d['id']==$M2" 64-mo-hinh-hien-lai.json

# ---- Gỡ ----
step 70-go-mo-hinh-hai     200 DELETE "/api/v1/admin/models/$M2" "" "$ADMIN"
step 71-khach-khong-thay   404 GET "/api/v1/public/heritages/$SLUG/model"
step 72-go-hai-lan         404 DELETE "/api/v1/admin/models/$M2" "" "$ADMIN"
checkfile 73-tep-van-con   200 model/gltf-binary "$SIZE" "$FILE2"
step 74-hien-lai-mo-hinh-mot 200 POST "/api/v1/admin/models/$M1/show" empty.json "$ADMIN"

# ---- Nhật ký thao tác ----
step 80-nhat-ky            200 GET "/api/v1/admin/audit-logs?action=HERITAGE_MODEL_CHANGED&pageSize=100" "" "$ADMIN"
check 80-co-du-viec "d['totalItems']>=9 and all(i['targetType']=='heritage_models' and '/files/' not in (i.get('newValue') or '') for i in d['items'])" 80-nhat-ky.json
check 80-co-gan-go "{'added','shown','hidden','replaced','updated','removed'} <= {__import__('json').loads(i['newValue'])['action'] for i in d['items']}" 80-nhat-ky.json

echo
echo "Tổng: $((PASS+FAIL)) bước, đúng $PASS, sai $FAIL"
[ "$FAIL" = "0" ]
