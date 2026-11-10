# -*- coding: utf-8 -*-
"""Sinh khối SQL nạp sẵn chủ đề chương trình môn học từ seed-data/chu-de-chuong-trinh.json.

Mỗi chủ đề là một câu lệnh EXEC(N'...') (bảng được tạo trong cùng migration nên câu lệnh dữ liệu phải chạy ở lô riêng): chèn chủ đề, rồi chèn liên kết
với di sản bằng INSERT ... SELECT nối theo heritages.slug (slug chưa có trong CSDL thì bỏ qua, nên CSDL trống chỉ có chủ đề).

Dùng:
  python scripts/chu-de-chuong-trinh/sinh_sql_chu_de.py                    # in các lệnh migrationBuilder.Sql(...) của C#
  python scripts/chu-de-chuong-trinh/sinh_sql_chu_de.py --sql              # in SQL thuần (chạy thử bằng sqlcmd)
  python scripts/chu-de-chuong-trinh/sinh_sql_chu_de.py --chen-vao <tệp migration .cs>
        # thay phần giữa hai dòng đánh dấu "nap-san-chu-de-bat-dau" và "nap-san-chu-de-ket-thuc" trong tệp migration

Khi đổi dữ liệu về sau: sửa tệp JSON rồi sinh lại cho MỘT MIGRATION MỚI (migration đã áp dụng không sửa).
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

GOC = Path(__file__).resolve().parents[2]
JSON_MAC_DINH = GOC / "seed-data" / "chu-de-chuong-trinh.json"
DONG_BAT_DAU = "// nap-san-chu-de-bat-dau"
DONG_KET_THUC = "// nap-san-chu-de-ket-thuc"


def chuoi(giatri: str | None) -> str:
    """Chữ Unicode trong câu lệnh SQL bên trong EXEC: nhân đôi dấu nháy đơn một lần."""
    if giatri is None:
        return "NULL"
    return "N'" + giatri.replace("'", "''") + "'"


def cau_lenh_chu_de(chu_de: dict) -> str:
    """Câu lệnh SQL (chưa bọc EXEC) chèn một chủ đề và liên kết với các di sản theo slug."""
    cot = "subject, grade, group_title, title, description, local_province, sort_order, source_url, is_active, created_at"
    gia_tri = ", ".join([
        chuoi(chu_de["subject"]), str(int(chu_de["grade"])), chuoi(chu_de["group"]), chuoi(chu_de["title"]),
        chuoi(chu_de.get("description")), "1" if chu_de.get("localProvince") else "0", str(int(chu_de["sortOrder"])),
        chuoi(chu_de.get("source")), "1", "SYSUTCDATETIME()",
    ])
    sql = f"DECLARE @id int; INSERT INTO curriculum_topics ({cot}) VALUES ({gia_tri}); SET @id = SCOPE_IDENTITY();"
    slugs = list(dict.fromkeys(chu_de.get("heritageSlugs") or []))
    if slugs:
        danh_sach = ", ".join(chuoi(s) for s in slugs)
        sql += f" INSERT INTO heritage_curriculum_topics (heritage_id, topic_id) SELECT h.id, @id FROM heritages h WHERE h.slug IN ({danh_sach});"
    return sql


def bao_exec(sql: str) -> str:
    """Bọc câu lệnh trong EXEC(N'...'): nhân đôi dấu nháy đơn lần nữa."""
    return "EXEC(N'" + sql.replace("'", "''") + "')"


def doc_chu_de(duong_dan: Path) -> list[dict]:
    du_lieu = json.loads(duong_dan.read_text(encoding="utf-8"))
    chu_de = du_lieu["topics"]
    khoa = [(c["grade"], c["subject"], c["group"], c["title"]) for c in chu_de]
    if len(set(khoa)) != len(khoa):
        raise SystemExit("Có hai chủ đề trùng (khối, môn, chủ đề lớn, nội dung).")
    return chu_de


def dong_csharp(chu_de: list[dict]) -> str:
    dong = []
    for c in chu_de:
        nhan = f"khối {c['grade']} · {c['subject']} · {c['title']}".replace("\r", " ").replace("\n", " ")
        sql = bao_exec(cau_lenh_chu_de(c)).replace('"', '""')
        dong.append(f"            // {nhan}")
        dong.append(f'            migrationBuilder.Sql(@"{sql}");')
        dong.append("")
    return "\n".join(dong).rstrip("\n")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--json", type=Path, default=JSON_MAC_DINH, help="tệp dữ liệu (mặc định seed-data/chu-de-chuong-trinh.json)")
    nhom = ap.add_mutually_exclusive_group()
    nhom.add_argument("--sql", action="store_true", help="in SQL thuần thay vì mã C#")
    nhom.add_argument("--chen-vao", type=Path, help="tệp migration .cs: thay phần giữa hai dòng đánh dấu")
    tham_so = ap.parse_args()

    chu_de = doc_chu_de(tham_so.json)
    if tham_so.sql:
        for c in chu_de:
            print(bao_exec(cau_lenh_chu_de(c)) + ";")
        return 0
    ma = dong_csharp(chu_de)
    if tham_so.chen_vao is None:
        print(ma)
        return 0

    tep = tham_so.chen_vao
    tho = tep.read_bytes()
    co_bom = tho[:3] == b"\xef\xbb\xbf"
    van_ban = tho.decode("utf-8-sig")
    kieu_dong = "\r\n" if "\r\n" in van_ban else "\n"
    van_ban = van_ban.replace("\r\n", "\n")
    dau, cuoi = van_ban.find(DONG_BAT_DAU), van_ban.find(DONG_KET_THUC)
    if dau < 0 or cuoi < dau:
        raise SystemExit("Không thấy hai dòng đánh dấu trong tệp migration.")
    het_dong_dau = van_ban.index("\n", dau) + 1
    dau_dong_cuoi = van_ban.rfind("\n", 0, cuoi) + 1
    moi = van_ban[:het_dong_dau] + ma + "\n" + van_ban[dau_dong_cuoi:]
    tep.write_bytes((b"\xef\xbb\xbf" if co_bom else b"") + moi.replace("\n", kieu_dong).encode("utf-8"))
    print(f"Đã chèn {len(chu_de)} chủ đề vào {tep}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
