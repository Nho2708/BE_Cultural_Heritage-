# -*- coding: utf-8 -*-
"""Trích văn xuôi của một bài Wikipedia tiếng Việt từ wikitext (đúng phiên bản đã dẫn trong nguồn) để làm nội dung di sản.

Chỉ giữ các đoạn văn xuôi: bỏ bảng, hộp thông tin, bản mẫu, chú thích, tệp ảnh, thư viện ảnh, danh sách gạch đầu dòng và
mã wiki/HTML. Chữ giữ nguyên văn, không viết lại; chỉ lược bớt (cắt ở ranh giới câu) cho vừa khoảng từ mong muốn.
Mục "Xem thêm", "Tham khảo", "Chú thích", "Liên kết ngoài", "Hình ảnh"... không lấy. Đoạn dưới 10 từ, đoạn kết bằng dấu hai chấm
(dẫn vào danh sách) hay không kết bằng dấu câu (mảnh dở, dòng số liệu) cũng không lấy.

Kết quả lưu ở `lam-viec/van-xuoi.json` theo khóa "<tên bài>#<phiên bản>"; phản hồi API cũng có cache nên chạy lại không tải lại.
"""
from __future__ import annotations

import json
import pathlib
import re

import wikitext as W
from nguon_wiki import VI_API, WikiClient

MIN_WORDS = 250     # đủ vượt mức 200 từ xuất bản sau khi cộng phần Xếp hạng
MAX_WORDS = 400
MIN_PARAGRAPH_WORDS = 10

SKIP_HEADINGS = {"xem them", "tham khao", "chu thich", "lien ket ngoai", "thu vien anh", "thu vien hinh anh", "hinh anh", "anh",
                 "ghi chu", "doc them", "chu giai", "tai lieu", "tai lieu tham khao", "nguon", "nguon tham khao", "thu muc",
                 "lien ket", "bo suu tap anh", "sach tham khao", "tham khao them", "thu vien", "ban do", "xem them tai"}
_TABLE = re.compile(r"\{\|(?:(?!\{\|).)*?\|\}", re.S)
_BLOCK_TAGS = re.compile(r"<(gallery|timeline|imagemap|math|score|syntaxhighlight|source|pre)\b[^>]*>.*?</\1\s*>", re.S | re.I)
_HEADING = re.compile(r"^(={2,6})\s*(.+?)\s*=*\s*$")
# Dòng danh sách wiki (*, #, :, ;), bảng, đường kẻ, từ khóa __TOC__, tệp còn sót, và gạch đầu dòng viết tay ("- ", "– ", "• ").
_LIST_LINE = re.compile(r"^\s*([*#:;|!{}]|----|__[A-Z]+__|\[\[|[-–—•]\s)")
# Đoạn văn hoàn chỉnh kết bằng dấu câu (hoặc dấu ngoặc/nháy sau dấu câu); đoạn khác là mảnh dở (nguồn bị cắt, dòng số liệu).
_PARAGRAPH_END = re.compile(r"[.!?…][\"”)»]?$")
_PRONUNCIATION = re.compile(r"\s*\((?:[^()]*?(?:phát âm|IPA|nghe|tiếng [A-Z][^()]*?:)[^()]*)\)")


def fold(text: str) -> str:
    import unicodedata
    out = []
    for ch in unicodedata.normalize("NFD", text):
        if unicodedata.category(ch) == "Mn":
            continue
        out.append("d" if ch in "đĐ" else ch.lower())
    return re.sub(r"[^a-z0-9]+", " ", "".join(out)).strip()


def words(text: str) -> int:
    return sum(1 for t in text.split() if any(c.isalnum() for c in t))


def shorten(text: str, max_words: int) -> str:
    """Cắt ở ranh giới câu sao cho không quá max_words từ; không thêm chữ."""
    text = re.sub(r"\s+", " ", text).strip()
    if words(text) <= max_words:
        return text
    sentences = re.split(r"(?<=[.!?])\s+(?=[A-ZÀ-Ỹ\"“(])", text)
    out: list[str] = []
    for s in sentences:
        if words(" ".join(out + [s])) > max_words:
            break
        out.append(s)
    return " ".join(out)


def clean(wikitext: str) -> str:
    text = W.strip_comments(wikitext)
    text, _, _ = W.extract_refs(text)
    text = _BLOCK_TAGS.sub("", text)
    prev = None
    while prev != text:
        prev = text
        text = _TABLE.sub("", text)
    # Bản mẫu nội dòng có chữ hiển thị trước, rồi bỏ mọi bản mẫu còn lại (hộp thông tin, chú thích web...).
    text = _inline_templates_once(text)
    text = W.replace_templates(text)
    text, _ = W.extract_files(text)
    text = W.links_to_text(text)
    text = W.strip_html(text)
    text = text.replace("'''", "").replace("''", "")
    return text


