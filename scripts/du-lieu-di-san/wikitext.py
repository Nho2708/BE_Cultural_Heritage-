# -*- coding: utf-8 -*-
"""Phân tích wikitext của các trang danh sách trên Wikipedia tiếng Việt: tách bảng, tách ô, làm sạch chữ.

Chỉ xử lý những gì các trang danh sách di sản dùng: bảng wikitable (ô trên từng dòng hoặc nối bằng ||),
liên kết [[bài|chữ hiện]], tệp ảnh [[Tập tin:...]], chú thích <ref>, bản mẫu {{coord}}, {{sort}}, {{nowrap}}.
Mọi chữ trả ra đều là văn bản thuần (không còn mã wiki hay HTML) để đưa vào dữ liệu.
"""
from __future__ import annotations

import html
import re
from dataclasses import dataclass, field

# Tiền tố không gian tên của tệp ảnh trên Wikipedia tiếng Việt và Commons.
FILE_PREFIXES = ("Tập tin:", "Tập_tin:", "File:", "Hình:", "Image:", "Tệp:")

_REF_PAIR = re.compile(r"<ref\b[^>/]*>.*?</ref>", re.S | re.I)
_REF_SINGLE = re.compile(r"<ref\b[^>]*/>", re.I)
_URL = re.compile(r"https?://[^\s|\]}<>\"]+")
_COMMENT = re.compile(r"<!--.*?-->", re.S)


@dataclass
class Row:
    """Một hàng của bảng: ô đã làm sạch, kèm các mẩu thông tin tách riêng trước khi làm sạch."""

    cells: list[str]
    links: list[list[str]]          # mỗi ô: danh sách tên bài được liên kết (không gồm tệp ảnh)
    files: list[str]                # tên tệp ảnh gặp trong hàng (đã bỏ tiền tố "Tập tin:")
    coords: list[tuple[float, float]]
    urls: list[str]                 # địa chỉ web gặp trong chú thích <ref> của hàng
    ref_texts: list[str] = field(default_factory=list)   # chữ thuần của từng chú thích <ref> trong hàng


@dataclass
class Table:
    headings: list[str]             # đường dẫn đề mục bao bảng, ví dụ ["Bắc Trung Bộ", "Thanh Hóa"]
    header: list[str]
    rows: list[Row]


def strip_comments(text: str) -> str:
    return _COMMENT.sub("", text)


def extract_refs(text: str) -> tuple[str, list[str], list[str]]:
    """Bỏ mọi <ref>; trả (chữ còn lại, địa chỉ web trong ref, chữ thuần của từng ref)."""
    urls: list[str] = []
    texts: list[str] = []

    def take(m: re.Match) -> str:
        inner = m.group(0)
        urls.extend(_URL.findall(inner))
        texts.append(plain(inner, keep_refs=True))
        return ""

    text = _REF_PAIR.sub(take, text)
    text = _REF_SINGLE.sub("", text)
    return text, urls, texts


def _parse_coord_args(args: list[str]) -> tuple[float, float] | None:
    """{{coord|21|02|11|N|105|50|35|E|...}} hoặc {{coord|21.03|105.84|...}} -> (vĩ độ, kinh độ)."""
    nums: list[float] = []
    signs: list[str] = []
    for a in args:
        a = a.strip()
        if not a or "=" in a or ":" in a:
            continue
        if a in ("N", "S", "E", "W"):
            signs.append(a)
            continue
        try:
            nums.append(float(a.replace(",", ".")))
        except ValueError:
            # Tham số dạng "region:VN_type:landmark" hoặc chữ khác: dừng ở đây.
            break
    if signs and len(signs) == 2:
        # Dạng độ-phút-giây: chia số theo hai dấu hướng.
        half = len(nums) // 2
        if len(nums) % 2 != 0 or half == 0:
            return None
        lat = _dms(nums[:half])
        lon = _dms(nums[half:])
        if signs[0] == "S":
            lat = -lat
        if signs[1] == "W":
            lon = -lon
        return lat, lon
    if len(nums) >= 2 and not signs:
        return nums[0], nums[1]
    return None


def _dms(parts: list[float]) -> float:
    value = 0.0
    for i, p in enumerate(parts[:3]):
        value += p / (60 ** i)
    return value


def split_template_args(body: str) -> list[str]:
    """Tách tham số bản mẫu theo | ở mức ngoài cùng (không cắt trong [[...]] hay {{...}})."""
    args, depth_sq, depth_br, cur = [], 0, 0, []
    i = 0
    while i < len(body):
        two = body[i:i + 2]
        if two == "[[":
            depth_sq += 1; cur.append(two); i += 2; continue
        if two == "]]":
            depth_sq = max(0, depth_sq - 1); cur.append(two); i += 2; continue
        if two == "{{":
            depth_br += 1; cur.append(two); i += 2; continue
        if two == "}}":
            depth_br = max(0, depth_br - 1); cur.append(two); i += 2; continue
        ch = body[i]
        if ch == "|" and depth_sq == 0 and depth_br == 0:
            args.append("".join(cur)); cur = []
        else:
            cur.append(ch)
        i += 1
    args.append("".join(cur))
    return args


