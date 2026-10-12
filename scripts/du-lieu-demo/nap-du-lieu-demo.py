"""Nạp dữ liệu demo cố định vào backend đang chạy (qua API), dùng lại được trên một cơ sở dữ liệu mới.

Thường được gọi bởi scripts/tao-csdl-demo.ps1 (script này bật backend, chờ sẵn sàng rồi gọi file này). Có thể chạy riêng khi đã có backend.

Dữ liệu tạo ra (tên cố định, không có hậu tố giờ):
  - xuất bản các di sản đã nạp từ tệp JSON; bản dịch và tóm tắt cho 12 di sản tiêu biểu (AI giả lập: Việt, Anh được duyệt, Hàn, Trung, Nhật là dịch máy);
    mô hình 3D thật (giấy phép mở) cho các di sản có trong seed-data/mo-hinh-3d.json khi tệp .glb đã được tải về seed-data/mo-hinh-3d/
    (scripts/du-lieu-demo/tai-mo-hinh-3d.py); chưa có tệp nào thì gắn mô hình mẫu tối thiểu cho 2 di sản như trước;
  - một trường demo (gói Miễn phí), 1 quản trị trường, 2 giáo viên, 3 lớp khối 3, 8, 11, mỗi lớp 8 học sinh đã đổi mật khẩu sang mật khẩu demo;
  - mỗi lớp 2 bài học đã phát hành (có mã bài học), ngân hàng câu hỏi theo cấp, mỗi bài học 1 bài kiểm tra đang mở tới 30/06/2027;
  - 5 học sinh mỗi lớp đã làm và nộp bài kiểm tra thứ nhất, 3 em đã làm bài thứ hai, kết quả khác nhau; số còn lại chưa làm.

Biến môi trường:
  BASE            địa chỉ backend (mặc định http://localhost:5180)
  API_LOG         tệp log của backend (đọc liên kết đặt mật khẩu; backend phải chạy với Email__Mode=Log)
  ADMIN_LOGIN     tên đăng nhập quản trị hệ thống dùng để nạp (mặc định admin.nho)
  ADMIN_PASSWORD  mật khẩu của tài khoản đó (không ghi ra tệp, không in)
  OUT             tệp JSON kết quả (mã học sinh, mã bài học...; không có mật khẩu quản trị)
  MO_HINH_DIR     thư mục chứa tệp .glb (mặc định seed-data/mo-hinh-3d của repo)
Mã thoát: 0 xong; 3 cơ sở dữ liệu đã có dữ liệu demo (không nạp trùng); khác 0 là lỗi.

Chạy với tham số --chi-mo-hinh để chỉ gắn mô hình 3D thật vào một cơ sở dữ liệu đã nạp từ trước (không cần API_LOG): gỡ mô hình mẫu,
tải mô hình thật lên và cho hiện; di sản đã có mô hình cùng trang nguồn thì bỏ qua, nên chạy lại nhiều lần không bị trùng.
"""
import datetime
import json
import os
import re
import struct
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = os.environ.get("BASE", "http://localhost:5180").rstrip("/")
API_LOG = os.environ.get("API_LOG")
ADMIN_LOGIN = os.environ.get("ADMIN_LOGIN", "admin.nho")
ADMIN_PASSWORD = os.environ["ADMIN_PASSWORD"]
OUT = os.environ.get("OUT", "du-lieu-demo.json")

DEMO_PASSWORD = "DemoDsvhvn2026"
SCHOOL_NAME = "Trường Liên cấp Demo Di sản Việt"
CLOSE_AT = "2027-06-30T16:59:00Z"  # hết ngày 30/06/2027 giờ Việt Nam
EMAIL_DOMAIN = "demo.dsvhvn.local"

ORG_ADMIN = {"fullName": "Nguyễn Thị Hương Giang", "username": "huonggiang.demo", "email": f"huonggiang@{EMAIL_DOMAIN}"}
TEACHERS = [
    {"fullName": "Trần Thị Minh Anh", "username": "minhanh.demo", "email": f"minhanh@{EMAIL_DOMAIN}"},
    {"fullName": "Lê Hoàng Nam", "username": "hoangnam.demo", "email": f"hoangnam@{EMAIL_DOMAIN}"},
]
STUDENT_NAMES = {
    3: ["Nguyễn Minh Khang", "Trần Gia Hân", "Lê Bảo Nam", "Phạm Khánh Linh", "Hoàng Anh Tuấn", "Vũ Thảo My", "Đặng Quốc Bảo", "Bùi Ngọc Diệp"],
    8: ["Ngô Đức Anh", "Dương Thùy Chi", "Lý Hoàng Long", "Đỗ Phương Thảo", "Trịnh Minh Quân", "Phan Hải Yến", "Võ Thành Đạt", "Đinh Mai Phương"],
    11: ["Nguyễn Hữu Phúc", "Lê Thị Kim Ngân", "Trần Quang Vinh", "Phạm Thu Hà", "Hồ Gia Bảo", "Mai Xuân Trang", "Cao Tiến Dũng", "Lương Thanh Tâm"],
}
CLASSES = [(3, "3A", 0), (8, "8A1", 0), (11, "11A2", 1)]  # (khối, tên lớp, chỉ số giáo viên phụ trách)
# Di sản được dịch, tóm tắt (12 di sản tiêu biểu theo mã nạp từ tệp JSON) và di sản gắn mô hình 3D mẫu khi chưa có tệp mô hình thật.
TRANSLATED_HERITAGE_IDS = [1, 2, 5, 6, 7, 9, 11, 12, 14, 15, 16, 17]
MODEL_HERITAGE_IDS = [1, 14]
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MODEL_LIST = os.path.join(REPO, "seed-data", "mo-hinh-3d.json")
MODEL_DIR = os.environ.get("MO_HINH_DIR") or os.path.join(REPO, "seed-data", "mo-hinh-3d")
SAMPLE_MODEL_AUTHOR = "Mô hình mẫu để thử tính năng xem 3D"
# Giấy phép CC BY yêu cầu ghi rõ đã chỉnh sửa: mọi mô hình đều được giảm chi tiết và nén lại trước khi tải lên.
MODIFIED_NOTE = ", đã nén để xem trên web"
# Số em đã nộp bài: bài kiểm tra thứ nhất 5 em, bài thứ hai 3 em; mức đúng giảm dần để điểm khác nhau.
DONE_COUNT = [5, 3]


