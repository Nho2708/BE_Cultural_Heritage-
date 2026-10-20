# -*- coding: utf-8 -*-
"""Sinh tệp SQL nhập di sản từ tệp seed (định dạng `seed-data/seed-dot-*.json`) vào CSDL SQL Server đang chạy.

Dùng cho máy không chạy seeder lúc khởi động (máy chủ): chạy tệp SQL bằng sqlcmd. Tệp sinh ra:
  - idempotent: chỉ thêm di sản có slug chưa có trong bảng `heritages`; chạy lại không nhân đôi;
  - ghi đúng mọi cột như seeder (`HeritageDataSeeder`): tỉnh theo mã, trạng thái DRAFT, `created_at` giờ UTC cắt tới mili giây,
    `updated_at`/`deleted_at` để trống, hai cột tìm kiếm không dấu `search_name`/`search_text` tính như `SearchText` của backend,
    ảnh vào `media` (gắn di sản), nguồn vào `heritage_references` (loại GOV_WEBSITE/UNESCO quy về WEBSITE);
  - một giao dịch, `SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET XACT_ABORT ON;` để lỗi bất kỳ thì hủy toàn bộ;
  - không nhập giai đoạn/sự kiện (các đợt sinh bằng script không có).

Cách chạy:
  python -I -X utf8 scripts/du-lieu-di-san/sinh_sql_nhap.py seed-data/seed-dot-2.json --out nhap-seed-dot-2.sql
  sqlcmd -S . -E -I -f 65001 -d <csdl> -i nhap-seed-dot-2.sql
"""
from __future__ import annotations

import argparse
import json
import pathlib
import sys
import unicodedata

NAME_MAX, SLUG_MAX, SHORT_MAX, ADDRESS_MAX, PLACE_MAX, URL_MAX, CAPTION_MAX, CREDIT_MAX, REF_TITLE_MAX = 255, 255, 300, 500, 255, 500, 500, 255, 500
SOURCE_TYPES = {"GOV_DOC", "WEBSITE", "BOOK"}
HERITAGE_TYPES = {"TANGIBLE", "INTANGIBLE", "NATURAL", "MIXED", "DOCUMENTARY"}
LEVELS = {"UNESCO", "NATIONAL_SPECIAL", "NATIONAL", "PROVINCIAL", "NONE"}


def fold(text: str | None) -> str:
    """Giống SearchText.Fold: bỏ dấu (đ thành d), chữ thường, đoạn ký tự khác chữ/số thành một dấu cách."""
    if not text or not text.strip():
        return ""
    out: list[str] = []
    pending = False
    for ch in unicodedata.normalize("NFD", text):
        if unicodedata.category(ch) == "Mn":
            continue
        c = "d" if ch in "đĐ" else ch.lower()
        if c.isalnum():
            if pending and out:
                out.append(" ")
            pending = False
            out.append(c)
        else:
            pending = True
    return "".join(out)


def search_name(name: str | None) -> str:
    f = fold(name)
    return " " + f if f else ""


def search_text(name, short, address, content) -> str:
    return " " + " | ".join([fold(name), fold(short), fold(address), fold(content)])


def lit(value) -> str:
    """Hằng SQL: NULL, số, hoặc chuỗi Unicode N'...' (nhân đôi dấu nháy)."""
    if value is None:
        return "NULL"
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, (int, float)):
        return repr(value)
    return "N'" + str(value).replace("'", "''") + "'"


def check(h: dict, label: str) -> list[str]:
    p: list[str] = []
    for key, mx in (("name", NAME_MAX), ("slug", SLUG_MAX), ("short_description", SHORT_MAX), ("address", ADDRESS_MAX),
                    ("place_id", PLACE_MAX), ("thumbnail_url", URL_MAX)):
        v = h.get(key)
        if v is not None and len(v) > mx:
            p.append(f"{key} dài {len(v)} > {mx}")
    if h.get("heritage_type") not in HERITAGE_TYPES:
        p.append(f"heritage_type {h.get('heritage_type')!r}")
    if h.get("recognition_level") not in LEVELS:
        p.append(f"recognition_level {h.get('recognition_level')!r}")
    if (h.get("latitude") is None) != (h.get("longitude") is None):
        p.append("tọa độ thiếu một nửa")
    for m in h.get("media") or []:
        if len(m.get("url") or "") > URL_MAX or len(m.get("caption") or "") > CAPTION_MAX or len(m.get("credit") or "") > CREDIT_MAX:
            p.append("media quá dài")
    for r in h.get("references") or []:
        if len(r.get("url") or "") > URL_MAX or len(r.get("title") or "") > REF_TITLE_MAX:
            p.append("reference quá dài")
    if h.get("timelines"):
        p.append("có giai đoạn/sự kiện: script này không nhập")
    return p