def _inline_templates_once(text: str) -> str:
    """Thay bản mẫu nội dòng có chữ hiển thị (không lồng bản mẫu khác)."""
    pattern = re.compile(r"\{\{([^{}]*)\}\}")

    def repl(m: re.Match) -> str:
        args = W.split_template_args(m.group(1))
        name = args[0].strip().lower().replace("_", " ")
        rest = [a.strip() for a in args[1:] if "=" not in a.split("|", 1)[0]]
        if name == "lang" and len(rest) >= 2:
            return rest[1]
        if name.startswith("lang-") and rest:
            return rest[0]
        if name in ("convert", "chuyển đổi") and len(rest) >= 2:
            return f"{rest[0]} {rest[1]}"
        return m.group(0)

    return pattern.sub(repl, text)


def tidy(text: str) -> str:
    text = _PRONUNCIATION.sub("", text)
    text = re.sub(r"\(\s*[;,.]?\s*\)", "", text)
    text = re.sub(r"\(\s*[;,]\s*", "(", text)
    text = re.sub(r"\s+([,.;:!?])", r"\1", text)
    # Chú thích bị bỏ để lại hai câu dính nhau ("...Huế.Trước năm..."): thêm dấu cách sau chấm giữa chữ thường và chữ hoa.
    text = re.sub(r"(?<=[a-zà-ỹ])([.!?])(?=[A-ZÀ-Ỹ])", r"\1 ", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text


def sections(wikitext: str) -> list[tuple[str, list[str]]]:
    """[(tên mục, [đoạn văn])] theo thứ tự bài; mục mở đầu có tên rỗng. Mục không lấy (Xem thêm...) đã bị bỏ."""
    text = clean(wikitext)
    out: list[tuple[str, list[str]]] = [("", [])]
    skipping = False
    buf: list[str] = []

    def flush():
        if buf:
            para = tidy(" ".join(buf))
            buf.clear()
            # Đoạn kết bằng dấu hai chấm là câu dẫn vào danh sách (đã bỏ danh sách) nên không lấy.
            if words(para) >= MIN_PARAGRAPH_WORDS and not skipping and _PARAGRAPH_END.search(para):
                out[-1][1].append(para)

    for raw in text.split("\n"):
        line = raw.strip()
        m = _HEADING.match(line)
        if m:
            flush()
            title = tidy(m.group(2).strip("= ").strip())
            skipping = fold(title) in SKIP_HEADINGS
            if not skipping:
                out.append((title, []))
            continue
        if not line:
            flush()
            continue
        if _LIST_LINE.match(line):
            flush()
            continue
        buf.append(line)
    flush()
    return [(h, ps) for h, ps in out if ps]


def choose(secs: list[tuple[str, list[str]]], min_words: int = MIN_WORDS, max_words: int = MAX_WORDS) -> list[tuple[str, str]]:
    """Lấy lần lượt các đoạn theo thứ tự bài tới khi đủ min_words; đoạn làm vượt max_words thì cắt ở ranh giới câu."""
    chosen: list[tuple[str, str]] = []
    total = 0
    for heading, paras in secs:
        texts: list[str] = []
        for p in paras:
            if total >= min_words:
                break
            budget = max_words - total
            if words(p) <= budget:
                texts.append(p)
                total += words(p)
                continue
            cut = shorten(p, budget)
            if words(cut) >= 15:
                texts.append(cut)
                total += words(cut)
            break
        if texts:
            chosen.append((heading, "\n\n".join(texts)))
        if total >= min_words:
            break
    return chosen


def key_of(title: str, revid: int | None) -> str:
    return f"{title}#{revid or ''}"


def load(work: pathlib.Path, client: WikiClient, wanted: list[tuple[str, int | None]]) -> dict[str, dict]:
    """Văn xuôi đã chọn cho từng bài (tải wikitext đúng phiên bản nếu chưa có trong lam-viec/van-xuoi.json)."""
    store_file = work / "lam-viec" / "van-xuoi.json"
    store: dict[str, dict] = json.loads(store_file.read_text(encoding="utf-8")) if store_file.exists() else {}
    missing = [(t, r) for t, r in dict.fromkeys(wanted) if key_of(t, r) not in store]
    for i, (title, revid) in enumerate(missing, 1):
        params = {"action": "parse", "prop": "wikitext", "oldid": revid} if revid else {"action": "parse", "prop": "wikitext", "page": title, "redirects": 1}
        d = client.get(VI_API, params)["data"]
        if "error" in d or "parse" not in d:
            store[key_of(title, revid)] = {"revid": revid, "error": (d.get("error") or {}).get("code", "không phân tích được")}
        else:
            secs = sections(d["parse"]["wikitext"])
            store[key_of(title, revid)] = {"revid": revid, "sections": secs, "words": sum(words(p) for _, ps in secs for p in ps)}
        if i % 10 == 0 or i == len(missing):
            print(f"  Văn xuôi {i}/{len(missing)}", end="\r", flush=True)
            store_file.write_text(json.dumps(store, ensure_ascii=False), encoding="utf-8")
    if missing:
        print()
    store_file.write_text(json.dumps(store, ensure_ascii=False), encoding="utf-8")
    return store