def replace_templates(text: str, coords: list[tuple[float, float]] | None = None) -> str:
    """Thay bản mẫu: {{sort|khóa|hiện}} -> hiện, {{nowrap|x}} -> x, {{coord|...}} -> "" (tọa độ gom vào coords),
    bản mẫu khác -> "". Xử lý từ trong ra ngoài để bản mẫu lồng nhau cũng được thay."""
    pattern = re.compile(r"\{\{([^{}]*)\}\}")
    while True:
        m = pattern.search(text)
        if not m:
            return text
        args = split_template_args(m.group(1))
        name = args[0].strip().lower().replace("_", " ")
        repl = ""
        if name == "coord":
            c = _parse_coord_args(args[1:])
            if c and coords is not None:
                coords.append(c)
        elif name in ("sort", "sortname", "nowrap", "nobold", "small", "center", "ntsh", "nts", "hlist"):
            rest = [a for a in args[1:] if "=" not in a.split("|", 1)[0]]
            repl = rest[-1] if name == "sort" and len(rest) >= 2 else (rest[0] if rest else "")
        elif name in ("ref", "efn", "chú thích web", "cite web", "webarchive", "chú thích", "cite", "fact", "cần dẫn nguồn", "citation needed"):
            repl = ""
        elif name.startswith("flag"):
            rest = [a for a in args[1:] if "=" not in a]
            repl = rest[0] if rest else ""
        text = text[:m.start()] + repl + text[m.end():]


def extract_files(text: str) -> tuple[str, list[str]]:
    """Bỏ [[Tập tin:...|...]] khỏi chữ; trả danh sách tên tệp (không tiền tố, dấu gạch dưới thành dấu cách)."""
    files: list[str] = []
    out: list[str] = []
    i = 0
    while i < len(text):
        if text.startswith("[[", i):
            # Tìm ]] tương ứng, cho phép [[ ]] lồng nhau (chú thích ảnh có liên kết).
            depth, j = 0, i
            while j < len(text):
                if text.startswith("[[", j):
                    depth += 1; j += 2; continue
                if text.startswith("]]", j):
                    depth -= 1; j += 2
                    if depth == 0:
                        break
                    continue
                j += 1
            inner = text[i + 2:j - 2]
            target = inner.split("|", 1)[0].strip()
            if any(target.startswith(p) for p in FILE_PREFIXES):
                files.append(target.split(":", 1)[1].replace("_", " ").strip())
                i = j
                continue
            out.append(text[i:j])
            i = j
            continue
        out.append(text[i])
        i += 1
    return "".join(out), files


def extract_links(text: str) -> list[str]:
    """Tên bài được liên kết (phần trước |), bỏ liên kết tệp và liên kết neo (#)."""
    links = []
    for m in re.finditer(r"\[\[([^\[\]|]+)(?:\|[^\[\]]*)?\]\]", text):
        target = m.group(1).strip()
        if any(target.startswith(p) for p in FILE_PREFIXES) or target.startswith("#"):
            continue
        target = target.split("#", 1)[0].strip()
        if target:
            links.append(target[0].upper() + target[1:])
    return links


def links_to_text(text: str) -> str:
    text = re.sub(r"\[\[([^\[\]|]+)\|([^\[\]]*)\]\]", lambda m: m.group(2), text)
    text = re.sub(r"\[\[([^\[\]]+)\]\]", lambda m: m.group(1).split("#", 1)[0], text)
    # Liên kết ngoài [http://... chữ] -> chữ
    text = re.sub(r"\[(?:https?|ftp)://[^\s\]]+\s+([^\]]*)\]", lambda m: m.group(1), text)
    text = re.sub(r"\[(?:https?|ftp)://[^\s\]]+\]", "", text)
    return text


def strip_html(text: str) -> str:
    text = re.sub(r"<\s*br\s*/?\s*>", " ", text, flags=re.I)
    text = re.sub(r"<[^>]+>", "", text)
    return html.unescape(text)


def collapse_space(text: str) -> str:
    text = text.replace(" ", " ")
    text = re.sub(r"[ \t\r\f\v]+", " ", text)
    text = re.sub(r"\s*\n\s*", "\n", text)
    return text.strip(" \n:;,")