def source_type(value: str | None) -> str | None:
    if value in ("GOV_WEBSITE", "UNESCO"):
        return "WEBSITE"
    return value if value in SOURCE_TYPES else value


def emit(seeds: list[pathlib.Path]) -> tuple[str, int, list[str]]:
    lines = ["SET QUOTED_IDENTIFIER ON;", "SET ANSI_NULLS ON;", "SET XACT_ABORT ON;", "SET NOCOUNT ON;", "",
             "BEGIN TRANSACTION;", "DECLARE @id bigint, @them int = 0, @bo_qua int = 0, @tinh int;",
             "DECLARE @luc datetime2(3) = CAST(SYSUTCDATETIME() AS datetime2(3));", ""]
    total, rejected = 0, []
    for seed in seeds:
        data = json.loads(seed.read_text(encoding="utf-8"))
        for province in data["provinces"]:
            for h in province["heritages"]:
                label = h.get("slug") or h.get("name") or "(không tên)"
                problems = check(h, label)
                if problems:
                    rejected.append(f"{seed.name}: {label}: {'; '.join(problems)}")
                    continue
                code = h.get("province_code") or province.get("code")
                lines += [f"-- {h['name']}",
                          f"IF NOT EXISTS (SELECT 1 FROM heritages WHERE slug = {lit(h['slug'])})", "BEGIN",
                          f"    SELECT @tinh = id FROM provinces WHERE code = {lit(code)};",
                          f"    IF @tinh IS NULL THROW 50001, {lit('Không có tỉnh mã ' + str(code) + ' cho ' + h['slug'])}, 1;",
                          "    INSERT INTO heritages (province_id, name, slug, short_description, content, heritage_type, recognition_level,"
                          " recognized_year, address, latitude, longitude, place_id, thumbnail_url, status, created_at, updated_at, deleted_at,"
                          " search_name, search_text)",
                          "    VALUES (@tinh, " + ", ".join(lit(v) for v in (
                              h.get("name"), h["slug"], h.get("short_description"), h.get("content"), h["heritage_type"],
                              h["recognition_level"], h.get("recognized_year"), h.get("address"), h.get("latitude"), h.get("longitude"),
                              h.get("place_id"), h.get("thumbnail_url"))) + ", N'DRAFT', @luc, NULL, NULL, "
                          + lit(search_name(h.get("name"))) + ", "
                          + lit(search_text(h.get("name"), h.get("short_description"), h.get("address"), h.get("content"))) + ");",
                          "    SET @id = SCOPE_IDENTITY();"]
                for m in h.get("media") or []:
                    lines.append("    INSERT INTO media (heritage_id, event_id, media_type, url, caption, credit, sort_order) VALUES (@id, NULL, "
                                 + ", ".join(lit(v) for v in (m.get("media_type"), m.get("url"), m.get("caption"), m.get("credit"), m.get("sort_order"))) + ");")
                for r in h.get("references") or []:
                    lines.append("    INSERT INTO heritage_references (heritage_id, title, url, source_type) VALUES (@id, "
                                 + ", ".join(lit(v) for v in (r.get("title"), r.get("url"), source_type(r.get("source_type")))) + ");")
                lines += ["    SET @them += 1;", "END", "ELSE SET @bo_qua += 1;", ""]
                total += 1
    lines += ["COMMIT TRANSACTION;",
              "PRINT N'Đã thêm ' + CAST(@them AS nvarchar(10)) + N' di sản, bỏ qua ' + CAST(@bo_qua AS nvarchar(10)) + N' di sản đã có slug.';", ""]
    return "\n".join(lines), total, rejected


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("seeds", nargs="+", help="tệp seed JSON (nạp theo thứ tự)")
    ap.add_argument("--out", required=True, help="tệp SQL sinh ra (UTF-8 có BOM để sqlcmd -f 65001 đọc đúng)")
    a = ap.parse_args()
    sql, total, rejected = emit([pathlib.Path(s) for s in a.seeds])
    pathlib.Path(a.out).write_text(sql, encoding="utf-8-sig")
    print(f"Sinh {a.out}: {total} di sản trong tệp SQL; bỏ {len(rejected)} vì không khớp cột")
    for r in rejected:
        print("  -", r)
    return 1 if rejected else 0


if __name__ == "__main__":
    sys.exit(main())