def call(method, path, body=None, token=None, expect=None):
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + "/api/v1" + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            status, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        status, raw = e.code, e.read()
    try:
        payload = json.loads(raw.decode("utf-8")) if raw else {}
    except ValueError:
        payload = {}
    if expect is not None and status != expect:
        sys.exit(f"{method} {path}: mong {expect}, nhận {status}: {str(payload)[:400]}")
    return payload.get("data"), status


def mail_token(email):
    """Lấy mã trong liên kết đặt mật khẩu từ log backend (Email__Mode=Log)."""
    if not API_LOG:
        sys.exit("thiếu biến môi trường API_LOG (tệp log của backend)")
    email = email.lower()
    for _ in range(60):
        text = open(API_LOG, encoding="utf-8", errors="replace").read()
        pos = text.lower().rfind("tới: " + email)
        if pos < 0:
            pos = text.lower().rfind(": " + email + " |")
        if pos >= 0:
            m = re.search(r"token=([A-Za-z0-9_\-%]+)", text[pos:])
            if m:
                return m.group(1)
        time.sleep(0.3)
    sys.exit("không thấy thư tới " + email + " trong log backend (backend phải chạy với Email__Mode=Log)")


def when(minutes):
    t = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=minutes)
    return t.strftime("%Y-%m-%dT%H:%M:%SZ")


def minimal_glb():
    """Mô hình glTF nhị phân hợp lệ nhỏ nhất vẽ được: một tam giác."""
    gltf = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}],
            "meshes": [{"primitives": [{"attributes": {"POSITION": 0}, "material": 0}]}],
            "materials": [{"pbrMetallicRoughness": {"baseColorFactor": [0.65, 0.1, 0.1, 1], "metallicFactor": 0}, "doubleSided": True}],
            "accessors": [{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0]}],
            "bufferViews": [{"buffer": 0, "byteLength": 36, "target": 34962}], "buffers": [{"byteLength": 36}]}
    doc = json.dumps(gltf).encode()
    doc += b" " * ((4 - len(doc) % 4) % 4)
    binary = struct.pack("<9f", 0, 0, 0, 1, 0, 0, 0, 1, 0)
    total = 12 + 8 + len(doc) + 8 + len(binary)
    return (struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(doc), 0x4E4F534A) + doc
            + struct.pack("<II", len(binary), 0x004E4942) + binary)


def upload_model(token, heritage_id, fields, content, filename):
    """Tải tệp .glb lên tab Mô hình 3D của di sản kèm ghi công (multipart/form-data)."""
    boundary = "----dsvhvn" + uuid.uuid4().hex
    crlf = bytes([13, 10])
    body = b""
    for name, value in fields:
        body += (f"--{boundary}").encode() + crlf + f'Content-Disposition: form-data; name="{name}"'.encode() + crlf + crlf
        body += value.encode("utf-8") + crlf
    body += (f"--{boundary}").encode() + crlf
    body += f'Content-Disposition: form-data; name="file"; filename="{filename}"'.encode() + crlf
    body += b"Content-Type: model/gltf-binary" + crlf + crlf + content + crlf
    body += (f"--{boundary}--").encode() + crlf
    req = urllib.request.Request(f"{BASE}/api/v1/admin/heritages/{heritage_id}/models/upload", data=body, method="POST",
                                 headers={"Authorization": "Bearer " + token, "Content-Type": f"multipart/form-data; boundary={boundary}"})
    try:
        urllib.request.urlopen(req, timeout=300).read()
    except urllib.error.HTTPError as e:
        sys.exit(f"tải mô hình 3D lên di sản {heritage_id}: {e.code} {e.read()[:300]}")


def real_models():
    """Mô hình chọn chính trong seed-data/mo-hinh-3d.json đã có tệp .glb trong thư mục mô hình."""
    if not os.path.exists(MODEL_LIST):
        return []
    with open(MODEL_LIST, encoding="utf-8") as f:
        items = json.load(f)
    return [(m, os.path.join(MODEL_DIR, m["file"])) for m in items
            if m["role"] == "chinh" and os.path.isfile(os.path.join(MODEL_DIR, m["file"]))]


