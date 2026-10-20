# -*- coding: utf-8 -*-
"""Sinh tệp SQL nhập di sản từ tệp seed (định dạng `seed-data/seed-dot-*.json`) vào CSDL SQL Server đang chạy.

Dùng cho máy không chạy seeder lúc khởi động (máy chủ): chạy tệp SQL bằng sqlcmd. Tệp sinh ra:
  - idempotent: chỉ thêm di sản có slug chưa có trong bảng `heritages`; chạy lại không nhân đôi;
  - ghi đúng mọi cột như seeder (`HeritageDataSeeder`): tỉnh theo mã, trạng thái DRAFT, `created_at` giờ UTC cắt tới mili giây,
    `updated_at`/`deleted_at` để trống, hai cột tìm kiếm không dấu `search_name`/`search_text` tính như `SearchText` của backend,
    ảnh vào `media` (gắn di sản), nguồn vào `heritage_references` (loại GOV_WEBSITE/UNESCO quy về WEBSITE);
  - một giao dịch, `SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET XACT_ABORT ON;` để lỗi bất kỳ thì hủy toàn bộ;
  - không nhập giai đoạn/sự kiện (các đợt sinh bằng script không có);
  - `--ban-cu <seed cũ>`: với di sản đã nhập từ bản seed cũ mà bản mới sửa địa chỉ/nội dung/mô tả ngắn (ví dụ thêm "(nay thuộc
    <tỉnh mới>)" sau tên tỉnh cũ), sinh thêm UPDATE theo slug, chỉ khi di sản còn DRAFT và ba cột đang đúng bằng bản cũ do seed
    tạo — quản trị đã sửa tay thì không đè; tính lại `search_text`;
  - `--bo-sung <tệp JSON của bo_sung_anh_toa_do.py>`: chỉ sinh lệnh bổ sung cho di sản đã có trên máy chủ: đặt tọa độ khi di sản
    còn DRAFT và tọa độ đang trống; thêm ảnh vào `media` (bỏ qua ảnh trùng địa chỉ) và đặt `thumbnail_url` khi còn DRAFT và
    ảnh đại diện đang trống. Không nhập di sản mới.

Cách chạy:
  python -I -X utf8 scripts/du-lieu-di-san/sinh_sql_nhap.py seed-data/seed-dot-2.json seed-data/seed-dot-3.json --ban-cu seed-dot-2-cu.json --out nhap-seed-dot-2-3.sql
  sqlcmd -S . -E -I -f 65001 -d <csdl> -i nhap-seed-dot-2-3.sql
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


def heritages_of(seed: pathlib.Path) -> dict[str, dict]:
    data = json.loads(seed.read_text(encoding="utf-8"))
    return {h["slug"]: h for province in data["provinces"] for h in province["heritages"]}


def emit_updates(old_seeds: list[pathlib.Path], new: dict[str, dict]) -> tuple[list[str], int]:
    """UPDATE cho di sản có trong bản cũ mà bản mới đổi địa chỉ/nội dung/mô tả ngắn; điều kiện ba cột đang bằng bản cũ và DRAFT."""
    lines: list[str] = []
    count = 0
    for old_seed in old_seeds:
        for slug, old in heritages_of(old_seed).items():
            h = new.get(slug)
            if h is None:
                continue
            fields = ("address", "content", "short_description")
            if all(old.get(f) == h.get(f) for f in fields):
                continue

            def same(f: str) -> str:
                return f"{f} IS NULL" if old.get(f) is None else f"{f} = {lit(old.get(f))}"

            lines += [f"-- cập nhật {h['name']}",
                      "UPDATE heritages SET " + ", ".join(f"{f} = {lit(h.get(f))}" for f in fields)
                      + f", search_text = {lit(search_text(h.get('name'), h.get('short_description'), h.get('address'), h.get('content')))}",
                      f"WHERE slug = {lit(slug)} AND status = N'DRAFT' AND " + " AND ".join(same(f) for f in fields) + ";",
                      "SET @cap_nhat += @@ROWCOUNT;", ""]
            count += 1
    return lines, count


def emit_supplement(found: dict[str, dict]) -> tuple[list[str], int]:
    """Lệnh bổ sung tọa độ/ảnh có điều kiện cho di sản đã có (theo slug); mỗi lệnh tự kiểm nên chạy lại không đổi gì."""
    lines: list[str] = []
    count = 0
    for slug, r in found.items():
        if r.get("latitude") is None and not r.get("media"):
            continue
        lines.append(f"-- bổ sung {r.get('name') or slug}")
        if r.get("latitude") is not None:
            lines += [f"UPDATE heritages SET latitude = {lit(r['latitude'])}, longitude = {lit(r['longitude'])}",
                      f"WHERE slug = {lit(slug)} AND status = N'DRAFT' AND deleted_at IS NULL AND latitude IS NULL AND longitude IS NULL"
                      " AND heritage_type IN (N'TANGIBLE', N'NATURAL', N'MIXED');",
                      "SET @cap_nhat += @@ROWCOUNT;"]
        if r.get("media"):
            lines += [f"SELECT @id = id FROM heritages WHERE slug = {lit(slug)} AND status = N'DRAFT' AND deleted_at IS NULL AND thumbnail_url IS NULL;",
                      "IF @id IS NOT NULL", "BEGIN"]
            for m in r["media"]:
                lines += [f"    IF NOT EXISTS (SELECT 1 FROM media WHERE heritage_id = @id AND url = {lit(m['url'])})",
                          "        INSERT INTO media (heritage_id, event_id, media_type, url, caption, credit, sort_order) VALUES (@id, NULL, "
                          + ", ".join(lit(v) for v in (m.get("media_type"), m.get("url"), m.get("caption"), m.get("credit")))
                          + ", (SELECT ISNULL(MAX(sort_order), 0) + 1 FROM media WHERE heritage_id = @id));"]
            lines += [f"    UPDATE heritages SET thumbnail_url = {lit(r['media'][0]['url'])} WHERE id = @id;", "    SET @cap_nhat += @@ROWCOUNT;",
                      "END", "SET @id = NULL;"]
        lines.append("")
        count += 1
    return lines, count


def emit(seeds: list[pathlib.Path], old_seeds: list[pathlib.Path] | None = None, supplement: dict[str, dict] | None = None) -> tuple[str, int, list[str], int]:
    lines = ["SET QUOTED_IDENTIFIER ON;", "SET ANSI_NULLS ON;", "SET XACT_ABORT ON;", "SET NOCOUNT ON;", "",
             "BEGIN TRANSACTION;", "DECLARE @id bigint, @them int = 0, @bo_qua int = 0, @cap_nhat int = 0, @tinh int;",
             "DECLARE @luc datetime2(3) = CAST(SYSUTCDATETIME() AS datetime2(3));", ""]
    total, rejected = 0, []
    update_lines, updates = emit_updates(old_seeds or [], {s: h for seed in seeds for s, h in heritages_of(seed).items()})
    lines += update_lines
    if supplement:
        supplement_lines, supplement_count = emit_supplement(supplement)
        lines += supplement_lines
        updates += supplement_count
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
              "PRINT N'Đã thêm ' + CAST(@them AS nvarchar(10)) + N' di sản, bỏ qua ' + CAST(@bo_qua AS nvarchar(10)) + N' di sản đã có slug, "
              "cập nhật ' + CAST(@cap_nhat AS nvarchar(10)) + N' lượt (địa chỉ/nội dung theo bản cũ, tọa độ, ảnh đại diện).';", ""]
    return "\n".join(lines), total, rejected, updates


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("seeds", nargs="*", help="tệp seed JSON (nạp theo thứ tự)")
    ap.add_argument("--out", required=True, help="tệp SQL sinh ra (UTF-8 có BOM để sqlcmd -f 65001 đọc đúng)")
    ap.add_argument("--ban-cu", nargs="*", default=[], help="bản seed cũ đã nhập trước đó, để sinh UPDATE cho di sản đổi địa chỉ/nội dung")
    ap.add_argument("--bo-sung", help="tệp JSON tọa độ/ảnh bổ sung (bo_sung_anh_toa_do.py); chỉ sinh UPDATE/INSERT có điều kiện")
    a = ap.parse_args()
    if not a.seeds and not a.bo_sung:
        ap.error("cần ít nhất một tệp seed hoặc --bo-sung")
    supplement = json.loads(pathlib.Path(a.bo_sung).read_text(encoding="utf-8")) if a.bo_sung else None
    sql, total, rejected, updates = emit([pathlib.Path(s) for s in a.seeds], [pathlib.Path(s) for s in a.ban_cu], supplement)
    # Ghi LF cả trên Windows: chế độ văn bản đổi xuống dòng thành CRLF, lọt vào nội dung nhiều dòng của di sản (seeder ghi LF).
    pathlib.Path(a.out).write_text(sql, encoding="utf-8-sig", newline="\n")
    print(f"Sinh {a.out}: {total} di sản trong tệp SQL, {updates} lệnh cập nhật (theo bản cũ hoặc bổ sung); bỏ {len(rejected)} vì không khớp cột")
    for r in rejected:
        print("  -", r)
    return 1 if rejected else 0


if __name__ == "__main__":
    sys.exit(main())
