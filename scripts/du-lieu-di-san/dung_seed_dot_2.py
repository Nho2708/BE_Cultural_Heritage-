# -*- coding: utf-8 -*-
"""Dựng kho di sản đợt 2 (`seed-data/seed-dot-2.json`) từ các trang danh sách trên Wikipedia tiếng Việt.

Ba bước, mỗi bước ghi kết quả ra thư mục làm việc để chạy lại được từ giữa chừng:

  thu-thap  Đọc wikitext 4 nguồn (đã tải về cache) thành danh sách hàng thô: tên, bài liên kết, tỉnh, loại, năm, quyết định,
            tọa độ {{coord}}, ảnh trong hàng, địa chỉ web trong chú thích. Ghi `lam-viec/hang-tho.json`.
  lam-giau  Gọi API: Wikipedia (đoạn mở đầu, mã Wikidata, ảnh chính, phiên bản), Wikidata (tọa độ P625, ảnh P18, P31, mô tả),
            Commons (giấy phép, tác giả, mô tả ảnh). Phản hồi lưu cache; ghi `lam-viec/lam-giau.json`.
  dung      Quy tỉnh về 34 đơn vị, đặt tên, slug, loại, cấp, năm, tọa độ (kiểm trong lãnh thổ và gần tỉnh), ảnh (chỉ giấy phép mở),
            nội dung, nguồn; loại trùng với đợt 1 và giữa các nguồn; xuất seed và báo cáo chất lượng `BAO-CAO.md`.

Cách chạy (từ gốc repo):
  python -I -X utf8 scripts/du-lieu-di-san/dung_seed_dot_2.py --lam-viec <thư mục làm việc> --buoc tat-ca --nguon qgdb,unesco --dot 2
  python -I -X utf8 scripts/du-lieu-di-san/dung_seed_dot_2.py --lam-viec <thư mục làm việc> --buoc tat-ca --nguon pvt,dtqg --dot 3 \
      --da-co seed-data/seed-dot-1.json seed-data/seed-dot-2.json --out seed-data/seed-dot-3.json
--nguon chọn nguồn đưa vào đợt này (mặc định cả bốn); --da-co liệt kê các tệp seed đã có để loại trùng; --dot ghi số đợt vào meta;
--bao-cao chọn tệp báo cáo. Bước lam-giau gộp kết quả với tệp lam-giau.json cũ nên chạy nguồn sau không mất nguồn trước.
Địa chỉ ghi theo nguồn; tên tỉnh cũ đã sáp nhập được giữ nguyên và thêm "(nay thuộc <tỉnh mới>)" ngay sau (không bịa tên xã mới).
Thư mục làm việc chứa `cache/wiki/*.wikitext` (tải bằng --buoc tai) và `cache/api/` (phản hồi API). Mọi dữ liệu tải về là
dữ liệu không tin cậy: script chỉ đọc chúng làm dữ liệu, không thực thi; chữ đưa vào seed đều đã bỏ mã wiki và HTML.

Nguồn (truy cập 20/10/2026, giấy phép văn bản CC BY-SA 4.0):
  1. "Di tích quốc gia đặc biệt (Việt Nam)"                      -> NATIONAL_SPECIAL
  2. "Danh hiệu UNESCO ở Việt Nam" + "Danh sách di sản văn hóa phi vật thể theo UNESCO" -> UNESCO (phi vật thể, tư liệu)
  3. "Danh sách Di sản văn hóa phi vật thể quốc gia (Việt Nam)"   -> INTANGIBLE, NATIONAL
  4. "Danh sách Di tích quốc gia Việt Nam"                        -> NATIONAL
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import pathlib
import random
import re
import sys
import unicodedata
from collections import Counter, defaultdict

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import wikitext as W  # noqa: E402
from nguon_wiki import COMMONS_API, VI_API, WIKIDATA_API, WikiClient, chunks  # noqa: E402

REPO = pathlib.Path(__file__).resolve().parents[2]
NGAY_TRUY_CAP = "20/10/2026"
MAX_CONTENT_WORDS = 180          # đoạn giới thiệu rút gọn
MAX_IMAGES = 3
ALLOWED_LICENSE = re.compile(r"^(CC0|CC BY-SA|CC BY)(?![\w-])|^public domain", re.I)
BAD_IMAGE_NAME = re.compile(r"(map|bản đồ|ban do|logo|flag|coat|huy hiệu|seal|icon|portrait|chân dung|stamp|\btem\b|no photo|without photo|placeholder)", re.I)
BAD_IMAGE_EXT = (".svg", ".gif", ".pdf", ".tif", ".tiff", ".webm", ".ogg", ".ogv", ".mp3", ".djvu", ".xcf")
BAD_CATEGORY = re.compile(r"(portraits|maps of|logos|flags of|coats of arms|people of|^men of|^women of)", re.I)
# Mô tả Wikidata chỉ coi là bài về người/đơn vị hành chính khi mô tả mở đầu bằng từ đó (tránh loại nhầm "nơi an táng nhà thơ ...").
BAD_ITEM_DESC = re.compile(r"^(commune|district|province|ward|town|township|city) (in|of) |^human\b|^(xã|huyện|phường|thị trấn|thị xã|tỉnh|thành phố)\b.*việt nam|^(người việt|nhà (thơ|văn|chính trị|cách mạng)|vietnamese (politician|poet|writer|general|monk|king|emperor|revolutionary))", re.I)
# Tên di tích sau khi bỏ tiền tố loại hình phải mở đầu bằng một từ chỉ công trình/địa điểm (so không dấu); không thì giữ nguyên
# tên nguồn (ví dụ "Di tích khảo cổ Vĩnh Hưng" giữ nguyên, vì "Vĩnh Hưng" một mình là tên xã).
PLACE_WORDS = {"chua", "den", "dinh", "mieu", "mo", "lang", "hang", "nha", "cac", "nhung", "khu", "quan", "thanh", "thap", "dia", "can",
               "chien", "cau", "phu", "co", "kinh", "rung", "nui", "dong", "vuon", "ho", "doi", "cum", "trung", "bai", "ben", "van",
               "tuong", "cot", "nen", "luy", "tran", "ao", "hon", "dao", "ga", "cang", "cho", "nghia", "cong", "toa", "hoang"}

PAGES = {
    "qgdb": ("ds-quoc-gia-dac-biet", "Di tích quốc gia đặc biệt (Việt Nam)"),
    "unesco": ("danh-hieu-unesco", "Danh hiệu UNESCO ở Việt Nam"),
    "unesco-pvt": ("ds-pvt-unesco", "Danh sách di sản văn hóa phi vật thể theo UNESCO"),
    "pvt": ("ds-pvt-quoc-gia", "Danh sách Di sản văn hóa phi vật thể quốc gia (Việt Nam)"),
    "dtqg": ("ds-di-tich-quoc-gia", "Danh sách Di tích quốc gia Việt Nam"),
}
LEVEL_RANK = {"UNESCO": 4, "NATIONAL_SPECIAL": 3, "NATIONAL": 2, "PROVINCIAL": 1, "NONE": 0}

# Sửa lỗi chính tả có trong wikitext nguồn (ghi vào báo cáo). Khóa là tên thô sau khi làm sạch mã wiki.
SUA_TEN = {
    "Quần thể di tích Tây6)ơn Thượng Đạo": "Quần thể di tích Tây Sơn Thượng Đạo",
    "ATK2 (10 di tích)": "An toàn khu II Hiệp Hòa",
}

# Nguồn 2: UNESCO ghi danh phi vật thể và tư liệu, chép từ trang "Danh hiệu UNESCO ở Việt Nam" (mục Di sản văn hóa phi vật thể,
# Di sản tư liệu) và đối chiếu trang "Danh sách di sản văn hóa phi vật thể theo UNESCO". Di sản đã có trong đợt 1 bị loại ở bước
# dựng theo mã Wikidata/tên. Mỗi dòng: tên hiển thị, tên bài Wikipedia, năm, loại, mã tỉnh chính, địa bàn theo nguồn, ghi chú danh mục.
UNESCO_ROWS = [
    ("Lễ hội Vía Bà Chúa Xứ Núi Sam", "Lễ hội Vía Bà Chúa Xứ Núi Sam", 2024, "INTANGIBLE", "91", "Khu du lịch Núi Sam, Châu Đốc, tỉnh An Giang", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (04/12/2024)"),
    ("Nghệ thuật làm gốm của người Chăm", "Gốm Chăm", 2022, "INTANGIBLE", "56", "Làng Bàu Trúc (tỉnh Khánh Hòa, trước là Ninh Thuận) và làng Bình Đức (tỉnh Lâm Đồng, trước là Bình Thuận)", "Danh sách Di sản văn hóa phi vật thể cần bảo vệ khẩn cấp (2022)"),
    ("Nghệ thuật Xòe Thái", "Xòe Thái", 2021, "INTANGIBLE", "14", "Bốn tỉnh Tây Bắc: Lào Cai (gồm Yên Bái cũ), Lai Châu, Điện Biên, Sơn La", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (15/12/2021)"),
    ("Thực hành Then của người Tày, Nùng, Thái", "Hát then", 2019, "INTANGIBLE", "08", "Tuyên Quang, Cao Bằng, Lạng Sơn, Thái Nguyên, Bắc Ninh, Quảng Ninh, Lào Cai, Sơn La, Điện Biên, Lai Châu", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (2019)"),
    ("Nghệ thuật Bài chòi Trung Bộ Việt Nam", "Bài chòi", 2017, "INTANGIBLE", "52", "Ven biển Trung Bộ: Quảng Trị, Huế, Đà Nẵng, Quảng Ngãi, Gia Lai, Đắk Lắk, Khánh Hòa, Lâm Đồng", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (2017)"),
    ("Thực hành Tín ngưỡng thờ Mẫu Tam phủ của người Việt", "Tín ngưỡng thờ Mẫu Việt Nam", 2016, "INTANGIBLE", "37", "Ninh Bình (gồm Nam Định cũ), Hà Nội, Hưng Yên, Hải Phòng, Bắc Ninh, Phú Thọ, Lạng Sơn, Lào Cai, Tuyên Quang, Thanh Hóa, Nghệ An, Hà Tĩnh, Huế, Thành phố Hồ Chí Minh", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (01/12/2016)"),
    ("Nghi lễ và trò chơi Kéo co", "Kéo co", 2015, "INTANGIBLE", "25", "Hồ sơ đa quốc gia (Việt Nam, Campuchia, Hàn Quốc, Philippines); ở Việt Nam: Phú Thọ, Lào Cai, Hà Nội, Bắc Ninh", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (02/12/2015)"),
    ("Dân ca Ví, Giặm Nghệ Tĩnh", "Dân ca ví, giặm Nghệ Tĩnh", 2014, "INTANGIBLE", "40", "Nghệ An và Hà Tĩnh", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (27/11/2014)"),
    ("Hát Xoan Phú Thọ", "Hát xoan", 2011, "INTANGIBLE", "25", "Phú Thọ", "Danh sách cần bảo vệ khẩn cấp (24/11/2011), chuyển sang Danh sách đại diện năm 2017"),
    ("Hội Gióng ở đền Phù Đổng và đền Sóc", "Hội Gióng", 2010, "INTANGIBLE", "01", "Đền Phù Đổng (Gia Lâm) và đền Sóc (Sóc Sơn), thành phố Hà Nội", "Danh sách Di sản văn hóa phi vật thể đại diện của nhân loại (16/11/2010)"),
    ("Ca trù", "Ca trù", 2009, "INTANGIBLE", "01", "Phú Thọ, Hà Nội, Bắc Ninh, Hải Phòng, Hưng Yên, Ninh Bình, Thanh Hóa, Nghệ An, Hà Tĩnh, Quảng Trị", "Danh sách Di sản văn hóa phi vật thể cần bảo vệ khẩn cấp (01/10/2009)"),
    ("Mộc bản triều Nguyễn", "Mộc bản triều Nguyễn", 2009, "DOCUMENTARY", "68", "Trung tâm Lưu trữ quốc gia IV, Đà Lạt, tỉnh Lâm Đồng", "Di sản tư liệu thế giới, Chương trình Ký ức thế giới (31/7/2009)"),
    ("Châu bản triều Nguyễn", "Châu bản triều Nguyễn", 2017, "DOCUMENTARY", "01", "Trung tâm Lưu trữ quốc gia I, phường Yên Hòa, Hà Nội", "Di sản tư liệu khu vực châu Á – Thái Bình Dương (14/5/2014); Di sản tư liệu thế giới (2017)"),
    ("Mộc bản chùa Vĩnh Nghiêm", "Mộc bản chùa Vĩnh Nghiêm", 2012, "DOCUMENTARY", "24", "Chùa Vĩnh Nghiêm, Yên Dũng (Bắc Giang cũ), nay thuộc Bắc Ninh", "Di sản tư liệu khu vực châu Á – Thái Bình Dương (16/5/2012)"),
    ("Thơ văn trên kiến trúc cung đình Huế", "Thơ văn trên kiến trúc cung đình Huế", 2016, "DOCUMENTARY", "46", "Quần thể di tích Cố đô Huế, thành phố Huế", "Di sản tư liệu khu vực châu Á – Thái Bình Dương (MOWCAP, 5/2016)"),
    ("Mộc bản trường học Phúc Giang", "Mộc bản trường học Phúc Giang", 2016, "DOCUMENTARY", "42", "Làng Trường Lưu, Can Lộc, tỉnh Hà Tĩnh", "Di sản tư liệu khu vực châu Á – Thái Bình Dương (MOWCAP, 5/2016)"),
    ("Hoàng hoa sứ trình đồ", "Hoàng hoa sứ trình đồ", 2018, "DOCUMENTARY", "42", "Dòng họ Nguyễn Huy, làng Trường Lưu, Can Lộc, tỉnh Hà Tĩnh", "Di sản tư liệu khu vực châu Á – Thái Bình Dương (MOWCAP, 30/5/2018)"),
    ("Bia ma nhai tại danh thắng Ngũ Hành Sơn", "Ma nhai Ngũ Hành Sơn", 2022, "DOCUMENTARY", "48", "Danh thắng Ngũ Hành Sơn, thành phố Đà Nẵng", "Di sản tư liệu khu vực châu Á – Thái Bình Dương (MOWCAP, 26/11/2022)"),
    ("Văn bản Hán Nôm làng Trường Lưu (1689–1943)", "Văn bản Hán Nôm làng Trường Lưu", 2022, "DOCUMENTARY", "42", "Làng Trường Lưu, Can Lộc, tỉnh Hà Tĩnh", "Di sản tư liệu khu vực châu Á – Thái Bình Dương (MOWCAP, 26/11/2022)"),
]

# Tâm gần đúng của 34 đơn vị cấp tỉnh (sau sắp xếp 01/7/2025) để kiểm tọa độ có gần đúng tỉnh không.
PROVINCE_CENTER = {
    "01": (21.0, 105.8), "04": (22.7, 106.1), "08": (22.3, 105.1), "11": (21.6, 103.0), "12": (22.3, 103.3), "14": (21.2, 104.0),
    "15": (21.9, 104.3), "19": (21.9, 105.8), "20": (21.8, 106.6), "22": (21.2, 107.3), "24": (21.3, 106.3), "25": (21.0, 105.2),
    "31": (20.9, 106.5), "33": (20.6, 106.2), "37": (20.3, 106.0), "38": (20.0, 105.4), "40": (19.2, 104.9), "42": (18.3, 105.9),
    "44": (17.2, 106.6), "46": (16.4, 107.5), "48": (15.7, 108.0), "51": (14.8, 108.3), "52": (13.9, 108.6), "56": (12.0, 109.0),
    "66": (12.9, 108.6), "68": (11.7, 108.0), "75": (11.4, 107.0), "79": (10.8, 106.8), "80": (11.0, 106.2), "82": (10.5, 105.9),
    "86": (10.0, 106.2), "91": (10.2, 105.1), "92": (9.8, 105.8), "96": (9.2, 105.2),
}
MAX_KM_FROM_CENTER = 260.0   # Côn Đảo thuộc Thành phố Hồ Chí Minh, cách tâm khoảng 236 km


# ---------------------------------------------------------------- tiện ích chữ

def fold(text: str | None) -> str:
    """Giống SearchText.Fold của backend: bỏ dấu (đ thành d), chữ thường, đoạn ký tự khác chữ/số thành một dấu cách."""
    if not text:
        return ""
    out, pending = [], False
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


def slugify(text: str) -> str:
    """Giống HeritageRules.Slugify: chỉ a-z0-9 và gạch ngang."""
    out, pending = [], False
    for ch in unicodedata.normalize("NFD", text):
        if unicodedata.category(ch) == "Mn":
            continue
        c = "d" if ch in "đĐ" else ch.lower()
        if ("a" <= c <= "z") or ("0" <= c <= "9"):
            if pending and out:
                out.append("-")
            pending = False
            out.append(c)
        else:
            pending = True
    return "".join(out)[:255].rstrip("-")


def cap_first(text: str) -> str:
    return text[:1].upper() + text[1:] if text else text


def km(a: tuple[float, float], b: tuple[float, float]) -> float:
    lat1, lon1, lat2, lon2 = map(math.radians, (a[0], a[1], b[0], b[1]))
    h = math.sin((lat2 - lat1) / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin((lon2 - lon1) / 2) ** 2
    return 2 * 6371.0 * math.asin(math.sqrt(h))


def words(text: str) -> int:
    return sum(1 for t in text.split() if any(c.isalnum() for c in t))


def shorten(text: str, max_words: int) -> str:
    """Rút gọn tới ranh giới câu sao cho không quá max_words từ; không thêm chữ nào."""
    text = re.sub(r"\s+", " ", text).strip()
    if words(text) <= max_words:
        return text
    sentences = re.split(r"(?<=[.!?])\s+(?=[A-ZÀ-Ỹ\"“(])", text)
    out: list[str] = []
    for s in sentences:
        if words(" ".join(out + [s])) > max_words:
            break
        out.append(s)
    if not out:
        return " ".join(text.split()[:max_words]) + "…"
    return " ".join(out)


def first_sentence(text: str) -> str:
    m = re.match(r"(.+?[.!?])(\s+[A-ZÀ-Ỹ\"“(]|$)", text.replace("\n", " "), re.S)
    return m.group(1).strip() if m else text.strip()


def today() -> str:
    return dt.date.today().strftime("%d/%m/%Y")


# ---------------------------------------------------------------- tỉnh

class Provinces:
    def __init__(self, provinces_file: pathlib.Path, mapping_file: pathlib.Path) -> None:
        self.rows = json.loads(provinces_file.read_text(encoding="utf-8"))
        self.by_code = {p["code"]: p for p in self.rows}
        mapping = json.loads(mapping_file.read_text(encoding="utf-8"))["mappings"]
        self.lookup: dict[str, str] = {}
        self.old_name_of: dict[str, dict[str, str]] = {}
        for p in self.rows:
            self.lookup[self._key(p["name"])] = p["code"]
        for m in mapping:
            self.lookup[self._key(m["old_name"])] = m["new_code"]
            self.old_name_of.setdefault(m["new_code"], {})[self._key(m["old_name"])] = m["old_name"]
        # Tên gọi khác gặp trong nguồn.
        for alias, code in {
            "thua thien hue": "46", "thua thien": "46", "ba ria vung tau": "79", "tp ho chi minh": "79", "sai gon": "79",
            "dak lak": "66", "dac lac": "66", "dak nong": "68", "dac nong": "68", "hue": "46", "ha tay": "01",
        }.items():
            self.lookup.setdefault(alias, code)
        # Tên tỉnh cũ không có trong bảng 63 tỉnh nhưng vẫn gặp trong nguồn (Hà Tây nhập Hà Nội năm 2008; cách viết cũ của Huế).
        self.old_name_of.setdefault("01", {})["ha tay"] = "Tỉnh Hà Tây"
        self.old_name_of.setdefault("46", {})["thua thien hue"] = "Tỉnh Thừa Thiên Huế"
        # Tên tỉnh cũ (gấp, bỏ tiền tố) -> (tên đầy đủ cũ, mã tỉnh mới), chỉ những tên đã đổi sau sáp nhập.
        self.changed_old: dict[str, tuple[str, str]] = {}
        for code, olds in self.old_name_of.items():
            for k, old in olds.items():
                if fold(old) != fold(self.by_code[code]["name"]):
                    self.changed_old[k] = (old, code)

    @staticmethod
    def _key(name: str) -> str:
        k = fold(name)
        k = re.sub(r"^(tinh|thanh pho|tp|thu do)\s+", "", k).strip()
        return k

    def code_of(self, name: str) -> str | None:
        return self.lookup.get(self._key(name))

    def short_name(self, code: str) -> str:
        name = self.by_code[code]["name"]
        return re.sub(r"^(Tỉnh|Thành phố)\s+", "", name)

    def full_name(self, code: str) -> str:
        return self.by_code[code]["name"]

    def lower_name(self, code: str) -> str:
        """'tỉnh Phú Thọ' / 'thành phố Hà Nội'."""
        name = self.by_code[code]["name"]
        return name[:1].lower() + name[1:]

    def old_name(self, code: str, text: str) -> str | None:
        """Tên tỉnh cũ (trước 01/7/2025) xuất hiện trong chữ, nếu khác tên mới."""
        for k, old in self.old_name_of.get(code, {}).items():
            if k in fold(text) and fold(old) != fold(self.by_code[code]["name"]):
                return old
        return None

    def old_full_lower(self, name: str) -> str | None:
        """'Hà Giang' -> 'tỉnh Hà Giang' (tên đầy đủ của tỉnh cũ hoặc tỉnh hiện có, viết thường chữ đầu); không biết thì None."""
        k = self._key(name)
        if k in self.changed_old:
            full = self.changed_old[k][0]
        elif k in self.lookup:
            full = self.by_code[self.lookup[k]]["name"]
        else:
            return None
        return full[:1].lower() + full[1:]

    # Từ chỉ đơn vị nhỏ hơn tỉnh đứng ngay trước tên: "thành phố Nam Định", "xã Hòa Bình" không phải tỉnh cũ.
    _UNIT_BEFORE = re.compile(r"(?:^|\s)(xa|phuong|thi tran|huyen|thi xa|thanh pho|tp|quan|lang|thon|xom|ap|song|nui|ho|cau|cho|ga)\s*$")

    def annotate_old(self, text: str, codes: list[str]) -> str:
        """Thêm "(nay thuộc <tỉnh mới>)" sau tên tỉnh cũ đã sáp nhập có trong chữ, chỉ với tỉnh cũ thuộc các mã `codes`.

        Bỏ qua khi tên tỉnh mới đã có ngay gần đó (nguồn đã ghi "trước là", "cũ", "nay thuộc") hoặc tên đứng sau từ chỉ
        đơn vị nhỏ hơn (xã Hòa Bình không phải tỉnh Hòa Bình). Không bịa địa danh: chỉ chèn thêm cụm chú thích."""
        if not text:
            return text
        folded: list[str] = []
        index: list[int] = []
        pending = False
        for i, ch in enumerate(unicodedata.normalize("NFC", text)):
            base = "".join(c for c in unicodedata.normalize("NFD", ch) if unicodedata.category(c) != "Mn")
            c = "d" if ch in "đĐ" else base.lower()
            if c.isalnum():
                if pending and folded:
                    folded.append(" ")
                    index.append(i)
                pending = False
                folded.append(c)
                index.append(i)
            else:
                pending = True
        ftext = "".join(folded)
        inserts: list[tuple[int, str]] = []
        for k, (old, code) in self.changed_old.items():
            if code not in codes:
                continue
            new_short = fold(self.short_name(code))
            for m in re.finditer(r"(?<![a-z0-9])" + re.escape(k) + r"(?![a-z0-9])", ftext):
                if self._UNIT_BEFORE.search(ftext[max(0, m.start() - 12):m.start()]):
                    continue
                window = ftext[max(0, m.start() - 30):m.end() + 30]
                if new_short in window:
                    continue
                end = index[m.end() - 1] + 1
                inserts.append((end, f" (nay thuộc {self.lower_name(code)})"))
        for pos, extra in sorted(inserts, reverse=True):
            text = text[:pos] + extra + text[pos:]
        return text


# ---------------------------------------------------------------- bước thu thập

def clean_name(raw: str) -> tuple[str, list[str]]:
    """Tên đầy đủ: bỏ viết tắt và tiền tố loại hình thừa; trả (tên, ghi chú về việc đã sửa)."""
    notes: list[str] = []
    name = raw.strip()
    if "[[" in name or "]]" in name:
        new = re.sub(r"\[\[|\]\]", "", name).strip()
        notes.append(f"bỏ mã wiki thừa của nguồn: '{name}' -> '{new}'")
        name = new
    if name in SUA_TEN:
        notes.append(f"sửa lỗi chính tả của nguồn: '{name}' -> '{SUA_TEN[name]}'")
        name = SUA_TEN[name]
    name = re.sub(r"\s*\[[^\]]*\]\s*$", "", name)          # "[đã hoàn thiện hồ sơ...]"
    for pat, repl in [
        (r"^DTLSVH\b\.?\s*", ""), (r"^DTLS\b\.?\s*", ""), (r"^DT\s+LS\b\.?\s*", ""), (r"^KTNT\b\.?\s*", ""),
        (r"^DLTC\b\.?\s*", ""), (r"^LSVH\b\.?\s*", ""), (r"^DTKC\b\.?\s*", ""), (r"^DT\b\.?\s+", ""),
        (r"^KDT\b\.?\s*", "Khu di tích "), (r"^ATK\b", "An toàn khu"),
    ]:
        new = re.sub(pat, repl, name)
        if new != name:
            notes.append(f"bỏ viết tắt: '{name}' -> '{new}'")
            name = new
    # Tiền tố dài xếp trước để "lịch sử và kiến trúc nghệ thuật" không bị cắt dở thành "lịch sử và".
    pre = re.compile(r"^Di tích (lịch sử,? văn hóa(?: và)? kiến trúc nghệ thuật|lịch sử(?:,| và) kiến trúc nghệ thuật|lịch sử(?:,| và) khảo cổ|"
                     r"lịch sử(?:[ \-–]+văn hóa)?|kiến trúc nghệ thuật|kiến trúc|khảo cổ(?: học)?|danh lam thắng cảnh|danh thắng)\s+", re.I)
    m = pre.match(name)
    if m and len(name) > m.end() + 2 and fold(name[m.end():]).split(" ")[0] in PLACE_WORDS:
        rest = name[m.end():]
        notes.append(f"bỏ tiền tố loại hình: '{name}' -> '{cap_first(rest)}'")
        name = cap_first(rest)
    elif re.match(r"^Di tích\s+[A-ZĐ]", name) and not re.match(r"^Di tích\s+(lịch sử|kiến trúc|khảo cổ|danh)", name, re.I) \
            and fold(name[len("Di tích "):]).split(" ")[0] in PLACE_WORDS | {"phong", "vam", "con", "plei", "lang"}:
        # "Di tích Mai Lượng" giữ nguyên (phần còn lại là tên người); "Di tích Làng Cả" -> "Làng Cả".
        rest = name[len("Di tích "):]
        notes.append(f"bỏ tiền tố 'Di tích': '{name}' -> '{rest}'")
        name = rest
    name = re.sub(r"\s+", " ", name).strip(" .,;")
    return name, notes


def parse_year(text: str) -> int | None:
    """Năm công nhận: lấy số 4 chữ số đầu tiên hợp lý (1945..năm nay)."""
    years = [int(y) for y in re.findall(r"(?<!\d)(1[89]\d\d|20\d\d)(?!\d)", text or "")]
    years = [y for y in years if 1945 <= y <= dt.date.today().year]
    return years[0] if years else None


def parse_years(text: str) -> list[int]:
    return [int(y) for y in re.findall(r"(?<!\d)(19[4-9]\d|20\d\d)(?!\d)", text or "")]


def decision_in(text: str) -> str | None:
    m = re.search(r"(?:số\s*)?(\d{1,5}(?:/\d{4})?\s*/\s*Q[ĐD]\s*[-–]?\s*[A-ZĐ\-]*(?:\s*/\s*[A-Z]+)?|\d{1,5}\s*-?\s*(?:VH|VHTT|VHTTDL)\s*/\s*Q[ĐD])", text or "")
    if not m:
        return None
    return re.sub(r"\s+", "", m.group(1)).replace("QD", "QĐ")


def type_from_category(category: str, name: str) -> str:
    c = fold(category)
    natural = any(k in c for k in ("danh thang", "danh lam", "thang canh"))
    cultural = any(k in c for k in ("lich su", "kien truc", "khao co", "van hoa", "nghe thuat", "cach mang"))
    if natural and cultural:
        return "MIXED"
    if natural:
        return "NATURAL"
    return "TANGIBLE"


def category_label(category: str) -> str:
    c = fold(category)
    parts = []
    if "lich su" in c or "cach mang" in c:
        parts.append("lịch sử")
    if "kien truc" in c or "nghe thuat" in c:
        parts.append("kiến trúc nghệ thuật")
    if "khao co" in c:
        parts.append("khảo cổ")
    if "danh thang" in c or "danh lam" in c or "thang canh" in c:
        parts.append("danh lam thắng cảnh")
    if not parts:
        return "di tích"
    if parts == ["danh lam thắng cảnh"]:
        return "danh lam thắng cảnh"
    return "di tích " + ", ".join(parts)


def pick_urls(row: W.Row) -> list[dict]:
    """Nguồn dẫn trong chú thích của hàng: bỏ bản lưu web.archive.org, phân GOV_DOC/WEBSITE theo tên miền."""
    out, seen = [], set()
    for i, url in enumerate(row.urls):
        if "web.archive.org" in url or url in seen:
            continue
        seen.add(url)
        host = re.sub(r"^https?://(www\.)?", "", url).split("/")[0].lower()
        gov = host.endswith(".gov.vn") or "chinhphu.vn" in host or "thuvienphapluat" in host or "luatvietnam" in host
        text = row.ref_texts[min(i, len(row.ref_texts) - 1)] if row.ref_texts else ""
        out.append({"url": url[:480], "gov": gov, "text": text[:300]})
        if len(out) >= 2:
            break
    return out


def collect(work: pathlib.Path, provinces: Provinces) -> list[dict]:
    rows: list[dict] = []
    cache = work / "cache" / "wiki"
    meta = {k: json.loads((cache / f"{v[0]}.meta.json").read_text(encoding="utf-8")) for k, v in PAGES.items()
            if (cache / f"{v[0]}.meta.json").exists()}

    def base(source: str, raw_name: str, row: W.Row | None) -> dict:
        name, notes = clean_name(raw_name)
        article = None
        if row is not None and row.links and row.links[0]:
            article = row.links[0][0]
        return {
            "source": source, "list_page": PAGES[source][1], "list_revid": meta.get(source, {}).get("revid"),
            "name_raw": raw_name, "name": name, "name_notes": notes, "article": article,
            "coord": list(row.coords[0]) if row and row.coords else None,
            "list_image": next((f for f in row.files if not BAD_IMAGE_NAME.search(f) and not f.lower().endswith(BAD_IMAGE_EXT)), None) if row else None,
            "refs": pick_urls(row) if row else [],
        }

    # Nguồn 1: di tích quốc gia đặc biệt theo đợt.
    w = (cache / f"{PAGES['qgdb'][0]}.wikitext").read_text(encoding="utf-8")
    for t in W.tables(w):
        if len(t.headings) < 2 or not t.headings[1].startswith("Đợt"):
            continue
        m = re.match(r"Đợt\s+(\d+)\s*\((\d{4})\)", t.headings[1])
        batch, year = int(m.group(1)), int(m.group(2))
        for r in t.rows:
            if len(r.cells) < 4 or not r.cells[0]:
                continue
            loc = r.cells[2]
            code = None
            for link in reversed(r.links[2] if len(r.links) > 2 else []):
                code = provinces.code_of(link)
                if code:
                    break
            if not code:
                for part in reversed(re.split(r"[,;]", loc)):
                    code = provinces.code_of(part.strip())
                    if code:
                        break
            d = base("qgdb", r.cells[0], r)
            d.update({"province_code": code, "province_text": loc, "location": loc, "category": r.cells[3], "year": year,
                      "batch": batch, "level": "NATIONAL_SPECIAL", "type": type_from_category(r.cells[3], d["name"]),
                      "decision": None, "notes": r.cells[4] if len(r.cells) > 4 else ""})
            rows.append(d)

    # Nguồn 2: UNESCO (bảng chép tay từ trang "Danh hiệu UNESCO ở Việt Nam").
    for name, article, year, htype, code, loc, note in UNESCO_ROWS:
        d = base("unesco", name, None)
        d.update({"article": article, "province_code": code, "province_text": loc, "location": loc, "category": note, "year": year,
                  "level": "UNESCO", "type": htype, "decision": None, "notes": note})
        rows.append(d)

    # Nguồn 3: di sản văn hóa phi vật thể quốc gia.
    w = (cache / f"{PAGES['pvt'][0]}.wikitext").read_text(encoding="utf-8")
    for t in W.tables(w):
        if "Tên di sản" not in t.header:
            continue
        for r in t.rows:
            if len(r.cells) < 4 or not r.cells[0]:
                continue
            raw = re.sub(r"\s*\((đồng thời|cùng|là)[^)]*\)\s*$", "", r.cells[0])
            loc = r.cells[2]
            head = re.split(r"[\(\n]", loc)[0]
            names = [p.strip() for p in re.split(r",| và |;", head) if p.strip()]
            codes = []
            for n in names:
                c = provinces.code_of(n)
                if c and c not in codes:
                    codes.append(c)
            d = base("pvt", raw, r)
            d.update({"province_code": codes[0] if codes else None, "province_codes": codes, "province_text": head, "location": loc,
                      "category": r.cells[1].strip("| "), "year": parse_year(r.cells[3]), "level": "NATIONAL", "type": "INTANGIBLE",
                      "decision": decision_in(" ".join(r.ref_texts)), "notes": ""})
            rows.append(d)

    # Nguồn 4: di tích quốc gia theo tỉnh (đề mục cuối bao bảng là tỉnh mới; riêng Hà Nội chỉ có một cấp đề mục).
    # Bảng chuẩn có 5 cột [tên, địa bàn, phân loại, năm, ghi chú]; hàng thiếu cột hoặc hàng rác (Vườn Di sản ASEAN, Khu Ramsar...)
    # bị bỏ và ghi vào báo cáo (cờ "bo"), không đoán lại cột.
    w = (cache / f"{PAGES['dtqg'][0]}.wikitext").read_text(encoding="utf-8")
    for t in W.tables(w):
        code = provinces.code_of(t.headings[-1]) if t.headings else None
        if not code or not t.header or "Di tích" not in t.header[0]:
            continue
        for r in t.rows:
            if not r.cells or not r.cells[0].strip():
                continue
            d = base("dtqg", r.cells[0], r)
            d.update({"province_code": code, "province_text": t.headings[-1], "level": "NATIONAL", "type": "TANGIBLE"})
            if len(r.cells) != 5 or "|" in r.cells[0] or re.match(r"^\d", r.cells[0]):
                d.update({"bo": f"hàng có {len(r.cells)} ô thay vì 5 (thiếu cột hoặc hàng rác)", "location": r.cells[1] if len(r.cells) > 1 else "",
                          "category": "", "year": None, "decision": None, "notes": " | ".join(c[:60] for c in r.cells[1:])})
                rows.append(d)
                continue
            category = r.cells[2].strip("| ")
            year_text, notes = r.cells[3], r.cells[4]
            special = "dac biet" in fold(category + " " + notes)
            years = parse_years(year_text + " " + notes)
            d.update({"location": r.cells[1], "category": category, "year": (max(years) if special else min(years)) if years else None,
                      "level": "NATIONAL_SPECIAL" if special else "NATIONAL", "type": type_from_category(category, d["name"]),
                      "decision": decision_in(notes) or decision_in(" ".join(r.ref_texts)), "notes": notes})
            if not category and not years:
                d["bo"] = "không có phân loại lẫn năm xếp hạng theo nguồn, không xác minh được là di tích quốc gia"
            rows.append(d)
    return rows


# ---------------------------------------------------------------- bước làm giàu

def enrich(work: pathlib.Path, rows: list[dict], previous: dict | None = None) -> dict:
    """Làm giàu các hàng (bỏ qua hàng đã bị loại ở bước thu thập). Kết quả gộp với `previous` (tệp lam-giau.json cũ) để
    chạy nguồn sau không mất dữ liệu của nguồn trước; phản hồi API có cache nên lặp lại không tốn lượt gọi."""
    client = WikiClient(work / "cache" / "api")
    rows = [r for r in rows if not r.get("bo")]
    titles = sorted({r["article"] for r in rows if r.get("article")})
    pages: dict[str, dict] = dict((previous or {}).get("pages", {}))
    # Wikipedia: đoạn mở đầu, mã Wikidata, ảnh chính, phiên bản. exlimit tối đa 20 khi lấy nhiều đoạn.
    for group in chunks(titles, 20):
        d = client.get(VI_API, {"action": "query", "titles": "|".join(group), "redirects": 1,
                                "prop": "pageprops|extracts|pageimages|revisions", "ppprop": "wikibase_item|disambiguation",
                                "exintro": 1, "explaintext": 1, "exlimit": 20, "piprop": "original|name", "rvprop": "ids"})["data"]
        q = d.get("query", {})
        redirect = {x["from"]: x["to"] for x in q.get("redirects", [])}
        normalized = {x["from"]: x["to"] for x in q.get("normalized", [])}
        by_title = {p["title"]: p for p in q.get("pages", [])}
        for t in group:
            final = redirect.get(normalized.get(t, t), normalized.get(t, t))
            p = by_title.get(final)
            if not p or p.get("missing"):
                pages[t] = {"missing": True}
                continue
            pages[t] = {
                "title": p["title"], "pageid": p.get("pageid"), "qid": p.get("pageprops", {}).get("wikibase_item"),
                "disambiguation": "disambiguation" in p.get("pageprops", {}), "extract": p.get("extract", ""),
                "pageimage": p.get("original", {}).get("source"), "pageimage_name": p.get("pageimage"),
                "revid": (p.get("revisions") or [{}])[0].get("revid"),
            }
        print(f"  Wikipedia {len(pages)}/{len(titles)}", end="\r", flush=True)
    print()
    # Wikidata: tọa độ, ảnh, loại thực thể, mô tả.
    qids = sorted({pages[t]["qid"] for t in titles if pages.get(t, {}).get("qid")})
    items: dict[str, dict] = dict((previous or {}).get("items", {}))
    for group in chunks(qids, 50):
        d = client.get(WIKIDATA_API, {"action": "wbgetentities", "ids": "|".join(group), "props": "claims|descriptions|labels",
                                      "languages": "vi|en"})["data"]
        for qid, e in d.get("entities", {}).items():
            claims = e.get("claims", {})

            def val(prop):
                for c in claims.get(prop, []):
                    v = c.get("mainsnak", {}).get("datavalue", {}).get("value")
                    if v is not None:
                        return v
                return None

            coord = val("P625")
            items[qid] = {
                "coord": [coord["latitude"], coord["longitude"]] if isinstance(coord, dict) and coord.get("globe", "").endswith("Q2") else None,
                "image": val("P18"),
                "instance_of": [c.get("mainsnak", {}).get("datavalue", {}).get("value", {}).get("id") for c in claims.get("P31", [])],
                "desc_vi": e.get("descriptions", {}).get("vi", {}).get("value"),
                "desc_en": e.get("descriptions", {}).get("en", {}).get("value"),
                "label_vi": e.get("labels", {}).get("vi", {}).get("value"),
            }
        print(f"  Wikidata {len(items)}/{len(qids)}", end="\r", flush=True)
    print()
    # Commons: giấy phép và tác giả của mọi ảnh ứng viên.
    files: set[str] = set()
    for r in rows:
        if r.get("list_image"):
            files.add(r["list_image"])
        p = pages.get(r.get("article") or "", {})
        if p.get("pageimage_name"):
            files.add(p["pageimage_name"].replace("_", " "))
        it = items.get(p.get("qid") or "", {})
        if it.get("image"):
            files.add(it["image"].replace("_", " "))
    files = {f for f in files if not f.lower().endswith(BAD_IMAGE_EXT)}
    images: dict[str, dict] = dict((previous or {}).get("images", {}))
    for group in chunks(sorted(files), 50):
        d = client.get(COMMONS_API, {"action": "query", "titles": "|".join("File:" + f for f in group), "prop": "imageinfo",
                                     "iiprop": "url|extmetadata|mime|size",
                                     "iiextmetadatafilter": "LicenseShortName|Artist|ImageDescription|Categories|ObjectName"})["data"]
        for p in d.get("query", {}).get("pages", []):
            name = p["title"].split(":", 1)[1]
            info = (p.get("imageinfo") or [{}])[0]
            if p.get("missing") or not info.get("url"):
                images[name] = {"missing": True}
                continue
            md = {k: v.get("value") for k, v in info.get("extmetadata", {}).items()}
            images[name] = {"url": info["url"], "mime": info.get("mime"), "width": info.get("width"), "height": info.get("height"),
                            "license": md.get("LicenseShortName"), "artist": W.plain(W.strip_html(md.get("Artist") or "")),
                            "description": W.plain(W.strip_html(md.get("ImageDescription") or "")),
                            "categories": md.get("Categories") or "", "object_name": md.get("ObjectName")}
        print(f"  Commons {len(images)}/{len(files)}", end="\r", flush=True)
    print()
    print(f"Gọi API: {client.calls} lượt mới, {client.cache_hits} từ cache")
    return {"pages": pages, "items": items, "images": images}


# ---------------------------------------------------------------- bước dựng

def article_ok(page: dict, item: dict, row: dict) -> tuple[bool, str]:
    """Bài liên kết có đúng là bài về di sản không (không phải bài về người, xã/huyện, trang định hướng)."""
    if not page or page.get("missing"):
        return False, "không có bài"
    if page.get("disambiguation"):
        return False, "bài định hướng"
    if "Q5" in (item.get("instance_of") or []):
        return False, "bài về người"
    # Bài về một dân tộc (Người Tày, Người Dao...) không phải bài về di sản của dân tộc đó.
    if re.match(r"^Người\b", page.get("title") or "") or "Q41710" in (item.get("instance_of") or []):
        return False, "bài về dân tộc"
    desc = " ".join(x for x in (item.get("desc_vi"), item.get("desc_en")) if x)
    if BAD_ITEM_DESC.search(desc):
        return False, f"bài về đơn vị hành chính/người ({desc[:60]})"
    # Chỉ xét cụm "X là ..." đầu tiên của câu đầu, không bắt "nay là xã ..." nằm giữa câu.
    ex = page.get("extract") or ""
    first = ex.split(".", 1)[0]
    after_la = first.split(" là ", 1)[1] if " là " in first else ""
    if re.match(r"^(một )?(xã|phường|thị trấn|huyện|thị xã|tỉnh|quận)\b", after_la):
        return False, "bài về đơn vị hành chính"
    if row["type"] != "INTANGIBLE" and re.match(r"^[^.]{0,80}\b(sinh|mất)\b[^.]{0,40}\d{4}", ex) and re.search(r"\blà (một )?(nhà|vị|tướng|vua|danh)", ex[:200]):
        return False, "bài về người"
    return True, ""


def build(work: pathlib.Path, rows: list[dict], enriched: dict, provinces: Provinces, existing: list[dict], out_file: pathlib.Path,
          chosen: list[str], dot: int = 2) -> dict:
    pages, items, images = enriched["pages"], enriched["items"], enriched["images"]
    report: dict = {"loai": [], "trung": [], "nghi_van": [], "toa_do_bo": [], "anh_bo": Counter(), "sua_ten": [], "ten_trung_them_tinh": [],
                    "bo_hang": [(r["name_raw"], r["source"], provinces.short_name(r["province_code"]) if r.get("province_code") else "", r["bo"], r.get("notes", ""))
                                for r in rows if r.get("bo")]}
    rows = [r for r in rows if not r.get("bo")]

    # Khóa của di sản đã có trong các đợt trước (mã Wikidata, slug, tên gấp + tỉnh).
    seed1_qids, seed1_slugs, seed1_names = set(), set(), set()
    for seed_prev in existing:
        for p in seed_prev["provinces"]:
            for h in p["heritages"]:
                seed1_qids.add(h.get("place_id"))
                seed1_slugs.add(h["slug"])
                seed1_names.add((fold(h["name"]), p["code"]))
    seed1_qids.discard(None)

    # Hai hàng của cùng một trang danh sách trỏ tới cùng một bài (ví dụ hai an toàn khu cùng trỏ "ATK"): bài đó không riêng về
    # di tích nào, không dùng để lấy nội dung/mã Wikidata; hai hàng vẫn là hai di sản riêng.
    # So theo tên bài sau khi đi theo đổi hướng ("Cao Lan" và "Sán Chay" cùng về "Người Cao Lan").
    def resolved(r: dict) -> str | None:
        return (pages.get(r["article"]) or {}).get("title") or r["article"] if r.get("article") else None

    shared = Counter((r["source"], resolved(r)) for r in rows if r.get("article"))
    for r in rows:
        if r.get("article") and shared[(r["source"], resolved(r))] > 1:
            report["nghi_van"].append((r["name"], r["source"], f"bài '{resolved(r)}' dùng chung cho {shared[(r['source'], resolved(r))]} hàng: giữ di sản, không lấy nội dung từ bài"))
            r["article"] = None

    # Lọc hàng thiếu tỉnh và gán khóa trùng.
    cands: list[dict] = []
    for r in rows:
        if not r.get("province_code"):
            report["loai"].append((r["name"], r["source"], f"không quy được tỉnh từ '{r.get('province_text')}'"))
            continue
        page = pages.get(r.get("article") or "", {})
        ok, why = article_ok(page, items.get(page.get("qid") or "", {}), r) if r.get("article") else (False, "không có bài liên kết")
        r["page"] = page if ok else {}
        r["article_reason"] = why
        r["qid"] = page.get("qid") if ok else None
        cands.append(r)

    # Trùng với đợt 1.
    kept: list[dict] = []
    for r in cands:
        key_name = (fold(r["name"]), r["province_code"])
        if (r["qid"] and r["qid"] in seed1_qids) or key_name in seed1_names or slugify(r["name"]) in seed1_slugs:
            report["trung"].append((r["name"], r["source"], "đã có trong đợt trước"))
            continue
        kept.append(r)

    # Trùng giữa các nguồn: theo mã Wikidata, rồi theo tên gấp + tỉnh. Giữ cấp cao nhất; ghép quyết định/ảnh/nguồn của bản bị gộp.
    order = {"qgdb": 0, "unesco": 1, "pvt": 2, "dtqg": 3}
    kept.sort(key=lambda r: (-LEVEL_RANK[r["level"]], order[r["source"]]))
    by_qid: dict[str, dict] = {}
    by_name: dict[tuple[str, str], dict] = {}
    merged: list[dict] = []
    for r in kept:
        key_name = (fold(r["name"]), r["province_code"])
        target = (by_qid.get(r["qid"]) if r["qid"] else None) or by_name.get(key_name)
        if target is None and r["source"] == "dtqg" and r["level"] == "NATIONAL_SPECIAL":
            # Danh sách đặc biệt (nguồn 1) là đầy đủ: hàng "đặc biệt" của nguồn 4 không khớp tên là cùng di tích ghi tên khác.
            report["nghi_van"].append((r["name"], r["source"], "ghi là đặc biệt nhưng không khớp tên nào ở danh sách đặc biệt, bỏ để tránh trùng"))
            continue
        if target is not None:
            report["trung"].append((r["name"], r["source"], f"trùng với '{target['name']}' ({target['source']}), giữ cấp {target['level']}"))
            target.setdefault("merged_from", []).append(r)
            if not target.get("decision") and r.get("decision"):
                target["decision"] = r["decision"]
            if not target.get("list_image") and r.get("list_image"):
                target["list_image"] = r["list_image"]
            if target["source"] == "qgdb" and r["source"] == "dtqg" and r.get("year") and not target.get("year_national"):
                target["year_national"] = r["year"]
            for ref in r.get("refs", []):
                if ref not in target.get("refs", []):
                    target.setdefault("refs", []).append(ref)
            continue
        if r["qid"]:
            by_qid[r["qid"]] = r
        by_name[key_name] = r
        merged.append(r)

    # Tên trùng nhau ở tỉnh khác nhau (di sản phi vật thể của nhiều cộng đồng): thêm tên tỉnh vào tên để phân biệt.
    name_count = Counter(fold(r["name"]) for r in merged)
    for r in merged:
        if name_count[fold(r["name"])] > 1:
            r["name_display"] = f"{r['name']} ({provinces.short_name(r['province_code'])})"
            report["ten_trung_them_tinh"].append(r["name_display"])
        else:
            r["name_display"] = r["name"]

    # Slug duy nhất.
    used = set(seed1_slugs)
    for r in merged:
        s = slugify(r["name_display"]) or "di-san"
        if s in used:
            s2 = f"{s}-{slugify(provinces.short_name(r['province_code']))}"
            i = 2
            s3 = s2
            while s3 in used:
                s3 = f"{s2}-{i}"
                i += 1
            s = s3
        used.add(s)
        r["slug"] = s

    out_provinces = {p["code"]: {"code": p["code"], "name": p["name"], "region": p["region"], "heritages": []} for p in provinces.rows}
    counts = Counter()
    for r in merged:
        h = to_heritage(r, provinces, images, items, report)
        out_provinces[r["province_code"]]["heritages"].append(h)
        counts["heritages"] += 1
        counts["media"] += len(h["media"])
        counts["references"] += len(h["references"])

    seed = {
        "meta": {
            "dot": dot, "generated_at": dt.date.today().isoformat(), "status": "DRAFT (mọi di sản chờ quản trị duyệt)",
            "nguon": chosen,
            "counts": {"provinces": len(out_provinces), **counts, "timelines": 0, "events": 0},
            "sources": [
                {"title": v[1], "url": "https://vi.wikipedia.org/wiki/" + v[1].replace(" ", "_"), "license": "CC BY-SA 4.0 (văn bản)",
                 "accessed": NGAY_TRUY_CAP} for k, v in PAGES.items() if k in chosen or (k == "unesco-pvt" and "unesco" in chosen)
            ] + [{"title": "Wikidata (tọa độ P625, ảnh P18)", "url": "https://www.wikidata.org", "license": "CC0", "accessed": NGAY_TRUY_CAP},
                 {"title": "Wikimedia Commons (ảnh; giấy phép từng ảnh ghi ở media.credit)", "url": "https://commons.wikimedia.org", "license": "theo từng tệp", "accessed": NGAY_TRUY_CAP}],
            "note": "Sinh bởi scripts/du-lieu-di-san/dung_seed_dot_2.py. Đoạn giới thiệu rút gọn từ đoạn mở đầu bài Wikipedia tiếng Việt (CC BY-SA 4.0), "
                    "không thêm ý; tọa độ chỉ từ Wikidata hoặc {{coord}} của trang danh sách; ảnh chỉ từ Wikidata P18, ảnh chính bài hoặc ảnh trong hàng của trang danh sách, "
                    "giấy phép CC0/CC BY/CC BY-SA/phạm vi công cộng. Tỉnh theo 34 đơn vị (QĐ 19/2025/QĐ-TTg).",
        },
        "provinces": [p for p in out_provinces.values() if p["heritages"]],
    }
    out_file.write_text(json.dumps(seed, ensure_ascii=False, indent=1), encoding="utf-8")
    report["counts"] = counts
    report["merged"] = merged
    return report


def to_heritage(r: dict, provinces: Provinces, images: dict, items: dict, report: dict) -> dict:
    code = r["province_code"]
    page = r.get("page") or {}
    item = items.get(r.get("qid") or "", {})
    name = r["name_display"]

    # Tọa độ: Wikidata P625, không có thì {{coord}} của trang danh sách; phải trong lãnh thổ và cách tâm tỉnh không quá 200 km.
    lat = lon = None
    coord_src = None
    for src, c in (("Wikidata P625", item.get("coord")), ("{{coord}} trang danh sách", r.get("coord"))):
        if not c:
            continue
        la, lo = float(c[0]), float(c[1])
        if not (8.0 <= la <= 23.5 and 102.0 <= lo <= 117.5):
            report["toa_do_bo"].append((name, src, f"ngoài lãnh thổ: {la:.4f}, {lo:.4f}"))
            continue
        d = km((la, lo), PROVINCE_CENTER[code])
        if d > MAX_KM_FROM_CENTER:
            report["toa_do_bo"].append((name, src, f"cách tâm {provinces.short_name(code)} {d:.0f} km: {la:.4f}, {lo:.4f}"))
            continue
        lat, lon, coord_src = round(la, 6), round(lo, 6), src
        break
    if r["type"] in ("INTANGIBLE", "DOCUMENTARY"):
        lat = lon = coord_src = None

    # Ảnh: Wikidata P18, ảnh chính của bài, ảnh trong hàng của trang danh sách; chỉ giấy phép mở.
    media = []
    seen = set()
    for src, fname in (("Wikidata P18", item.get("image")), ("ảnh chính bài Wikipedia", page.get("pageimage_name")), ("ảnh trong hàng của trang danh sách", r.get("list_image"))):
        if not fname:
            continue
        fname = fname.replace("_", " ")
        if fname in seen or len(media) >= MAX_IMAGES:
            continue
        seen.add(fname)
        info = images.get(fname)
        if not info or info.get("missing"):
            report["anh_bo"]["không tìm thấy trên Commons"] += 1
            continue
        if fname.lower().endswith(BAD_IMAGE_EXT) or not (info.get("mime") or "").startswith("image/"):
            report["anh_bo"]["không phải ảnh (svg/gif/pdf...)"] += 1
            continue
        if BAD_IMAGE_NAME.search(fname) or BAD_CATEGORY.search(info.get("categories") or ""):
            report["anh_bo"]["tên/thể loại là bản đồ, logo, chân dung"] += 1
            continue
        lic = (info.get("license") or "").strip()
        if not ALLOWED_LICENSE.search(lic):
            report["anh_bo"][f"giấy phép không nhận: {lic or '(trống)'}"] += 1
            continue
        artist = re.sub(r"\s+", " ", info.get("artist") or "").strip() or "Không rõ tác giả"
        credit = f"{artist} · {lic} · Wikimedia Commons"
        if len(credit) > 255:
            artist = artist[: 255 - len(f" · {lic} · Wikimedia Commons") - 1] + "…"
            credit = f"{artist} · {lic} · Wikimedia Commons"
        desc = re.sub(r"\s+", " ", info.get("description") or "").strip()
        caption = desc if 0 < len(desc) <= 200 else name
        # Bỏ tham số theo dõi (utm_...) mà API Commons gắn sau địa chỉ tệp.
        media.append({"media_type": "IMAGE", "url": info["url"].split("?", 1)[0], "caption": caption, "credit": credit, "sort_order": len(media) + 1,
                      "source_note": src})
    thumbnail = media[0]["url"] if media else None

    # Nội dung.
    level_label = {"UNESCO": "Di sản được UNESCO ghi danh", "NATIONAL_SPECIAL": "Di tích quốc gia đặc biệt",
                   "NATIONAL": "Di sản văn hóa phi vật thể quốc gia" if r["type"] == "INTANGIBLE" else "Di tích quốc gia"}[r["level"]]
    province_lower = provinces.lower_name(code)
    if r["type"] == "INTANGIBLE":
        kind = f"di sản văn hóa phi vật thể (loại hình {r['category'].lower()})" if r.get("category") and r["source"] == "pvt" else "di sản văn hóa phi vật thể"
    elif r["type"] == "DOCUMENTARY":
        kind = "di sản tư liệu"
    else:
        kind = category_label(r.get("category") or "")

    # Địa chỉ: theo nguồn, địa phương có di sản; tên tỉnh cũ đã sáp nhập giữ nguyên và thêm "(nay thuộc <tỉnh mới>)" ngay sau.
    place = re.sub(r"\s+", " ", r.get("location") or "").strip()
    if r["source"] == "dtqg":
        address = f"{place}, {province_lower}" if place else provinces.full_name(code)
    elif r["source"] == "pvt":
        # Ô địa bàn dạng "Tỉnh cũ (xã, huyện)" hoặc danh sách nhiều tỉnh; đưa phần trong ngoặc lên trước và ghi tỉnh đầy đủ.
        m = re.match(r"^([^()]+?)\s*\((.+)\)\s*$", place, re.S)
        head, detail = (m.group(1).strip(), m.group(2).strip()) if m else (place, "")
        full = provinces.old_full_lower(head) if head and not re.search(r",| và |;", head) else None
        if full:
            address = f"{detail}, {full}" if detail else full
        else:
            address = f"{detail}, {head}" if detail else (head or provinces.full_name(code))
    else:
        address = place or provinces.full_name(code)
    place_text = provinces.annotate_old(address, r.get("province_codes") or [code]).strip(" ,;")
    address = cap_first(place_text)[:500]
    year = r.get("year")
    built = f"{r['name']} là {kind} tại {place_text}" + (f", được công nhận {level_label.lower()} năm {year}." if year else ".")

    extract = re.sub(r"\s+", " ", (page.get("extract") or "")).strip()
    extract = re.sub(r"\s*\((?:[^()]*?(?:phát âm|IPA|nghe|tiếng [A-Z][^()]*?:)[^()]*)\)", "", extract)
    intro = shorten(extract, MAX_CONTENT_WORDS) if extract else ""
    short = first_sentence(intro) if intro else built
    if len(short) > 300 or words(short) < 4:
        short = built if len(built) <= 300 else built[:297] + "…"

    rank_lines = [f"- Cấp: {level_label}" + (f" (đợt {r['batch']})" if r.get("batch") else "")]
    if r.get("category") and r["source"] != "unesco":
        rank_lines.append(f"- Loại hình theo nguồn: {r['category']}")
    if r["source"] == "unesco":
        rank_lines.append(f"- Danh mục: {r['notes']}")
    rank_lines.append(f"- Năm: {year}" if year else "- Năm: chưa rõ theo nguồn")
    if r.get("year_national"):
        rank_lines.append(f"- Xếp hạng di tích quốc gia trước đó: năm {r['year_national']}")
    if r.get("decision"):
        rank_lines.append(f"- Quyết định: số {r['decision']}")
    rank_lines.append(f"- Địa bàn theo nguồn: {place_text}")
    content = "## Giới thiệu\n" + (intro if intro else built) + "\n\n## Xếp hạng\n" + "\n".join(rank_lines)
    if intro:
        content += f"\n\nĐoạn giới thiệu rút gọn từ bài \"{page['title']}\" trên Wikipedia tiếng Việt (CC BY-SA 4.0)."

    # Nguồn tham chiếu.
    refs = [{"title": f"{r['list_page']} — Wikipedia tiếng Việt (văn bản CC BY-SA 4.0), phiên bản {r.get('list_revid')}; truy cập {NGAY_TRUY_CAP}",
             "url": "https://vi.wikipedia.org/w/index.php?" + ("oldid=%s" % r["list_revid"] if r.get("list_revid") else "title=" + r["list_page"].replace(" ", "_")),
             "source_type": "WEBSITE"}]
    if page:
        refs.append({"title": f"Bài \"{page['title']}\" — Wikipedia tiếng Việt (văn bản CC BY-SA 4.0), phiên bản {page.get('revid')}; truy cập {NGAY_TRUY_CAP}",
                     "url": f"https://vi.wikipedia.org/w/index.php?oldid={page.get('revid')}" if page.get("revid") else "https://vi.wikipedia.org/wiki/" + page["title"].replace(" ", "_"),
                     "source_type": "WEBSITE"})
    if r.get("qid"):
        refs.append({"title": f"Mục Wikidata {r['qid']} (CC0): tọa độ, ảnh; truy cập {NGAY_TRUY_CAP}", "url": f"https://www.wikidata.org/wiki/{r['qid']}", "source_type": "WEBSITE"})
    for ref in r.get("refs", [])[:2]:
        text = ref.get("text") or ref["url"]
        refs.append({"title": f"{text[:440]}; truy cập {NGAY_TRUY_CAP}"[:500], "url": ref["url"], "source_type": "GOV_DOC" if ref["gov"] else "WEBSITE"})
    if r["source"] == "unesco":
        refs.append({"title": f"Danh sách di sản văn hóa phi vật thể theo UNESCO — Wikipedia tiếng Việt (đối chiếu); truy cập {NGAY_TRUY_CAP}",
                     "url": "https://vi.wikipedia.org/wiki/Danh_sách_di_sản_văn_hóa_phi_vật_thể_theo_UNESCO", "source_type": "WEBSITE"})

    if r.get("name_notes"):
        report["sua_ten"].append((r["name_raw"], name, "; ".join(r["name_notes"])))

    h = {
        "key": r["slug"], "province_code": code, "name": name[:255], "slug": r["slug"],
        "short_description": short, "content": content, "heritage_type": r["type"], "recognition_level": r["level"],
        "recognized_year": year, "address": address, "latitude": lat, "longitude": lon, "place_id": r.get("qid"),
        "map_point_note": (f"Tọa độ từ {coord_src}" if coord_src else ("Di sản phi vật thể/tư liệu: không ghim" if r["type"] in ("INTANGIBLE", "DOCUMENTARY") else "Chưa có tọa độ từ Wikidata hay trang danh sách")),
        "thumbnail_url": thumbnail, "status": "DRAFT", "timelines": [], "media": media, "references": refs,
        "source_list": r["source"],
    }
    return h


# ---------------------------------------------------------------- báo cáo

def write_report(work: pathlib.Path, rows: list[dict], report: dict, provinces: Provinces, out_file: pathlib.Path,
                 report_file: pathlib.Path, dot: int = 2) -> None:
    merged = report["merged"]
    seed = json.loads(out_file.read_text(encoding="utf-8"))
    hs = [h for p in seed["provinces"] for h in p["heritages"]]
    n = len(hs)

    def pct(k):
        return f"{100.0 * k / n:.1f}%" if n else "0%"

    lines = [f"# Báo cáo chất lượng kho di sản đợt {dot} ({dt.date.today().strftime('%d/%m/%Y')})", "",
             f"Tệp: `{out_file}` ({out_file.stat().st_size / 1024 / 1024:.1f} MB). Mọi di sản ở trạng thái DRAFT.", "",
             "## 1. Số lượng", "",
             f"- Hàng thô đọc từ nguồn: {len(rows)} (" + ", ".join(f"{k}: {v}" for k, v in Counter(r['source'] for r in rows).items()) + ")",
             f"- Di sản xuất ra: **{n}**; ảnh: {report['counts']['media']}; nguồn tham chiếu: {report['counts']['references']}",
             f"- Hàng bỏ ngay khi đọc nguồn (thiếu cột, hàng rác, không có phân loại lẫn năm): {len(report['bo_hang'])}",
             f"- Loại trùng với đợt trước hoặc giữa các nguồn: {len(report['trung'])}; loại vì không quy được tỉnh: {len(report['loai'])}; nghi vấn bỏ/không dùng bài: {len(report['nghi_van'])}", ""]
    for title, key in (("Theo nguồn", "source_list"), ("Theo cấp", "recognition_level"), ("Theo loại", "heritage_type")):
        c = Counter(h[key] for h in hs)
        lines += [f"### {title}", "", "| Giá trị | Số |", "|---|---:|"] + [f"| {k} | {v} |" for k, v in c.most_common()] + [""]
    c = Counter(h["province_code"] for h in hs)
    lines += ["### Theo tỉnh", "", "| Mã | Tỉnh | Số | Có tọa độ | Có ảnh |", "|---|---|---:|---:|---:|"]
    for code in sorted(c):
        hp = [h for h in hs if h["province_code"] == code]
        lines.append(f"| {code} | {provinces.full_name(code)} | {len(hp)} | {sum(1 for h in hp if h['latitude'] is not None)} | {sum(1 for h in hp if h['media'])} |")
    with_coord = sum(1 for h in hs if h["latitude"] is not None)
    with_img = sum(1 for h in hs if h["media"])
    with_intro = sum(1 for h in hs if "Đoạn giới thiệu rút gọn" in h["content"])
    with_qid = sum(1 for h in hs if h["place_id"])
    with_year = sum(1 for h in hs if h["recognized_year"])
    tangible = [h for h in hs if h["heritage_type"] not in ("INTANGIBLE", "DOCUMENTARY")]
    lines += ["", "## 2. Độ đầy", "", "| Chỉ số | Số | Tỷ lệ |", "|---|---:|---:|",
              f"| Có tọa độ (trên di sản vật thể/thiên nhiên/hỗn hợp) | {with_coord} / {len(tangible)} | {100.0 * with_coord / max(1, len(tangible)):.1f}% |",
              f"| Có ảnh (giấy phép mở) | {with_img} | {pct(with_img)} |",
              f"| Có đoạn giới thiệu từ bài Wikipedia | {with_intro} | {pct(with_intro)} |",
              f"| Có mã Wikidata | {with_qid} | {pct(with_qid)} |",
              f"| Có năm xếp hạng/ghi danh | {with_year} | {pct(with_year)} |",
              f"| Nội dung đủ 200 từ (điều kiện xuất bản) | {sum(1 for h in hs if words(h['content']) >= 200)} | {pct(sum(1 for h in hs if words(h['content']) >= 200))} |", ""]
    lines += ["## 3. Ảnh bị bỏ (theo lý do)", "", "| Lý do | Số |", "|---|---:|"] + [f"| {k} | {v} |" for k, v in report["anh_bo"].most_common()] + [""]
    lines += ["## 4. Tọa độ bị bỏ", "", "| Di sản | Nguồn tọa độ | Lý do |", "|---|---|---|"] + [f"| {a} | {b} | {c} |" for a, b, c in report["toa_do_bo"]] + [""]
    lines += ["## 5. Trùng (đã loại hoặc gộp)", "", "| Tên | Nguồn | Lý do |", "|---|---|---|"] + [f"| {a} | {b} | {c} |" for a, b, c in report["trung"]] + [""]
    lines += ["## 6. Nghi vấn (bỏ, hoặc giữ nhưng không dùng bài liên kết; xem cột lý do)", "", "| Tên | Nguồn | Lý do |", "|---|---|---|"] + [f"| {a} | {b} | {c} |" for a, b, c in report["nghi_van"]] + [""]
    lines += ["## 7. Không quy được tỉnh, đã bỏ", "", "| Tên | Nguồn | Lý do |", "|---|---|---|"] + [f"| {a} | {b} | {c} |" for a, b, c in report["loai"]] + [""]
    lines += ["## 7b. Hàng bỏ ngay khi đọc nguồn (thiếu cột, hàng rác, không có phân loại lẫn năm) — để người duyệt bổ sung tay nếu cần", "",
              "| Tên theo nguồn | Nguồn | Tỉnh | Lý do | Các ô còn lại |", "|---|---|---|---|---|"] + \
             [f"| {a} | {b} | {c} | {d} | {e.replace('|', '/')[:120]} |" for a, b, c, d, e in report["bo_hang"]] + [""]
    lines += ["## 8. Tên đã sửa (bỏ viết tắt, tiền tố, lỗi chính tả của nguồn)", "", "| Tên gốc | Tên dùng | Ghi chú |", "|---|---|---|"] + [f"| {a} | {b} | {c} |" for a, b, c in report["sua_ten"]] + [""]
    lines += ["## 9. Tên trùng nhau ở tỉnh khác nhau (đã thêm tên tỉnh)", ""] + [f"- {x}" for x in report["ten_trung_them_tinh"]] + [""]
    reasons = Counter(r.get("article_reason") for r in merged if not r.get("page"))
    lines += ["## 10. Bài Wikipedia không dùng được (chỉ dựng câu từ dữ liệu có cấu trúc)", "", "| Lý do | Số |", "|---|---:|"] + [f"| {k} | {v} |" for k, v in reasons.most_common()] + [""]
    wrong_art = [(r["name_display"], r["article"], r["article_reason"]) for r in merged if r.get("article") and not r.get("page") and r["article_reason"] not in ("không có bài",)]
    lines += ["Bài liên kết bị loại (để người duyệt soát):", "", "| Di sản | Bài liên kết | Lý do |", "|---|---|---|"] + [f"| {a} | {b} | {c} |" for a, b, c in wrong_art] + [""]
    rnd = random.Random(20261020)
    sample = rnd.sample(hs, min(15, n))
    lines += ["## 11. 15 mẫu ngẫu nhiên để người duyệt soát", ""]
    for h in sample:
        lines += [f"### {h['name']} ({h['slug']})", "",
                  f"- Tỉnh: {provinces.full_name(h['province_code'])} · Loại: {h['heritage_type']} · Cấp: {h['recognition_level']} · Năm: {h['recognized_year']} · Nguồn: {h['source_list']}",
                  f"- Địa chỉ: {h['address']}", f"- Tọa độ: {h['latitude']}, {h['longitude']} ({h['map_point_note']}) · Wikidata: {h['place_id']}",
                  f"- Mô tả ngắn: {h['short_description']}",
                  f"- Ảnh: " + ("; ".join(f"{m['url']} [{m['credit']}]" for m in h["media"]) if h["media"] else "không"),
                  f"- Nguồn: " + "; ".join(x["url"] for x in h["references"]), ""]
    report_file.write_text("\n".join(lines), encoding="utf-8")


# ---------------------------------------------------------------- main

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lam-viec", required=True, help="thư mục làm việc (cache, kết quả trung gian, báo cáo)")
    ap.add_argument("--buoc", default="tat-ca", choices=["tai", "thu-thap", "lam-giau", "dung", "tat-ca"])
    ap.add_argument("--out", default=str(REPO / "seed-data" / "seed-dot-2.json"))
    ap.add_argument("--provinces", default=str(REPO / "seed-data" / "provinces.json"))
    ap.add_argument("--quy-doi", default=str(REPO / "scripts" / "du-lieu-di-san" / "quy-doi-tinh-63-34.json"))
    ap.add_argument("--da-co", nargs="+", default=[str(REPO / "seed-data" / "seed-dot-1.json")],
                    help="các tệp seed đã có (loại trùng theo mã Wikidata, slug, tên + tỉnh)")
    ap.add_argument("--nguon", default="qgdb,unesco,pvt,dtqg", help="nguồn đưa vào đợt này, cách nhau bằng dấu phẩy")
    ap.add_argument("--dot", type=int, default=2, help="số thứ tự đợt ghi vào meta của tệp seed và tiêu đề báo cáo")
    ap.add_argument("--bao-cao", default=None, help="tệp báo cáo chất lượng (mặc định BAO-CAO.md trong thư mục làm việc)")
    a = ap.parse_args()
    chosen = [x.strip() for x in a.nguon.split(",") if x.strip()]
    unknown = [x for x in chosen if x not in PAGES and x != "unesco"]
    if unknown:
        ap.error(f"nguồn không biết: {unknown}; chọn trong {list(PAGES)}")
    work = pathlib.Path(a.lam_viec)
    (work / "lam-viec").mkdir(parents=True, exist_ok=True)
    provinces = Provinces(pathlib.Path(a.provinces), pathlib.Path(a.quy_doi))

    if a.buoc in ("tai", "tat-ca"):
        client = WikiClient(work / "cache" / "api")
        cache = work / "cache" / "wiki"
        cache.mkdir(parents=True, exist_ok=True)
        for key, (fname, title) in PAGES.items():
            out = cache / f"{fname}.wikitext"
            if out.exists():
                continue
            d = client.get(VI_API, {"action": "parse", "page": title, "prop": "wikitext|revid", "redirects": 1})["data"]
            out.write_text(d["parse"]["wikitext"], encoding="utf-8")
            (cache / f"{fname}.meta.json").write_text(json.dumps({"title": d["parse"]["title"], "pageid": d["parse"]["pageid"], "revid": d["parse"].get("revid")}, ensure_ascii=False), encoding="utf-8")
            print("tải", title)

    rows_file = work / "lam-viec" / "hang-tho.json"
    if a.buoc in ("thu-thap", "tat-ca"):
        rows = collect(work, provinces)
        rows_file.write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"Thu thập: {len(rows)} hàng -> {rows_file}")
        print("  theo nguồn:", dict(Counter(r["source"] for r in rows)))
        print("  không quy được tỉnh:", sum(1 for r in rows if not r.get("province_code")))
    else:
        rows = json.loads(rows_file.read_text(encoding="utf-8"))
    rows = [r for r in rows if r["source"] in chosen]
    print(f"Nguồn đưa vào đợt này: {chosen} -> {len(rows)} hàng")

    enriched_file = work / "lam-viec" / "lam-giau.json"
    if a.buoc in ("lam-giau", "tat-ca"):
        previous = json.loads(enriched_file.read_text(encoding="utf-8")) if enriched_file.exists() else None
        enriched = enrich(work, rows, previous)
        enriched_file.write_text(json.dumps(enriched, ensure_ascii=False), encoding="utf-8")
        print(f"Làm giàu: {len(enriched['pages'])} bài, {len(enriched['items'])} mục Wikidata, {len(enriched['images'])} ảnh -> {enriched_file}")
    elif a.buoc == "dung":
        enriched = json.loads(enriched_file.read_text(encoding="utf-8"))

    if a.buoc in ("dung", "tat-ca"):
        existing = [json.loads(pathlib.Path(f).read_text(encoding="utf-8")) for f in a.da_co]
        out_file = pathlib.Path(a.out)
        report_file = pathlib.Path(a.bao_cao) if a.bao_cao else work / "BAO-CAO.md"
        report = build(work, rows, enriched, provinces, existing, out_file, chosen, a.dot)
        write_report(work, rows, report, provinces, out_file, report_file, a.dot)
        print(f"Dựng: {report['counts']['heritages']} di sản, {report['counts']['media']} ảnh -> {out_file}; báo cáo {report_file}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
