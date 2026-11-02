"""Tạo dữ liệu cho kịch bản tải k6 qua API của backend: 5 lớp khối 8, mỗi lớp 40 học sinh (đã đổi mật khẩu), mỗi lớp một bài học đã phát hành
và ba bài kiểm tra 20 câu (một bài cho cả lớp vào làm, một bài cho lưu câu trả lời, một bài cho nộp bài). Ghi kết quả ra du-lieu/du-lieu.json (ngoài git).

CHỈ chạy lên backend thử (máy cục bộ hoặc máy thử). Không chạy lên máy chủ thật khi chưa được người phụ trách đồng ý.

Chạy lại khi tệp kết quả đã có: không tạo thêm lớp và học sinh, chỉ tạo "đợt bài mới" (bài học và ba bài kiểm tra mới) cho các lớp cũ,
vì mỗi lượt làm bài chỉ nộp được một lần. Muốn tạo lại từ đầu thì xóa tệp kết quả (và dùng cơ sở dữ liệu mới).

Biến môi trường:
  BASE              địa chỉ backend (mặc định http://localhost:5180)
  TEACHER_LOGIN     tên đăng nhập giáo viên (mặc định minhanh.demo, giáo viên do scripts/tao-csdl-demo.ps1 tạo)
  TEACHER_PASSWORD  mật khẩu giáo viên (bắt buộc; không in ra, không ghi ra tệp)
  STUDENT_PASSWORD  mật khẩu đặt cho học sinh tải (mặc định TaiThu2026a; 8-64 ký tự, có chữ hoa, chữ thường, chữ số)
  OUT               tệp kết quả (mặc định du-lieu/du-lieu.json cạnh script này)
Mã thoát: 0 xong; khác 0 là lỗi.
"""
import datetime
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("BASE", "http://localhost:5180").rstrip("/")
TEACHER_LOGIN = os.environ.get("TEACHER_LOGIN", "minhanh.demo")
TEACHER_PASSWORD = os.environ.get("TEACHER_PASSWORD")
STUDENT_PASSWORD = os.environ.get("STUDENT_PASSWORD", "TaiThu2026a")
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.environ.get("OUT") or os.path.join(HERE, "du-lieu", "du-lieu.json")

CLASS_COUNT = 5
STUDENTS_PER_CLASS = 40
QUESTIONS_PER_QUIZ = 20
QUIZ_KINDS = ["vaoLop", "luu", "nop"]


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
        sys.exit(f"{method} {path}: mong {expect}, nhận {status}: {str(payload)[:300]}")
    return payload.get("data"), status


def when(minutes):
    t = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=minutes)
    return t.strftime("%Y-%m-%dT%H:%M:%SZ")


def create_students(token, class_id, class_index):
    """Thêm 40 học sinh vào lớp, đổi mật khẩu tạm sang mật khẩu tải. Trả danh sách tài khoản."""
    rows = [{"rowNumber": i + 1, "fullName": f"Học Sinh Tải {class_index + 1}-{i + 1:02d}"} for i in range(STUDENTS_PER_CLASS)]
    result, _ = call("POST", f"/teacher/classes/{class_id}/students/import", {"rows": rows}, token=token, expect=201)
    entries = result["created"]["sheet"]["entries"]
    if len(entries) != STUDENTS_PER_CLASS:
        sys.exit(f"Lớp {class_index + 1}: tạo được {len(entries)} học sinh, cần {STUDENTS_PER_CLASS} (xem hạn mức của gói và sĩ số tối đa của lớp)")
    accounts = []
    for e in entries:
        session, _ = call("POST", "/student/auth/login", {"studentCode": e["studentCode"], "password": e["temporaryPassword"]}, expect=200)
        call("PUT", "/student/me/password", {"currentPassword": e["temporaryPassword"], "newPassword": STUDENT_PASSWORD,
                                              "confirmNewPassword": STUDENT_PASSWORD}, token=session["accessToken"], expect=200)
        accounts.append({"maHocSinh": e["studentCode"], "matKhau": STUDENT_PASSWORD, "lop": class_index})
    return accounts


def questions_payload(heritage_id, class_index, batch):
    out = []
    for n in range(QUESTIONS_PER_QUIZ):
        out.append({
            "questionType": "SINGLE_CHOICE",
            "heritageId": heritage_id,
            "content": f"Câu hỏi tải số {n + 1} của lớp {class_index + 1}, đợt {batch}: đáp án đúng là phương án nào?",
            "explanation": "Câu hỏi tạo tự động để đo hiệu năng, đáp án đúng là phương án thứ nhất.",
            "difficultyLevel": "EASY",
            "options": [{"content": "Phương án A", "isCorrect": True}, {"content": "Phương án B", "isCorrect": False},
                        {"content": "Phương án C", "isCorrect": False}, {"content": "Phương án D", "isCorrect": False}],
        })
    return out