def attach_models(token, published):
    """Gắn mô hình 3D cho các di sản đã xuất bản, trả về mã các di sản có mô hình đang hiển thị.

    Có tệp mô hình thật: gỡ mô hình mẫu cũ, tải mô hình thật lên và cho hiện. Di sản đã có mô hình cùng trang nguồn thì bỏ qua;
    di sản đang hiện một mô hình khác do người dùng tự thêm thì mô hình thật chỉ được lưu nháp. Chưa có tệp nào thì gắn mô hình mẫu như trước.
    """
    found = real_models()
    if not found:
        print(f"Chưa có tệp mô hình 3D thật trong {MODEL_DIR}: gắn mô hình mẫu tối thiểu")
        sample = [("author", SAMPLE_MODEL_AUTHOR), ("license", "CC0 1.0"),
                  ("sourceUrl", "https://creativecommons.org/publicdomain/zero/1.0/"), ("show", "true")]
        shown = []
        for hid in MODEL_HERITAGE_IDS:
            if hid in published:
                title = "Mô hình mẫu - " + published[hid]["name"][:60]
                upload_model(token, hid, [("title", title)] + sample, minimal_glb(), "mo-hinh-mau.glb")
                shown.append(hid)
        return shown

    by_slug = {h["slug"]: h["id"] for h in published.values()}
    shown, skipped = [], []
    for m, path in found:
        hid = by_slug.get(m["heritageSlug"])
        if hid is None:
            skipped.append(m["heritageSlug"])
            continue
        existing, _ = call("GET", f"/admin/heritages/{hid}/models", token=token, expect=200)
        if any(x["sourceUrl"] == m["sourceUrl"] for x in existing):
            shown.append(hid)
            continue
        for x in existing:
            if x["author"] == SAMPLE_MODEL_AUTHOR:
                call("DELETE", f"/admin/models/{x['id']}", token=token, expect=200)
        other_visible = any(x["author"] != SAMPLE_MODEL_AUTHOR and x["status"] == "PUBLISHED" for x in existing)
        license_name = m["license"] + ("" if m["license"].startswith("CC0") else MODIFIED_NOTE)
        fields = [("title", m["title"]), ("author", m["author"]), ("license", license_name), ("licenseUrl", m["licenseUrl"]),
                  ("sourceUrl", m["sourceUrl"]), ("show", "false" if other_visible else "true")]
        with open(path, "rb") as f:
            upload_model(token, hid, fields, f.read(), m["file"])
        if other_visible:
            print(f"  {m['title']}: di sản đang hiện mô hình khác, mô hình thật chỉ lưu nháp")
        else:
            shown.append(hid)
    if skipped:
        print(f"  Bỏ qua {len(skipped)} mô hình vì di sản chưa xuất bản: {', '.join(skipped)}")
    return shown


def main_models_only():
    admin, st = call("POST", "/auth/login", {"emailOrUsername": ADMIN_LOGIN, "password": ADMIN_PASSWORD})
    if st != 200:
        sys.exit(f"Đăng nhập quản trị hệ thống {ADMIN_LOGIN} thất bại ({st}).")
    pub, _ = call("GET", "/admin/heritages?status=PUBLISHED&pageSize=100", token=admin["accessToken"], expect=200)
    published = {h["id"]: h for h in pub["items"]}
    shown = attach_models(admin["accessToken"], published)
    print(f"Có mô hình 3D đang hiển thị ở {len(shown)} di sản")