def plain(text: str, keep_refs: bool = False) -> str:
    """Chữ thuần từ một đoạn wikitext (không giữ tọa độ, tệp)."""
    if not keep_refs:
        text, _, _ = extract_refs(text)
    text = replace_templates(text)
    text, _ = extract_files(text)
    text = links_to_text(text)
    text = strip_html(text)
    text = text.replace("'''", "").replace("''", "")
    return collapse_space(text)


def _split_cells(line: str, sep: str) -> list[str]:
    """Tách một dòng ô theo || (hoặc !!) ở mức ngoài [[ ]] và {{ }}."""
    cells, depth_sq, depth_br, cur, i = [], 0, 0, [], 0
    while i < len(line):
        two = line[i:i + 2]
        if two == "[[":
            depth_sq += 1
        elif two == "]]":
            depth_sq = max(0, depth_sq - 1)
        elif two == "{{":
            depth_br += 1
        elif two == "}}":
            depth_br = max(0, depth_br - 1)
        if two == sep and depth_sq == 0 and depth_br == 0:
            cells.append("".join(cur)); cur = []; i += 2; continue
        cur.append(line[i]); i += 1
    cells.append("".join(cur))
    return cells


_ATTR = re.compile(r'^\s*(?:[a-zA-Z-]+\s*=\s*(?:"[^"]*"|\'[^\']*\'|[^\s|]+)\s*)+\|(?!\|)')


def _strip_attrs(cell: str) -> str:
    """Bỏ phần thuộc tính `align="center" |` ở đầu ô."""
    m = _ATTR.match(cell)
    return cell[m.end():] if m else cell


def parse_table(block: str) -> tuple[list[str], list[list[str]]]:
    """Trả (ô đầu bảng, các hàng với ô thô chưa làm sạch)."""
    lines = block.split("\n")
    header: list[str] = []
    rows: list[list[str]] = []
    current: list[str] | None = None
    cell_buf: list[str] | None = None   # ô đang gom nhiều dòng

    def flush_cell():
        nonlocal cell_buf
        if cell_buf is not None and current is not None:
            current.append("\n".join(cell_buf))
        cell_buf = None

    for raw in lines[1:]:
        line = raw.rstrip()
        if line.startswith("|}"):
            flush_cell()
            break
        if line.startswith("|-"):
            flush_cell()
            if current is not None and current:
                rows.append(current)
            current = []
            continue
        if line.startswith("|+"):
            continue
        if line.startswith("!"):
            flush_cell()
            cells = [_strip_attrs(c) for c in _split_cells(line[1:], "!!")]
            if current is None or not current:
                header.extend(cells)
            else:
                # Ô tiêu đề nằm trong hàng dữ liệu (ví dụ năm in đậm bằng !) tính như ô thường.
                current.extend(cells)
            continue
        if line.startswith("|"):
            flush_cell()
            if current is None:
                current = []
            cells = [_strip_attrs(c) for c in _split_cells(line[1:], "||")]
            current.extend(cells[:-1])
            cell_buf = [cells[-1]]
            continue
        if cell_buf is not None:
            cell_buf.append(line)
    flush_cell()
    if current:
        rows.append(current)
    return header, rows


def tables(wikitext: str) -> list[Table]:
    """Mọi bảng của trang kèm đường dẫn đề mục bao nó."""
    wikitext = strip_comments(wikitext)
    result: list[Table] = []
    headings: dict[int, str] = {}
    pos = 0
    heading_re = re.compile(r"^(={2,6})\s*(.+?)\s*\1\s*$", re.M)
    table_re = re.compile(r"^\{\|.*?^\|\}", re.M | re.S)
    events = [(m.start(), "h", m) for m in heading_re.finditer(wikitext)] + \
             [(m.start(), "t", m) for m in table_re.finditer(wikitext)]
    events.sort(key=lambda e: e[0])
    for _, kind, m in events:
        if kind == "h":
            level = len(m.group(1))
            headings = {k: v for k, v in headings.items() if k < level}
            headings[level] = plain(m.group(2))
            continue
        header, raw_rows = parse_table(m.group(0))
        rows: list[Row] = []
        for raw in raw_rows:
            cells, links, files, coords, urls, ref_texts = [], [], [], [], [], []
            for c in raw:
                c, u, rt = extract_refs(c)
                urls.extend(u)
                ref_texts.extend(rt)
                c = replace_templates(c, coords)
                c, f = extract_files(c)
                files.extend(f)
                links.append(extract_links(c))
                c = links_to_text(c)
                c = strip_html(c).replace("'''", "").replace("''", "")
                cells.append(collapse_space(c))
            rows.append(Row(cells=cells, links=links, files=files, coords=coords, urls=urls, ref_texts=ref_texts))
        result.append(Table(headings=[headings[k] for k in sorted(headings)], header=[plain(h) for h in header], rows=rows))
    return result
