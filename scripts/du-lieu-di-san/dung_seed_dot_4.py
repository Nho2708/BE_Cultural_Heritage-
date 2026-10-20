# -*- coding: utf-8 -*-
"""Dựng kho di sản đợt 4 (`seed-data/seed-dot-4.json`): di sản thiên nhiên và tư liệu còn thiếu.

Nguồn (Wikipedia tiếng Việt, văn bản CC BY-SA 4.0; truy cập 20/10/2026):
  vqg         "Danh sách vườn quốc gia tại Việt Nam" (bảng: tên, năm thành lập, diện tích, địa điểm)   -> NATURAL, NATIONAL
  sinh-quyen  "Khu dự trữ sinh quyển thế giới" (mục Khu dự trữ sinh quyển thế giới tại Việt Nam)       -> NATURAL, UNESCO
  dia-chat    "Danh hiệu UNESCO ở Việt Nam" (mục Mạng lưới Công viên địa chất Việt Nam)                -> NATURAL, UNESCO
  ramsar      "Công ước Ramsar" (mục Danh sách khu Ramsar tại Việt Nam)                                -> NATURAL, NATIONAL
  asean       "Vườn di sản ASEAN" (mục Việt Nam)                                                       -> NATURAL, NATIONAL
  tu-lieu     "Di sản tư liệu thế giới" (bảng Di sản tư liệu châu Á - Thái Bình Dương tại Việt Nam)    -> DOCUMENTARY, UNESCO

Một nơi được nhiều danh hiệu chỉ là một di sản (gộp theo bài Wikipedia sau khi đi theo đổi hướng); các danh hiệu ghi vào mục
Xếp hạng của nội dung và vào nguồn. Cấp: UNESCO khi có danh hiệu do UNESCO trao (sinh quyển, địa chất toàn cầu, tư liệu);
vườn quốc gia, khu bảo tồn, khu Ramsar, vườn di sản ASEAN: NATIONAL (enum không có cấp "quốc tế ngoài UNESCO").
Loại trùng với các đợt trước theo mã Wikidata, slug và tên gấp + tỉnh. Dùng lại các bước làm giàu, chọn tọa độ/ảnh, văn xuôi
và báo cáo của `dung_seed_dot_2.py`.

Cách chạy (từ gốc repo):
  python -I -X utf8 scripts/du-lieu-di-san/dung_seed_dot_4.py --lam-viec <thư mục làm việc> --buoc tat-ca \
      --da-co seed-data/seed-dot-1.json seed-data/seed-dot-2.json seed-data/seed-dot-3.json --out seed-data/seed-dot-4.json
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import re
import sys
from collections import Counter

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import dung_seed_dot_2 as D  # noqa: E402
import van_xuoi_wiki as V  # noqa: E402
import wikitext as W  # noqa: E402
from nguon_wiki import VI_API, WikiClient  # noqa: E402

REPO = pathlib.Path(__file__).resolve().parents[2]
NGAY_TRUY_CAP = "20/10/2026"
DOT = 4

PAGES = {
    "vqg": ("ds-vuon-quoc-gia", "Danh sách vườn quốc gia tại Việt Nam"),
    "sinh-quyen": ("khu-sinh-quyen", "Khu dự trữ sinh quyển thế giới"),
    "dia-chat": ("danh-hieu-unesco", "Danh hiệu UNESCO ở Việt Nam"),
    "ramsar": ("cong-uoc-ramsar", "Công ước Ramsar"),
    "asean": ("vuon-di-san-asean", "Vườn di sản ASEAN"),
    "tu-lieu": ("di-san-tu-lieu", "Di sản tư liệu thế giới"),
}
UNESCO_SOURCES = {"sinh-quyen", "dia-chat", "tu-lieu"}
LABEL = {
    "vqg": "Vườn quốc gia",
    "sinh-quyen": "Khu dự trữ sinh quyển thế giới (UNESCO, Chương trình Con người và Sinh quyển)",
    "dia-chat": "Công viên địa chất toàn cầu UNESCO",
    "ramsar": "Khu Ramsar (vùng đất ngập nước có tầm quan trọng quốc tế theo Công ước Ramsar)",
    "asean": "Vườn di sản ASEAN",
    "tu-lieu": "Di sản tư liệu khu vực châu Á – Thái Bình Dương (Chương trình Ký ức thế giới của UNESCO)",
}

# Hàng chép từ các mục danh sách (không phải bảng) của trang nguồn: nguồn, tên hiển thị, bài Wikipedia, năm, mã tỉnh (34 đơn vị),
# địa bàn theo nguồn. Tỉnh cũ đã sáp nhập ghi kèm "(nay thuộc ...)" ở bước dựng.
HAND_ROWS = [
    ("sinh-quyen", "Khu dự trữ sinh quyển rừng ngập mặn Cần Giờ", "Khu dự trữ sinh quyển rừng ngập mặn Cần Giờ", 2000, ["79"], "Cần Giờ, Thành phố Hồ Chí Minh"),
    ("sinh-quyen", "Khu dự trữ sinh quyển quần đảo Cát Bà", "Quần đảo Cát Bà", 2004, ["31"], "Huyện đảo Cát Bà (cũ), Hải Phòng"),
    ("sinh-quyen", "Khu dự trữ sinh quyển châu thổ sông Hồng", "Khu dự trữ sinh quyển châu thổ sông Hồng", 2004, ["37", "33"], "Vùng cửa sông ven biển của hai tỉnh Hưng Yên và Ninh Bình (Thái Bình và Nam Định cũ)"),
    ("sinh-quyen", "Khu dự trữ sinh quyển ven biển và biển đảo Kiên Giang", "Khu dự trữ sinh quyển ven biển và biển đảo Kiên Giang", 2006, ["91"], "Tỉnh Kiên Giang, nay thuộc tỉnh An Giang"),
    ("sinh-quyen", "Khu dự trữ sinh quyển miền tây Nghệ An", "Khu dự trữ sinh quyển miền tây Nghệ An", 2007, ["40"], "Miền tây tỉnh Nghệ An"),
    ("sinh-quyen", "Khu dự trữ sinh quyển Mũi Cà Mau", "Khu dự trữ sinh quyển Mũi Cà Mau", 2009, ["96"], "Tỉnh Cà Mau"),
    ("sinh-quyen", "Khu dự trữ sinh quyển Cù Lao Chàm", "Cù lao Chàm", 2009, ["48"], "Cù Lao Chàm – Hội An, nay thuộc thành phố Đà Nẵng"),
    ("sinh-quyen", "Khu dự trữ sinh quyển Đồng Nai", "Khu dự trữ sinh quyển Đồng Nai", 2011, ["75", "68", "79"], "Ba tỉnh Đồng Nai, Lâm Đồng và Thành phố Hồ Chí Minh"),
    ("sinh-quyen", "Khu dự trữ sinh quyển Langbiang", "Khu dự trữ sinh quyển Langbiang", 2015, ["68"], "Tỉnh Lâm Đồng"),
    ("sinh-quyen", "Khu dự trữ sinh quyển Núi Chúa", "Vườn quốc gia Núi Chúa", 2021, ["56"], "Tỉnh Ninh Thuận, nay thuộc tỉnh Khánh Hòa"),
    ("sinh-quyen", "Khu dự trữ sinh quyển Kon Hà Nừng", "Cao nguyên Kon Hà Nừng", 2021, ["52"], "Tỉnh Gia Lai"),
    ("dia-chat", "Công viên địa chất toàn cầu Cao nguyên đá Đồng Văn", "Cao nguyên đá Đồng Văn", 2010, ["08"], "Tỉnh Hà Giang, nay thuộc tỉnh Tuyên Quang"),
    ("dia-chat", "Công viên địa chất toàn cầu Non nước Cao Bằng", "Công viên địa chất Non nước Cao Bằng", 2018, ["04"], "Tỉnh Cao Bằng"),
    ("dia-chat", "Công viên địa chất toàn cầu Đắk Nông", "Công viên địa chất Đắk Nông", 2020, ["68"], "Tỉnh Đắk Nông, nay thuộc tỉnh Lâm Đồng"),
    ("dia-chat", "Công viên địa chất toàn cầu Lạng Sơn", "Công viên địa chất Lạng Sơn", 2024, ["20"], "Tỉnh Lạng Sơn"),
    ("ramsar", "Vườn quốc gia Xuân Thủy", "Vườn quốc gia Xuân Thủy", 1989, ["37"], "Nam Định, nay thuộc tỉnh Ninh Bình"),
    ("ramsar", "Vùng đất ngập nước Bàu Sấu", "Bàu Sấu", 2005, ["75"], "Vùng đất ngập nước Bàu Sấu thuộc Vườn quốc gia Cát Tiên, tỉnh Đồng Nai"),
    ("ramsar", "Hồ Ba Bể", "Hồ Ba Bể", 2011, ["19"], "Bắc Kạn, nay thuộc tỉnh Thái Nguyên"),
    ("ramsar", "Vườn quốc gia Tràm Chim", "Vườn quốc gia Tràm Chim", 2012, ["82"], "Huyện Tam Nông (cũ), tỉnh Đồng Tháp"),
    ("ramsar", "Vườn quốc gia Mũi Cà Mau", "Vườn quốc gia Mũi Cà Mau", 2013, ["96"], "Huyện Ngọc Hiển (cũ), tỉnh Cà Mau"),
    ("ramsar", "Vườn quốc gia Côn Đảo", "Vườn quốc gia Côn Đảo", 2014, ["79"], "Côn Đảo, Thành phố Hồ Chí Minh"),
    ("ramsar", "Khu bảo tồn đất ngập nước Láng Sen", "Khu bảo tồn đất ngập nước Láng Sen", 2015, ["80"], "Tỉnh Long An, nay thuộc tỉnh Tây Ninh"),
    ("ramsar", "Vườn quốc gia U Minh Thượng", "Vườn quốc gia U Minh Thượng", 2016, ["91"], "Tỉnh Kiên Giang, nay thuộc tỉnh An Giang"),
    ("ramsar", "Khu bảo tồn thiên nhiên đất ngập nước Vân Long", "Khu bảo tồn thiên nhiên đất ngập nước Vân Long", 2019, ["37"], "Tỉnh Ninh Bình"),
    ("asean", "Vườn quốc gia Ba Bể", "Vườn quốc gia Ba Bể", 2003, ["19"], "Bắc Kạn, nay thuộc tỉnh Thái Nguyên"),
    ("asean", "Vườn quốc gia Chư Mom Ray", "Vườn quốc gia Chư Mom Ray", 2003, ["51"], "Kon Tum, nay thuộc tỉnh Quảng Ngãi"),
    ("asean", "Vườn quốc gia Hoàng Liên", "Vườn quốc gia Hoàng Liên", 2003, ["15", "12"], "Lào Cai và Lai Châu"),
    ("asean", "Vườn quốc gia Kon Ka Kinh", "Vườn quốc gia Kon Ka Kinh", 2003, ["52"], "Tỉnh Gia Lai"),
    ("asean", "Vườn quốc gia U Minh Thượng", "Vườn quốc gia U Minh Thượng", 2012, ["91"], "Tỉnh Kiên Giang, nay thuộc tỉnh An Giang"),
    ("asean", "Vườn quốc gia Bái Tử Long", "Vườn quốc gia Bái Tử Long", 2017, ["22"], "Tỉnh Quảng Ninh"),
    ("asean", "Vườn quốc gia Vũ Quang", "Vườn quốc gia Vũ Quang", 2019, ["42"], "Tỉnh Hà Tĩnh"),
    ("asean", "Vườn quốc gia Bidoup Núi Bà", "Vườn quốc gia Bidoup Núi Bà", 2019, ["68"], "Tỉnh Lâm Đồng"),
    ("asean", "Vườn quốc gia Lò Gò – Xa Mát", "Vườn quốc gia Lò Gò - Xa Mát", 2019, ["80"], "Tỉnh Tây Ninh"),
    ("asean", "Khu bảo tồn thiên nhiên Ngọc Linh", "Khu bảo tồn thiên nhiên Ngọc Linh", 2019, ["51"], "Kon Tum, nay thuộc tỉnh Quảng Ngãi"),
    ("asean", "Vườn quốc gia Côn Đảo", "Vườn quốc gia Côn Đảo", 2022, ["79"], "Côn Đảo, Thành phố Hồ Chí Minh"),
    ("asean", "Vườn quốc gia Bạch Mã", "Vườn quốc gia Bạch Mã", 2022, ["46"], "Thành phố Huế"),
    ("tu-lieu", "Những bản đúc nổi trên chín đỉnh đồng ở Hoàng cung Huế (Cửu đỉnh)", "Cửu Đỉnh (nhà Nguyễn)", 2024, ["46"], "Trung tâm Bảo tồn Di tích Cố đô Huế, thành phố Huế"),
]
# Mục trong nguồn không đưa vào đợt này (ghi báo cáo): bộ sưu tập Hoàng Vân (Di sản tư liệu thế giới 2025) chưa có bài riêng,
# bài "Hoàng Vân" là bài về người và nguồn không nêu nơi lưu giữ -> để người duyệt bổ sung tay.
SKIPPED_NOTES = [
    ("Bộ sưu tập tài liệu của nhạc sĩ Hoàng Vân", "tu-lieu", "Di sản tư liệu thế giới (10/4/2025) nhưng chưa có bài riêng (bài 'Hoàng Vân' là bài về người) và nguồn không nêu nơi lưu giữ; người duyệt bổ sung tay"),
]


def meta_of(work: pathlib.Path, key: str) -> dict:
    f = work / "cache" / "wiki" / f"{PAGES[key][0]}.meta.json"
    return json.loads(f.read_text(encoding="utf-8")) if f.exists() else {}


def download(work: pathlib.Path) -> None:
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


def collect(work: pathlib.Path, provinces: D.Provinces) -> list[dict]:
    rows: list[dict] = []
    # Vườn quốc gia: bảng [vùng (gộp ô), tên, năm thành lập, diện tích, địa điểm]; ô vùng gộp nên hàng có 4 hoặc 5 ô.
    w = (work / "cache" / "wiki" / f"{PAGES['vqg'][0]}.wikitext").read_text(encoding="utf-8")
    for t in W.tables(w):
        if not t.header or "Tên vườn" not in " ".join(t.header):
            continue
        for r in t.rows:
            if len(r.cells) < 4:
                continue
            name_cell, year_cell, area_cell, loc_cell = r.cells[-4:]
            if not name_cell.strip():
                continue
            codes: list[str] = []
            for link in r.links[-1] or re.split(r",|;| và ", loc_cell):
                c = provinces.code_of(link.strip())
                if c and c not in codes:
                    codes.append(c)
            article = r.links[-4][0] if r.links[-4] else None
            name = name_cell if D.fold(name_cell).startswith("vuon quoc gia") else f"Vườn quốc gia {name_cell}"
            rows.append({"source": "vqg", "name": name, "article": article, "year": D.parse_year(year_cell), "area": area_cell.strip() if re.search(r"\d", area_cell) else None,
                         "province_codes": codes,
                         "location": ", ".join(provinces.full_name(c) if i == 0 else provinces.lower_name(c) for i, c in enumerate(codes)) if codes else loc_cell,
                         "list_image": None, "coord": list(r.coords[0]) if r.coords else None, "refs": D.pick_urls(r)})
    for source, name, article, year, codes, location in HAND_ROWS:
        rows.append({"source": source, "name": name, "article": article, "year": year, "area": None, "province_codes": codes,
                     "location": location, "list_image": None, "coord": None, "refs": []})
    for r in rows:
        r.update({"list_page": PAGES[r["source"]][1], "list_revid": meta_of(work, r["source"]).get("revid"), "name_raw": r["name"], "name_notes": [],
                  "province_code": r["province_codes"][0] if r["province_codes"] else None, "province_text": r["location"],
                  "type": "DOCUMENTARY" if r["source"] == "tu-lieu" else "NATURAL",
                  "level": "UNESCO" if r["source"] in UNESCO_SOURCES else "NATIONAL", "category": LABEL[r["source"]], "notes": ""})
    return rows


def build(work: pathlib.Path, rows: list[dict], enriched: dict, provinces: D.Provinces, existing: list[dict], out_file: pathlib.Path) -> dict:
    pages, items, images = enriched["pages"], enriched["items"], enriched["images"]
    report: dict = {"loai": [], "trung": [], "nghi_van": [], "toa_do_bo": [], "anh_bo": Counter(), "sua_ten": [], "ten_trung_them_tinh": [],
                    "bo_hang": [], "gan_trung": [], "gop": []}
    for name, source, why in SKIPPED_NOTES:
        report["nghi_van"].append((name, source, why))

    prev_qids, prev_slugs, prev_names, prev_list = set(), set(), set(), []
    for seed_prev in existing:
        for p in seed_prev["provinces"]:
            for h in p["heritages"]:
                prev_qids.add(h.get("place_id"))
                prev_slugs.add(h["slug"])
                prev_names.add((D.fold(h["name"]), p["code"]))
                prev_list.append((h["name"], h["slug"], p["code"]))
    prev_qids.discard(None)

    # Bài liên kết có dùng được không; khóa gộp là tên bài sau đổi hướng (không có bài thì theo tên gấp).
    for r in rows:
        if not r.get("province_code"):
            report["loai"].append((r["name"], r["source"], f"không quy được tỉnh từ '{r.get('province_text')}'"))
            continue
        page = pages.get(r.get("article") or "", {})
        ok, why = D.article_ok(page, items.get(page.get("qid") or "", {}), r) if r.get("article") else (False, "không có bài liên kết")
        r["page"] = page if ok else {}
        r["article_reason"] = why
        r["qid"] = page.get("qid") if ok else None
        r["key"] = page["title"] if ok else D.fold(r["name"])
    rows = [r for r in rows if r.get("province_code")]

    # Gộp nhiều danh hiệu của cùng một nơi: ưu tiên hàng vườn quốc gia làm tên/tỉnh; cấp cao nhất; năm theo cấp đó.
    order = {"vqg": 0, "sinh-quyen": 1, "dia-chat": 2, "tu-lieu": 3, "ramsar": 4, "asean": 5}
    groups: dict[str, list[dict]] = {}
    for r in sorted(rows, key=lambda r: order[r["source"]]):
        groups.setdefault(r["key"], []).append(r)
    merged: list[dict] = []
    for key, grp in groups.items():
        main = grp[0]
        titles = [(g["source"], g["year"], g.get("area")) for g in grp]
        if len(grp) > 1:
            report["gop"].append((main["name"], "; ".join(f"{LABEL[g['source']].split(' (')[0]} ({g['year']})" for g in grp)))
        unesco = [g for g in grp if g["source"] in UNESCO_SOURCES]
        level = "UNESCO" if unesco else "NATIONAL"
        if unesco:
            year = min(g["year"] for g in unesco if g["year"]) if any(g["year"] for g in unesco) else None
        else:
            vqg = [g for g in grp if g["source"] == "vqg" and g["year"]]
            year = vqg[0]["year"] if vqg else min((g["year"] for g in grp if g["year"]), default=None)
        name = main["name"]
        if level == "UNESCO" and main["source"] == "vqg":
            # Vườn quốc gia đồng thời là khu dự trữ sinh quyển: giữ tên vườn, danh hiệu UNESCO ghi ở mục Xếp hạng.
            pass
        codes: list[str] = []
        for g in grp:
            for c in g["province_codes"]:
                if c not in codes:
                    codes.append(c)
        r = dict(main)
        r.update({"name": name, "level": level, "year": year, "titles": titles, "province_codes": codes, "province_code": codes[0],
                  "sources": [g["source"] for g in grp], "list_refs": [(g["list_page"], g["list_revid"]) for g in grp],
                  "refs": [x for g in grp for x in g.get("refs", [])][:2], "merged_from": grp[1:]})
        merged.append(r)

    # Trùng với các đợt trước.
    kept: list[dict] = []
    for r in merged:
        key_name = (D.fold(r["name"]), r["province_code"])
        if (r["qid"] and r["qid"] in prev_qids) or key_name in prev_names or D.slugify(r["name"]) in prev_slugs:
            report["trung"].append((r["name"], "+".join(r["sources"]), "đã có trong đợt trước (" + "; ".join(LABEL[s].split(" (")[0] for s in r["sources"]) + ": danh hiệu này chưa ghi ở bản cũ)"))
            continue
        core = D.fold(re.sub(r"^(Vườn quốc gia|Khu dự trữ sinh quyển( rừng ngập mặn| quần đảo)?|Công viên địa chất toàn cầu|Khu bảo tồn( thiên nhiên)?( đất ngập nước)?|Vùng đất ngập nước)\s+", "", r["name"]))
        near = [f"{n} ({s})" for n, s, c in prev_list if core and len(core) >= 5 and core in D.fold(n)]
        if near:
            report["gan_trung"].append((r["name"], "; ".join(near[:4])))
        kept.append(r)

    name_count = Counter(D.fold(r["name"]) for r in kept)
    for r in kept:
        r["name_display"] = f"{r['name']} ({provinces.short_name(r['province_code'])})" if name_count[D.fold(r["name"])] > 1 else r["name"]
        if name_count[D.fold(r["name"])] > 1:
            report["ten_trung_them_tinh"].append(r["name_display"])
    used = set(prev_slugs)
    for r in kept:
        s = D.slugify(r["name_display"]) or "di-san"
        base, i = s, 2
        while s in used:
            s = f"{base}-{i}"
            i += 1
        used.add(s)
        r["slug"] = s

    prose = V.load(work, WikiClient(work / "cache" / "api"), [(r["page"]["title"], r["page"].get("revid")) for r in kept if r.get("page")])
    out_provinces = {p["code"]: {"code": p["code"], "name": p["name"], "region": p["region"], "heritages": []} for p in provinces.rows}
    counts = Counter()
    for r in kept:
        h = to_heritage(r, provinces, images, items, report, prose)
        out_provinces[r["province_code"]]["heritages"].append(h)
        counts["heritages"] += 1
        counts["media"] += len(h["media"])
        counts["references"] += len(h["references"])
    seed = {
        "meta": {
            "dot": DOT, "generated_at": dt.date.today().isoformat(), "status": "DRAFT (mọi di sản chờ quản trị duyệt)",
            "nguon": list(PAGES), "counts": {"provinces": len(out_provinces), **counts, "timelines": 0, "events": 0},
            "sources": [{"title": v[1], "url": "https://vi.wikipedia.org/wiki/" + v[1].replace(" ", "_"), "license": "CC BY-SA 4.0 (văn bản)", "accessed": NGAY_TRUY_CAP}
                        for v in PAGES.values()]
                       + [{"title": "Wikidata (tọa độ P625, ảnh P18)", "url": "https://www.wikidata.org", "license": "CC0", "accessed": NGAY_TRUY_CAP},
                          {"title": "Wikimedia Commons (ảnh; giấy phép từng ảnh ghi ở media.credit)", "url": "https://commons.wikimedia.org", "license": "theo từng tệp", "accessed": NGAY_TRUY_CAP}],
            "note": "Sinh bởi scripts/du-lieu-di-san/dung_seed_dot_4.py. Di sản thiên nhiên (vườn quốc gia, khu dự trữ sinh quyển, công viên địa chất toàn cầu, "
                    "khu Ramsar, vườn di sản ASEAN) và di sản tư liệu còn thiếu; một nơi nhiều danh hiệu là một di sản. Nội dung trích nguyên văn các đoạn "
                    "văn xuôi đầu bài Wikipedia tiếng Việt (CC BY-SA 4.0, đúng phiên bản dẫn trong nguồn), có lược bớt; tọa độ chỉ từ Wikidata hoặc trang danh sách; "
                    "ảnh chỉ giấy phép mở. Tỉnh theo 34 đơn vị (QĐ 19/2025/QĐ-TTg).",
        },
        "provinces": [p for p in out_provinces.values() if p["heritages"]],
    }
    out_file.write_text(json.dumps(seed, ensure_ascii=False, indent=1), encoding="utf-8")
    report["counts"] = counts
    report["merged"] = kept
    return report


def to_heritage(r: dict, provinces: D.Provinces, images: dict, items: dict, report: dict, prose: dict) -> dict:
    code = r["province_code"]
    page = r.get("page") or {}
    item = items.get(r.get("qid") or "", {})
    name = r["name_display"]
    lat, lon, coord_src = D.pick_coord(r, item, provinces, report, name)
    media = D.pick_media(r, page, item, images, report, name)
    thumbnail = media[0]["url"] if media else None

    kind = {"vqg": "vườn quốc gia", "sinh-quyen": "khu dự trữ sinh quyển thế giới", "dia-chat": "công viên địa chất toàn cầu",
            "ramsar": "khu bảo tồn đất ngập nước", "asean": "vườn di sản ASEAN", "tu-lieu": "di sản tư liệu"}[r["source"]]
    if r["source"] in ("ramsar", "asean") and D.fold(r["name"]).startswith("vuon quoc gia"):
        kind = "vườn quốc gia"
    place_text = provinces.annotate_old(r["location"], r["province_codes"]).strip(" ,;")
    address = D.cap_first(place_text)[:500]
    year = r.get("year")
    level_label = "Di sản được UNESCO ghi danh" if r["level"] == "UNESCO" else "Vườn quốc gia / khu bảo tồn cấp quốc gia"
    built = f"{r['name']} là {kind} tại {place_text}" + (f", được công nhận năm {year}." if year else ".")

    intro = D.intro_of(page)
    short = D.first_sentence(intro) if intro else built
    if len(short) > 300 or D.words(short) < 4:
        short = built if len(built) <= 300 else built[:297] + "…"

    rank_lines = [f"- Cấp: {level_label}"]
    for source, y, area in r["titles"]:
        detail = []
        if y:
            detail.append(f"thành lập năm {y}" if source == "vqg" else f"năm {y}")
        if area:
            detail.append(f"diện tích {area} ha")
        rank_lines.append(f"- Danh hiệu: {LABEL[source]}" + (f" ({', '.join(detail)})" if detail else ""))
    rank_lines.append(f"- Năm: {year}" if year else "- Năm: chưa rõ theo nguồn")
    rank_lines.append(f"- Địa bàn theo nguồn: {place_text}")
    content = D.compose_content(page, prose, built, intro, rank_lines)

    refs = []
    seen_pages = set()
    for list_page, revid in r["list_refs"]:
        if list_page in seen_pages:
            continue
        seen_pages.add(list_page)
        refs.append({"title": f"{list_page} — Wikipedia tiếng Việt (văn bản CC BY-SA 4.0), phiên bản {revid}; truy cập {NGAY_TRUY_CAP}",
                     "url": "https://vi.wikipedia.org/w/index.php?" + (f"oldid={revid}" if revid else "title=" + list_page.replace(" ", "_")), "source_type": "WEBSITE"})
    if page:
        refs.append({"title": f"Bài \"{page['title']}\" — Wikipedia tiếng Việt (văn bản CC BY-SA 4.0), phiên bản {page.get('revid')}; truy cập {NGAY_TRUY_CAP}",
                     "url": f"https://vi.wikipedia.org/w/index.php?oldid={page.get('revid')}" if page.get("revid") else "https://vi.wikipedia.org/wiki/" + page["title"].replace(" ", "_"),
                     "source_type": "WEBSITE"})
    if r.get("qid"):
        refs.append({"title": f"Mục Wikidata {r['qid']} (CC0): tọa độ, ảnh; truy cập {NGAY_TRUY_CAP}", "url": f"https://www.wikidata.org/wiki/{r['qid']}", "source_type": "WEBSITE"})
    for ref in r.get("refs", [])[:2]:
        text = ref.get("text") or ref["url"]
        refs.append({"title": f"{text[:440]}; truy cập {NGAY_TRUY_CAP}"[:500], "url": ref["url"], "source_type": "GOV_DOC" if ref["gov"] else "WEBSITE"})

    return {
        "key": r["slug"], "province_code": code, "name": name[:255], "slug": r["slug"],
        "short_description": short, "content": content, "heritage_type": r["type"], "recognition_level": r["level"],
        "recognized_year": year, "address": address, "latitude": lat, "longitude": lon, "place_id": r.get("qid"),
        "map_point_note": (f"Tọa độ từ {coord_src}" if coord_src else ("Di sản tư liệu: không ghim" if r["type"] == "DOCUMENTARY" else "Chưa có tọa độ từ Wikidata hay trang danh sách")),
        "thumbnail_url": thumbnail, "status": "DRAFT", "timelines": [], "media": media, "references": refs,
        "source_list": "+".join(r["sources"]),
    }


def append_report(report_file: pathlib.Path, report: dict) -> None:
    lines = ["", "## 12. Riêng đợt 4", "",
             "Lựa chọn enum: `heritage_type` NATURAL cho mọi khu thiên nhiên, DOCUMENTARY cho di sản tư liệu; `recognition_level` UNESCO khi có "
             "danh hiệu do UNESCO trao (khu dự trữ sinh quyển thế giới, công viên địa chất toàn cầu, di sản tư liệu Chương trình Ký ức thế giới), "
             "NATIONAL cho vườn quốc gia, khu bảo tồn, khu Ramsar và vườn di sản ASEAN (enum không có cấp quốc tế ngoài UNESCO; danh hiệu ghi ở mục Xếp hạng).",
             "", "### Nơi có nhiều danh hiệu đã gộp thành một di sản", "", "| Di sản | Danh hiệu |", "|---|---|"]
    lines += [f"| {a} | {b} |" for a, b in report["gop"]]
    lines += ["", "### Gần trùng với di sản đợt trước (giữ, vì là đối tượng khác: vườn/khu rộng hơn so với hồ, hang, nhà tù...)", "",
              "| Di sản mới | Di sản đã có (slug) |", "|---|---|"] + [f"| {a} | {b} |" for a, b in report["gan_trung"]]
    with report_file.open("a", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lam-viec", required=True)
    ap.add_argument("--buoc", default="tat-ca", choices=["tai", "thu-thap", "lam-giau", "dung", "tat-ca"])
    ap.add_argument("--out", default=str(REPO / "seed-data" / "seed-dot-4.json"))
    ap.add_argument("--provinces", default=str(REPO / "seed-data" / "provinces.json"))
    ap.add_argument("--quy-doi", default=str(REPO / "scripts" / "du-lieu-di-san" / "quy-doi-tinh-63-34.json"))
    ap.add_argument("--da-co", nargs="+", default=[str(REPO / "seed-data" / f"seed-dot-{i}.json") for i in (1, 2, 3)])
    ap.add_argument("--bao-cao", default=None)
    a = ap.parse_args()
    work = pathlib.Path(a.lam_viec)
    (work / "lam-viec").mkdir(parents=True, exist_ok=True)
    provinces = D.Provinces(pathlib.Path(a.provinces), pathlib.Path(a.quy_doi))

    if a.buoc in ("tai", "tat-ca"):
        download(work)
    rows_file = work / "lam-viec" / "hang-tho-dot4.json"
    if a.buoc in ("thu-thap", "tat-ca"):
        rows = collect(work, provinces)
        rows_file.write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"Thu thập: {len(rows)} hàng ->", rows_file, dict(Counter(r["source"] for r in rows)))
    else:
        rows = json.loads(rows_file.read_text(encoding="utf-8"))
    enriched_file = work / "lam-viec" / "lam-giau-dot4.json"
    if a.buoc in ("lam-giau", "tat-ca"):
        previous = json.loads(enriched_file.read_text(encoding="utf-8")) if enriched_file.exists() else None
        enriched = D.enrich(work, rows, previous)
        enriched_file.write_text(json.dumps(enriched, ensure_ascii=False), encoding="utf-8")
        print(f"Làm giàu: {len(enriched['pages'])} bài, {len(enriched['items'])} mục Wikidata, {len(enriched['images'])} ảnh")
    elif a.buoc == "dung":
        enriched = json.loads(enriched_file.read_text(encoding="utf-8"))
    if a.buoc in ("dung", "tat-ca"):
        existing = [json.loads(pathlib.Path(f).read_text(encoding="utf-8")) for f in a.da_co]
        out_file = pathlib.Path(a.out)
        report_file = pathlib.Path(a.bao_cao) if a.bao_cao else work / "BAO-CAO-dot-4.md"
        report = build(work, rows, enriched, provinces, existing, out_file)
        D.write_report(work, rows, report, provinces, out_file, report_file, DOT)
        append_report(report_file, report)
        print(f"Dựng: {report['counts']['heritages']} di sản, {report['counts']['media']} ảnh -> {out_file}; báo cáo {report_file}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