# ---------------------------------------------------------------------------------------------------------------------
# Câu hỏi theo cấp. Mỗi mục: lời câu, dạng, phần riêng của dạng, đáp án đúng (dùng để chấm thử khi cho học sinh làm bài).
# Ảnh lấy theo từ khoá trong chú thích ảnh của di sản (ảnh phải nằm trong thư viện ảnh của di sản được gắn).
# ---------------------------------------------------------------------------------------------------------------------
def question_sets():
    """Trả về danh sách bài học: mỗi bài có khối, tiêu đề, mã di sản, bài kiểm tra và các câu hỏi (dựng khi đã có ảnh)."""
    return [
        {"grade": 3, "title": "Hoàng thành Thăng Long qua hình ảnh", "heritage": 1, "quiz": "Trò chơi khám phá Hoàng thành Thăng Long",
         "questions": lambda img: [
             {"type": "TRUE_FALSE", "difficulty": "EASY", "content": "Em hãy xem hình: đây là Đoan Môn, cửa phía nam dẫn vào khu cung cấm của Hoàng thành.",
              "explanation": "Đúng, đây là Đoan Môn của Hoàng thành Thăng Long.", "imageUrl": img("Đoan Môn"), "answerIsTrue": True},
             {"type": "TRUE_FALSE", "difficulty": "EASY", "content": "Em hãy xem hình: đây là cổng Đoan Môn của Hoàng thành.",
              "explanation": "Hình này là khu điện Kính Thiên, không phải cổng Đoan Môn.", "imageUrl": img("Kính Thiên"), "answerIsTrue": False},
             {"type": "IMAGE_CHOICE", "difficulty": "EASY", "content": "Em hãy chọn hình có tên: Điện Kính Thiên.",
              "explanation": "Hình thứ hai là khu điện Kính Thiên với bậc thềm rồng đá.",
              "options": [{"content": "Đoan Môn", "imageUrl": img("Đoan Môn"), "isCorrect": False},
                          {"content": "Điện Kính Thiên", "imageUrl": img("Kính Thiên"), "isCorrect": True},
                          {"content": "Khu khảo cổ", "imageUrl": img("khảo cổ 18 Hoàng Diệu", 0), "isCorrect": False}]},
             {"type": "MATCHING", "difficulty": "MEDIUM", "content": "Em hãy ghép mỗi hình với tên của hình đó.",
              "explanation": "Mỗi tên là chú thích của hình trong thư viện ảnh di sản.",
              "pairs": [{"leftImageUrl": img("Đoan Môn"), "right": "Đoan Môn"}, {"leftImageUrl": img("Kính Thiên"), "right": "Điện Kính Thiên"},
                        {"leftImageUrl": img("Hố khai quật"), "right": "Hố khai quật"}, {"leftImageUrl": img("đất nung"), "right": "Vật liệu đất nung"}]},
             {"type": "ORDERING", "difficulty": "MEDIUM", "content": "Hãy sắp xếp các sự kiện của Hoàng thành Thăng Long theo thứ tự thời gian.",
              "explanation": "Xếp theo năm: dời đô 1010, dựng điện Kính Thiên thế kỷ 15, xếp hạng đặc biệt 2009, UNESCO 2010.",
              "steps": ["Năm 1010: vua Lý Thái Tổ dời đô ra Thăng Long", "Thế kỷ 15: triều Lê sơ dựng điện Kính Thiên",
                        "Năm 2009: xếp hạng di tích quốc gia đặc biệt", "Năm 2010: UNESCO ghi danh Di sản thế giới"]},
         ]},
        {"grade": 3, "title": "Vịnh Hạ Long và quần đảo Cát Bà", "heritage": 5, "quiz": "Trò chơi khám phá Vịnh Hạ Long",
         "questions": lambda img: [
             {"type": "TRUE_FALSE", "difficulty": "EASY", "content": "Em hãy xem hình: đây là loài voọc sống ở Vườn quốc gia Cát Bà.",
              "explanation": "Đúng, đây là voọc Cát Bà.", "imageUrl": img("Voọc"), "answerIsTrue": True},
             {"type": "TRUE_FALSE", "difficulty": "EASY", "content": "Em hãy xem hình: đây là hang Sửng Sốt trên vịnh Hạ Long.",
              "explanation": "Hình này là vịnh Lan Hạ thuộc quần đảo Cát Bà, không phải hang Sửng Sốt.", "imageUrl": img("Lan Hạ"), "answerIsTrue": False},
             {"type": "IMAGE_CHOICE", "difficulty": "EASY", "content": "Em hãy chọn hình có tên: Hang Sửng Sốt.",
              "explanation": "Hình thứ hai là hang Sửng Sốt.",
              "options": [{"content": "Đảo đá vôi", "imageUrl": img("Đảo đá vôi"), "isCorrect": False},
                          {"content": "Hang Sửng Sốt", "imageUrl": img("Hang Sửng Sốt"), "isCorrect": True},
                          {"content": "Vịnh Lan Hạ", "imageUrl": img("Lan Hạ"), "isCorrect": False}]},
             {"type": "MATCHING", "difficulty": "MEDIUM", "content": "Em hãy ghép mỗi hình với tên của hình đó.",
              "explanation": "Mỗi tên là chú thích của hình trong thư viện ảnh di sản.",
              "pairs": [{"leftImageUrl": img("Đảo đá vôi"), "right": "Đảo đá vôi"}, {"leftImageUrl": img("Hang Sửng Sốt"), "right": "Hang Sửng Sốt"},
                        {"leftImageUrl": img("Lan Hạ"), "right": "Vịnh Lan Hạ"}, {"leftImageUrl": img("Voọc"), "right": "Voọc Cát Bà"}]},
             {"type": "ORDERING", "difficulty": "MEDIUM", "content": "Hãy sắp xếp các mốc của Vịnh Hạ Long theo thứ tự thời gian.",
              "explanation": "Xếp theo năm: 1994, 2000, 2023.",
              "steps": ["Năm 1994: UNESCO ghi danh vịnh Hạ Long", "Năm 2000: UNESCO công nhận thêm giá trị địa chất, địa mạo",
                        "Năm 2023: ghi danh Vịnh Hạ Long - Quần đảo Cát Bà"]},
         ]},
        {"grade": 8, "title": "Quần thể di tích Cố đô Huế", "heritage": 14, "quiz": "Kiểm tra Cố đô Huế",
         "questions": lambda img: [
             {"type": "SINGLE_CHOICE", "difficulty": "EASY", "content": "Quần thể di tích Cố đô Huế được UNESCO ghi danh Di sản văn hóa thế giới vào năm nào?",
              "explanation": "UNESCO ghi danh Cố đô Huế năm 1993, di sản thế giới đầu tiên của Việt Nam.",
              "options": [{"content": "Năm 1993", "isCorrect": True}, {"content": "Năm 1999", "isCorrect": False},
                          {"content": "Năm 2003", "isCorrect": False}, {"content": "Năm 2010", "isCorrect": False}]},
             {"type": "SINGLE_CHOICE", "difficulty": "MEDIUM", "content": "Lăng của vua Tự Đức ở Huế còn có tên gọi nào khác?",
              "explanation": "Lăng Tự Đức còn gọi là Khiêm lăng, xây từ năm 1864 đến năm 1867.",
              "options": [{"content": "Khiêm lăng", "isCorrect": True}, {"content": "Hiếu lăng", "isCorrect": False},
                          {"content": "Xương lăng", "isCorrect": False}, {"content": "Thiên Thọ lăng", "isCorrect": False}]},
             {"type": "TRUE_FALSE", "difficulty": "MEDIUM", "levelMin": 2, "levelMax": 2,
              "content": "Kinh thành Huế khởi công năm 1805 và hoàn thành năm 1832.",
              "explanation": "Đúng, Kinh thành Huế được xây từ năm 1805 đến năm 1832.", "answerIsTrue": True},
             {"type": "ORDERING", "difficulty": "HARD", "levelMin": 2, "levelMax": 2, "content": "Hãy sắp xếp các công trình của Cố đô Huế theo thứ tự thời gian xây dựng.",
              "explanation": "Xếp theo năm khởi công: Kinh thành 1805, Ngọ Môn 1833, lăng Minh Mạng 1840, lăng Tự Đức 1864.",
              "steps": ["Năm 1805: khởi công Kinh thành Huế", "Năm 1833: xây Ngọ Môn", "Năm 1840: khởi công lăng Minh Mạng", "Năm 1864: khởi công lăng Tự Đức"]},
             {"type": "FILL_BLANK", "difficulty": "MEDIUM", "levelMin": 2, "levelMax": 2,
              "content": "Cổng chính phía nam của Hoàng thành Huế là ___, trên có lầu Ngũ Phụng.",
              "explanation": "Ngọ Môn được xây năm 1833, trên có lầu Ngũ Phụng.",
              "blanks": [{"accepted": ["Ngọ Môn"]}], "distractors": ["Hiển Nhơn", "Đoan Môn"]},
         ]},
        {"grade": 8, "title": "Thành nhà Hồ", "heritage": 12, "quiz": "Kiểm tra Thành nhà Hồ",
         "questions": lambda img: [
             {"type": "SINGLE_CHOICE", "difficulty": "EASY", "content": "Ai là người cho xây dựng thành Tây Đô (thành nhà Hồ) vào năm 1397?",
              "explanation": "Hồ Quý Ly cho xây kinh thành đá ở đất Thanh Hóa năm 1397.",
              "options": [{"content": "Hồ Quý Ly", "isCorrect": True}, {"content": "Lê Lợi", "isCorrect": False},
                          {"content": "Lý Thái Tổ", "isCorrect": False}, {"content": "Trần Hưng Đạo", "isCorrect": False}]},
             {"type": "SINGLE_CHOICE", "difficulty": "EASY", "content": "Thành nhà Hồ nằm ở tỉnh nào của nước ta?",
              "explanation": "Thành nhà Hồ ở tỉnh Thanh Hóa.",
              "options": [{"content": "Thanh Hóa", "isCorrect": True}, {"content": "Nghệ An", "isCorrect": False},
                          {"content": "Ninh Bình", "isCorrect": False}, {"content": "Hà Tĩnh", "isCorrect": False}]},
             {"type": "TRUE_FALSE", "difficulty": "MEDIUM", "levelMin": 2, "levelMax": 2,
              "content": "Thành nhà Hồ được UNESCO ghi danh Di sản văn hóa thế giới vào năm 2011.",
              "explanation": "Đúng, UNESCO ghi danh Thành nhà Hồ năm 2011.", "answerIsTrue": True},
             {"type": "ORDERING", "difficulty": "HARD", "levelMin": 2, "levelMax": 2, "content": "Hãy sắp xếp các mốc của Thành nhà Hồ theo thứ tự thời gian.",
              "explanation": "Xếp theo năm: 1397, 1407, 1962, 2011.",
              "steps": ["Năm 1397: Hồ Quý Ly cho xây thành Tây Đô", "Năm 1407: nhà Hồ sụp đổ", "Năm 1962: xếp hạng di tích quốc gia", "Năm 2011: UNESCO ghi danh"]},
             {"type": "FILL_BLANK", "difficulty": "MEDIUM", "levelMin": 2, "levelMax": 2,
              "content": "Thành nhà Hồ nổi tiếng với kỹ thuật ghép những khối ___ lớn nặng hàng chục tấn.",
              "explanation": "Tường thành được ghép bằng những khối đá lớn.",
              "blanks": [{"accepted": ["đá", "đá vôi"]}], "distractors": ["gỗ", "gạch"]},
         ]},
        {"grade": 11, "title": "Khu di tích Đền Hùng", "heritage": 9, "quiz": "Kiểm tra Đền Hùng",
         "questions": lambda img: [
             {"type": "SINGLE_CHOICE", "difficulty": "EASY", "levelMin": 3, "levelMax": 3, "content": "Giỗ Tổ Hùng Vương được tổ chức vào ngày nào hằng năm?",
              "explanation": "Ngày Giỗ Tổ chính thức là mùng 10 tháng 3 âm lịch.",
              "options": [{"content": "Mùng 10 tháng 3 âm lịch", "isCorrect": True}, {"content": "Mùng 10 tháng 3 dương lịch", "isCorrect": False},
                          {"content": "Rằm tháng Giêng", "isCorrect": False}, {"content": "Mùng 10 tháng 4 âm lịch", "isCorrect": False}]},
             {"type": "SINGLE_CHOICE", "difficulty": "MEDIUM", "levelMin": 3, "levelMax": 3, "content": "Khu di tích Đền Hùng nằm trên ngọn núi nào ở Phú Thọ?",
              "explanation": "Khu di tích nằm trên núi Nghĩa Lĩnh, xã Hy Cương.",
              "options": [{"content": "Núi Nghĩa Lĩnh", "isCorrect": True}, {"content": "Núi Ba Vì", "isCorrect": False},
                          {"content": "Núi Tam Đảo", "isCorrect": False}, {"content": "Núi Yên Tử", "isCorrect": False}]},
             {"type": "MULTI_TRUE_FALSE", "difficulty": "MEDIUM", "content": "Em hãy đọc các nhận định về khu di tích Đền Hùng và xác định đúng hoặc sai.",
              "explanation": "Ngày Giỗ Tổ là mùng 10 tháng 3 âm lịch, nên nhận định về dương lịch là sai.",
              "statements": [{"text": "Đền Hùng được xếp hạng di tích quốc gia đặc biệt vào năm 2009.", "isTrue": True},
                             {"text": "Đền Hùng thuộc địa phận tỉnh Phú Thọ.", "isTrue": True},
                             {"text": "Ngày Giỗ Tổ chính thức là mùng 10 tháng 3 dương lịch.", "isTrue": False},
                             {"text": "Đền Hùng thờ các Vua Hùng, những người có công dựng nước.", "isTrue": True}]},
             {"type": "MULTI_TRUE_FALSE", "difficulty": "HARD", "content": "Em hãy đọc các nhận định về lịch sử Đền Hùng và xác định đúng hoặc sai.",
              "explanation": "Đền Mẫu Âu Cơ chỉ được khởi dựng năm 2001, không phải thế kỷ 15.",
              "statements": [{"text": "Bản ngọc phả về các Vua Hùng được sao chép, đóng dấu dưới đời vua Lê Thánh Tông.", "isTrue": True},
                             {"text": "Đền Mẫu Âu Cơ được khởi dựng từ thế kỷ 15 cùng thời với ngọc phả.", "isTrue": False},
                             {"text": "Đền Lạc Long Quân được khánh thành vào năm 2009.", "isTrue": True}]},
             {"type": "SOURCE_READING", "difficulty": "MEDIUM", "content": "Theo đoạn tư liệu, bản ngọc phả về các Vua Hùng được sao chép dưới đời vua nào?",
              "explanation": "Đoạn tư liệu nêu rõ đời vua Lê Thánh Tông.",
              "passage": "Bản ngọc phả về các Vua Hùng được sao chép và đóng dấu vào đời vua Lê Thánh Tông, năm 1470, để lưu giữ tại Đền Hùng.",
              "passageSource": "Cục Di sản văn hóa, dsvh.gov.vn",
              "options": [{"content": "Lê Thánh Tông", "isCorrect": True}, {"content": "Lý Thái Tổ", "isCorrect": False},
                          {"content": "Gia Long", "isCorrect": False}, {"content": "Minh Mạng", "isCorrect": False}]},
         ]},
        {"grade": 11, "title": "Đô thị cổ Hội An", "heritage": 16, "quiz": "Kiểm tra Đô thị cổ Hội An",
         "questions": lambda img: [
             {"type": "SINGLE_CHOICE", "difficulty": "EASY", "levelMin": 3, "levelMax": 3, "content": "Đô thị cổ Hội An được UNESCO ghi danh Di sản văn hóa thế giới vào năm nào?",
              "explanation": "UNESCO ghi danh Đô thị cổ Hội An ngày 1 tháng 12 năm 1999.",
              "options": [{"content": "Năm 1999", "isCorrect": True}, {"content": "Năm 1993", "isCorrect": False},
                          {"content": "Năm 2003", "isCorrect": False}, {"content": "Năm 2010", "isCorrect": False}]},
             {"type": "SINGLE_CHOICE", "difficulty": "MEDIUM", "levelMin": 3, "levelMax": 3, "content": "Cây cầu gỗ có mái ở Hội An được chúa Nguyễn Phúc Chu đặt tên chữ là gì?",
              "explanation": "Tên chữ là Lai Viễn kiều, dân gian gọi là chùa Cầu.",
              "options": [{"content": "Lai Viễn kiều", "isCorrect": True}, {"content": "Phước Duyên", "isCorrect": False},
                          {"content": "Thê Húc", "isCorrect": False}, {"content": "Long Biên", "isCorrect": False}]},
             {"type": "SINGLE_CHOICE", "difficulty": "MEDIUM", "levelMin": 3, "levelMax": 3, "content": "Dự án tu bổ tổng thể chùa Cầu được khánh thành vào năm nào?",
              "explanation": "Dự án khởi công ngày 28/12/2022 và khánh thành ngày 3/8/2024.",
              "options": [{"content": "Năm 2024", "isCorrect": True}, {"content": "Năm 2022", "isCorrect": False},
                          {"content": "Năm 2019", "isCorrect": False}, {"content": "Năm 2010", "isCorrect": False}]},
             {"type": "MULTI_TRUE_FALSE", "difficulty": "MEDIUM", "content": "Em hãy đọc các nhận định về Đô thị cổ Hội An và xác định đúng hoặc sai.",
              "explanation": "Hội An được ghi danh năm 1999, nên nhận định về năm 2010 là sai.",
              "statements": [{"text": "Hội An là thương cảng sầm uất vào thế kỷ 17 và 18.", "isTrue": True},
                             {"text": "Khu phố cổ Hội An nằm bên sông Thu Bồn.", "isTrue": True},
                             {"text": "Khu phố cổ Hội An được UNESCO ghi danh vào năm 2010.", "isTrue": False},
                             {"text": "Chùa Cầu còn được gọi theo tên chữ là Lai Viễn kiều.", "isTrue": True}]},
             {"type": "SOURCE_READING", "difficulty": "MEDIUM", "content": "Theo đoạn tư liệu, Hội An ở thế kỷ 17 và 18 giữ vai trò gì?",
              "explanation": "Đoạn tư liệu nêu Hội An là trung tâm mậu dịch lớn nhất Đàng Trong.",
              "passage": "Thế kỷ 17 và 18, Hội An là trung tâm mậu dịch lớn nhất Đàng Trong, có phố Nhật và phố Khách.",
              "passageSource": "Cục Di sản văn hóa, dsvh.gov.vn",
              "options": [{"content": "Trung tâm mậu dịch lớn nhất Đàng Trong", "isCorrect": True}, {"content": "Kinh đô của nhà Nguyễn", "isCorrect": False},
                          {"content": "Trung tâm khai khoáng", "isCorrect": False}, {"content": "Căn cứ quân sự biên giới", "isCorrect": False}]},
         ]},
    ]