def create_batch(token, class_id, class_index, heritage_id, batch):
    """Một bài học đã phát hành + 20 câu hỏi + ba bài kiểm tra đang mở dùng chung 20 câu đó."""
    lesson, _ = call("POST", "/teacher/lessons", {"classId": class_id, "title": f"Bài học tải lớp {class_index + 1} đợt {batch}",
                                                   "heritageIds": [heritage_id]}, token=token, expect=201)
    draft, _ = call("POST", f"/teacher/lessons/{lesson['id']}/ai/compose", {"length": "DETAILED"}, token=token, expect=200)
    body = {"summary": draft["summary"], "keyPoints": draft["keyPoints"], "sections": [
        {k: s[k] for k in ("type", "title", "body", "imageUrl", "milestoneYear", "eventId", "sourceCitation")} for s in draft["sections"]]}
    call("PUT", f"/teacher/lessons/{lesson['id']}", body, token=token, expect=200)
    published, _ = call("POST", f"/teacher/lessons/{lesson['id']}/publish", token=token, expect=200)

    question_ids = []
    for q in questions_payload(heritage_id, class_index, batch):
        created, _ = call("POST", "/teacher/questions", q, token=token, expect=201)
        question_ids.append(created["id"])

    quizzes = {}
    for kind in QUIZ_KINDS:
        quiz, _ = call("POST", "/teacher/quizzes", {
            "lessonId": lesson["id"], "name": f"Kiểm tra tải {kind} lớp {class_index + 1} đợt {batch}", "difficultyLevel": "EASY",
            "timeLimitMinutes": 30, "openAt": when(-1), "closeAt": when(60 * 24 * 30), "showAnswersAfterSubmit": False}, token=token, expect=201)
        call("POST", f"/teacher/quizzes/{quiz['id']}/questions", {"questionIds": question_ids, "point": 1}, token=token, expect=200)
        call("POST", f"/teacher/quizzes/{quiz['id']}/publish", token=token, expect=200)
        quizzes[kind] = quiz["id"]
    return {"maBaiHoc": published["accessCode"], "baiKiemTra": quizzes}


def main():
    if not TEACHER_PASSWORD:
        sys.exit("Thiếu biến môi trường TEACHER_PASSWORD (mật khẩu giáo viên trên backend thử)")
    login, status = call("POST", "/auth/login", {"emailOrUsername": TEACHER_LOGIN, "password": TEACHER_PASSWORD})
    if status != 200:
        sys.exit(f"Đăng nhập giáo viên {TEACHER_LOGIN} thất bại ({status})")
    token = login["accessToken"]

    heritages, _ = call("GET", "/public/heritages?page=1&pageSize=1", expect=200)
    if not heritages["items"]:
        sys.exit("Backend chưa có di sản đã xuất bản (chạy scripts/tao-csdl-demo.ps1 trước)")
    heritage_id = heritages["items"][0]["id"]

    state = {"lop": [], "hocSinh": [], "dot": 0}
    if os.path.exists(OUT):
        with open(OUT, encoding="utf-8") as f:
            state = json.load(f)
        print(f"Đã có {OUT}: giữ lớp và học sinh cũ, tạo đợt bài mới.")
    else:
        for i in range(CLASS_COUNT):
            c, _ = call("POST", "/teacher/classes", {"name": f"Lop-Tai-{i + 1}", "grade": 8, "schoolYear": "2026-2027"}, token=token, expect=201)
            state["lop"].append({"ten": c["name"], "lopId": c["id"]})
            state["hocSinh"].extend(create_students(token, c["id"], i))
            print(f"Lớp {i + 1}: {STUDENTS_PER_CLASS} học sinh")

    state["dot"] = state.get("dot", 0) + 1
    for i, lop in enumerate(state["lop"]):
        lop.update(create_batch(token, lop["lopId"], i, heritage_id, state["dot"]))
        print(f"Lớp {i + 1}: bài học {lop['maBaiHoc']}, ba bài kiểm tra {lop['baiKiemTra']}")

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=2)
    print("Đã ghi", OUT, "(đợt", state["dot"], ", chứa mật khẩu học sinh: không đưa vào git)")


main()