def expected_answer(q, shown, right):
    """Dựng câu trả lời (đúng hoặc sai) theo những gì học sinh thấy trên màn hình làm bài."""
    t = q["type"]

    def idx(items, text):
        return [i for i, x in enumerate(items) if (x["content"] if isinstance(x, dict) else x) == text][0]

    if t in ("SINGLE_CHOICE", "SOURCE_READING", "IMAGE_CHOICE"):
        correct = next(o["content"] for o in q["options"] if o["isCorrect"])
        wrong = next(o["content"] for o in q["options"] if not o["isCorrect"])
        return {"selectedIndex": idx(shown["options"], correct if right else wrong)}
    if t == "TRUE_FALSE":
        return {"selectedIndex": idx(shown["options"], "Đúng" if (q["answerIsTrue"] == right) else "Sai")}
    if t == "ORDERING":
        steps = q["steps"] if right else q["steps"][::-1]
        return {"order": [idx(shown["items"], s) for s in steps]}
    if t == "FILL_BLANK":
        return {"blanks": [q["blanks"][0]["accepted"][0] if right else "không biết"]}
    if t == "MULTI_TRUE_FALSE":
        truth = {s["text"]: s["isTrue"] for s in q["statements"]}
        return {"statements": [(truth[s] if isinstance(s, str) else truth[s["text"]]) == right for s in shown["statements"]]}
    if t == "MATCHING":
        by_image = {p["leftImageUrl"]: p["right"] for p in q["pairs"]}
        names = [p["right"] for p in q["pairs"]]
        matches = []
        for left in shown["leftItems"]:
            want = by_image[left["imageUrl"]]
            target = want if right else names[(names.index(want) + 1) % len(names)]
            matches.append(idx(shown["rightItems"], target))
        return {"matches": matches}
    raise ValueError(t)


def main():
    result = {"schoolName": SCHOOL_NAME}
    admin, st = call("POST", "/auth/login", {"emailOrUsername": ADMIN_LOGIN, "password": ADMIN_PASSWORD})
    if st != 200:
        sys.exit(f"Đăng nhập quản trị hệ thống {ADMIN_LOGIN} thất bại ({st}): mật khẩu không khớp với cơ sở dữ liệu này.")
    A = admin["accessToken"]

    orgs, _ = call("GET", "/admin/organizations?keyword=" + urllib.request.quote(SCHOOL_NAME), token=A, expect=200)
    if any(o["name"] == SCHOOL_NAME for o in orgs["items"]):
        print("Cơ sở dữ liệu đã có dữ liệu demo (trường demo đã tồn tại), không nạp trùng.")
        sys.exit(3)

    # ---- Di sản: xuất bản mọi di sản đủ điều kiện ----
    listing, _ = call("GET", "/admin/heritages?status=DRAFT&pageSize=100", token=A, expect=200)
    failed = []
    for h in sorted(listing["items"], key=lambda item: item["id"]):
        _, st = call("POST", f"/admin/heritages/{h['id']}/publish", token=A)
        if st != 200:
            failed.append(h["id"])
    pub, _ = call("GET", "/admin/heritages?status=PUBLISHED&pageSize=100", token=A, expect=200)
    published = {h["id"]: h for h in pub["items"]}
    result["heritagesPublished"] = len(published)
    result["heritagesNotPublished"] = failed
    print(f"Di sản đã xuất bản: {len(published)} (không xuất bản được: {failed})")

    # ---- Bản dịch và tóm tắt cho 12 di sản tiêu biểu ----
    translated = []
    for hid in TRANSLATED_HERITAGE_IDS:
        if hid not in published:
            continue
        call("POST", f"/admin/heritages/{hid}/translations/generate", {}, token=A, expect=200)
        call("POST", f"/admin/heritages/{hid}/translations/vi/approve", {"kind": "SUMMARY"}, token=A, expect=200)
        call("POST", f"/admin/heritages/{hid}/translations/en/approve", {"kind": "BOTH"}, token=A, expect=200)
        translated.append(hid)
    result["heritagesTranslated"] = translated
    print(f"Đã dịch và tóm tắt {len(translated)} di sản")

    # ---- Mô hình 3D ----
    models = attach_models(A, published)
    result["heritagesWith3d"] = models
    print(f"Đã gắn mô hình 3D cho {len(models)} di sản")

    # ---- Trường, quản trị trường, giáo viên ----
    call("POST", "/admin/organizations", {"name": SCHOOL_NAME, "address": "Hà Nội", "orgAdmin": ORG_ADMIN}, token=A, expect=201)
    call("POST", "/auth/set-password", {"token": mail_token(ORG_ADMIN["email"]), "newPassword": DEMO_PASSWORD}, expect=200)
    oa, _ = call("POST", "/auth/login", {"emailOrUsername": ORG_ADMIN["username"], "password": DEMO_PASSWORD}, expect=200)
    tokens = []
    for t in TEACHERS:
        call("POST", "/org/teachers", t, token=oa["accessToken"], expect=201)
        call("POST", "/auth/set-password", {"token": mail_token(t["email"]), "newPassword": DEMO_PASSWORD}, expect=200)
        lg, _ = call("POST", "/auth/login", {"emailOrUsername": t["username"], "password": DEMO_PASSWORD}, expect=200)
        tokens.append(lg["accessToken"])
    print("Đã tạo trường, quản trị trường và 2 giáo viên")

    # ---- Lớp và học sinh ----
    classes = {}
    students = {}
    for grade, name, teacher in CLASSES:
        c, _ = call("POST", "/teacher/classes", {"name": name, "grade": grade, "schoolYear": "2026-2027"}, token=tokens[teacher], expect=201)
        classes[grade] = c["id"]
        students[grade] = []
        for full_name in STUDENT_NAMES[grade]:
            d, _ = call("POST", f"/teacher/classes/{c['id']}/students", {"fullName": full_name}, token=tokens[teacher], expect=201)
            e = d["sheet"]["entries"][0]
            lg, _ = call("POST", "/student/auth/login", {"studentCode": e["studentCode"], "password": e["temporaryPassword"]}, expect=200)
            call("PUT", "/student/me/password", {"newPassword": DEMO_PASSWORD, "confirmNewPassword": DEMO_PASSWORD},
                 token=lg["accessToken"], expect=200)
            students[grade].append({"studentCode": e["studentCode"], "fullName": full_name})
    print("Đã tạo 3 lớp và 24 học sinh (đã đổi sang mật khẩu demo)")

    # ---- Bài học, câu hỏi, bài kiểm tra ----
    lessons = []
    for spec in question_sets():
        grade, hid = spec["grade"], spec["heritage"]
        teacher = next(t for g, _, t in CLASSES if g == grade)
        T = tokens[teacher]
        les, _ = call("POST", "/teacher/lessons", {"classId": classes[grade], "title": spec["title"], "heritageIds": [hid]}, token=T, expect=201)
        draft, _ = call("POST", f"/teacher/lessons/{les['id']}/ai/compose", {"length": "DETAILED"}, token=T, expect=200)
        body = {"summary": draft["summary"], "keyPoints": draft["keyPoints"], "sections": [
            {k: s[k] for k in ("type", "title", "body", "imageUrl", "milestoneYear", "eventId", "sourceCitation")} for s in draft["sections"]]}
        call("PUT", f"/teacher/lessons/{les['id']}", body, token=T, expect=200)
        pub_lesson, _ = call("POST", f"/teacher/lessons/{les['id']}/publish", token=T, expect=200)

        materials, _ = call("GET", f"/teacher/lessons/{les['id']}/materials", token=T, expect=200)
        images = materials["images"]

        def img(keyword, nth=0, _images=images):
            found = [i["url"] for i in _images if keyword.lower() in (i.get("caption") or "").lower()]
            if len(found) <= nth:
                sys.exit(f"Không thấy ảnh có chú thích chứa '{keyword}' trong thư viện ảnh của di sản {hid}")
            return found[nth]

        qdefs = spec["questions"](img)
        qids = []
        for q in qdefs:
            payload = {"questionType": q["type"], "heritageId": hid, "content": q["content"], "explanation": q["explanation"],
                       "difficultyLevel": q["difficulty"]}
            for key in ("imageUrl", "answerIsTrue", "options", "pairs", "steps", "blanks", "distractors", "statements", "passage",
                        "passageSource", "levelMin", "levelMax"):
                if key in q:
                    payload[key] = q[key]
            created, _ = call("POST", "/teacher/questions", payload, token=T, expect=201)
            qids.append(created["id"])
        limit = None if grade == 3 else (20 if grade == 8 else 30)
        quiz, _ = call("POST", "/teacher/quizzes", {"lessonId": les["id"], "name": spec["quiz"], "difficultyLevel": "MEDIUM",
                                                    "timeLimitMinutes": limit, "openAt": when(-1), "closeAt": CLOSE_AT,
                                                    "showAnswersAfterSubmit": True}, token=T, expect=201)
        call("POST", f"/teacher/quizzes/{quiz['id']}/questions", {"questionIds": qids, "point": 2}, token=T, expect=200)
        call("POST", f"/teacher/quizzes/{quiz['id']}/publish", token=T, expect=200)
        lessons.append({"grade": grade, "lessonId": les["id"], "title": spec["title"], "code": pub_lesson["accessCode"],
                        "quizId": quiz["id"], "quizName": spec["quiz"], "questions": qdefs, "questionIds": qids})
    print(f"Đã tạo {len(lessons)} bài học đã phát hành, {sum(len(x['questionIds']) for x in lessons)} câu hỏi, {len(lessons)} bài kiểm tra đang mở")

    # ---- Học sinh làm và nộp bài kiểm tra ----
    submitted = []
    by_grade = {}
    for item in lessons:
        by_grade.setdefault(item["grade"], []).append(item)
    for grade, items in by_grade.items():
        for quiz_no, item in enumerate(items):
            for j in range(DONE_COUNT[quiz_no]):
                student = students[grade][j]
                lg, _ = call("POST", "/student/auth/login", {"studentCode": student["studentCode"], "password": DEMO_PASSWORD}, expect=200)
                tok = lg["accessToken"]
                attempt, _ = call("POST", f"/student/quizzes/{item['quizId']}/attempts", token=tok, expect=201)
                shown_by_id = {sq["questionId"]: sq for sq in attempt["questions"]}
                by_qid = dict(zip(item["questionIds"], item["questions"]))
                # Mức đúng giảm dần theo thứ tự học sinh: em đầu đúng hết, các em sau sai xen kẽ nhiều hơn.
                target = max(1, len(item["questionIds"]) - j)
                correct_count = 0
                for i, qid in enumerate(item["questionIds"]):
                    right = ((i + j) % len(item["questionIds"])) < target
                    correct_count += right
                    call("PUT", f"/student/attempts/{attempt['id']}/answers/{qid}", expect=200, token=tok,
                         body=expected_answer(by_qid[qid], shown_by_id[qid], right))
                call("POST", f"/student/attempts/{attempt['id']}/submit", token=tok, expect=200)
                submitted.append({"studentCode": student["studentCode"], "quiz": item["quizName"], "correctQuestions": correct_count,
                                  "totalQuestions": len(item["questionIds"])})
    print(f"Đã có {len(submitted)} lượt làm bài được nộp")

    result["organizationAdmin"] = {k: ORG_ADMIN[k] for k in ("fullName", "username", "email")}
    result["teachers"] = TEACHERS
    result["classes"] = {str(g): {"name": n, "teacher": TEACHERS[t]["username"], "students": students[g]} for g, n, t in CLASSES}
    result["lessons"] = [{k: v for k, v in x.items() if k not in ("questions", "questionIds")} for x in lessons]
    result["submitted"] = submitted
    result["demoPassword"] = DEMO_PASSWORD
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
    print("Đã ghi", OUT)


if "--chi-mo-hinh" in sys.argv[1:]:
    main_models_only()
else:
    main()
