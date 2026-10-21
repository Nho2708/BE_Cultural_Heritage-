# -*- coding: utf-8 -*-
"""Bổ sung tọa độ và ảnh cho di sản bản nháp đã đủ 200 từ nội dung nhưng còn thiếu ảnh hoặc tọa độ.

Dùng các nguồn khác với bước làm giàu trước (ảnh chính bài, Wikidata P625/P18), chỉ lấy dữ liệu của chính bài về di sản:
  - tọa độ: GeoData của bài Wikipedia tiếng Việt (`prop=coordinates`), bản mẫu {{coord}} trong wikitext (kể cả trong hộp thông tin),
    rồi GeoData của bài tiếng Anh tương ứng (qua langlinks). Không lấy tâm xã/huyện, không đoán theo địa chỉ. Tọa độ phải trong
    lãnh thổ (8–23,5 vĩ, 102–117,5 kinh) và cách tâm tỉnh của di sản không quá giới hạn như bước dựng seed.
  - ảnh: tệp ảnh nhắc trong wikitext bài tiếng Việt rồi tiếng Anh ([[Tập tin:...]], [[File:...]], tham số ảnh của hộp thông tin),
    tệp phải nằm trên Wikimedia Commons với giấy phép mở (CC0, CC BY, CC BY-SA, phạm vi công cộng); bỏ bản đồ, biểu tượng, cờ, logo.

Cách chạy (bước thu thập ghi kết quả ra tệp JSON, bước áp dụng ghi vào tệp seed):
  python -I -X utf8 scripts/du-lieu-di-san/bo_sung_anh_toa_do.py --lam-viec <thư mục làm việc> --buoc thu-thap seed-data/seed-dot-2.json ...
  python -I -X utf8 scripts/du-lieu-di-san/bo_sung_anh_toa_do.py --lam-viec <thư mục làm việc> --buoc ap-dung seed-data/seed-dot-2.json ...
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from dung_seed_dot_2 import (ALLOWED_LICENSE, BAD_CATEGORY, BAD_IMAGE_EXT, BAD_IMAGE_NAME, MAX_KM_FROM_CENTER, PROVINCE_CENTER,  # noqa: E402
                             km, words)
from nguon_wiki import COMMONS_API, VI_API, WikiClient, chunks  # noqa: E402

EN_API = "https://en.wikipedia.org/w/api.php"
NEED_COORD = {"TANGIBLE", "NATURAL", "MIXED"}
FILE_LINK = re.compile(r"\[\[\s*(?:Tập tin|Tập_tin|Hình|File|Image)\s*:\s*([^|\]\n]+)", re.I)
IMAGE_PARAM = re.compile(r"^\s*\|\s*(?:hình|hình ảnh|image|image_name|ảnh|photo|img)\s*=\s*(?:\[\[\s*(?:Tập tin|File|Hình|Image)\s*:)?\s*([^|\]\n]+\.(?:jpe?g|png|webp))", re.I | re.M)
COORD_TPL = re.compile(r"\{\{\s*coord\s*\|([^{}]*)\}\}", re.I)
IMAGE_EXT = (".jpg", ".jpeg", ".png", ".webp")


def article_title(h: dict) -> str | None:
    for r in h.get("references") or []:
        m = re.match(r'Bài "(.+?)" — Wikipedia tiếng Việt', r.get("title") or "")
        if m:
            return m.group(1)
    return None


def targets_of(seeds: list[pathlib.Path]) -> list[dict]:
    out = []
    for seed in seeds:
        data = json.loads(seed.read_text(encoding="utf-8"))
        for p in data["provinces"]:
            for h in p["heritages"]:
                if words(h.get("content") or "") < 200:
                    continue
                need_coord = h["heritage_type"] in NEED_COORD and h.get("latitude") is None
                need_image = not h.get("thumbnail_url") or not any(m.get("url") == h.get("thumbnail_url") for m in h.get("media") or [])
                if need_coord or need_image:
                    out.append({"slug": h["slug"], "name": h["name"], "province_code": h.get("province_code") or p["code"],
                                "article": article_title(h), "need_coord": need_coord, "need_image": need_image,
                                "has_urls": [m.get("url") for m in h.get("media") or []]})
    return out


def parse_coord_template(text: str) -> tuple[float, float] | None:
    """{{coord|21.03|105.85|...}} hoặc {{coord|21|1|48|N|105|51|0|E|...}}; trả về (vĩ, kinh)."""
    for m in COORD_TPL.finditer(text):
        parts = [p.strip() for p in m.group(1).split("|")]
        nums: list[float] = []
        signs: list[str] = []
        for p in parts:
            if "=" in p:
                continue
            if p.upper() in ("N", "S", "E", "W"):
                signs.append(p.upper())
                continue
            try:
                nums.append(float(p.replace(",", ".")))
            except ValueError:
                break
        if signs:
            half = len(nums) // 2
            if half == 0 or len(nums) % 2:
                continue
            la = sum(v / 60 ** i for i, v in enumerate(nums[:half]))
            lo = sum(v / 60 ** i for i, v in enumerate(nums[half:]))
            if "S" in signs:
                la = -la
            if "W" in signs:
                lo = -lo
            return la, lo
        if len(nums) >= 2:
            return nums[0], nums[1]
    return None


def query_pages(client: WikiClient, api: str, titles: list[str], prop: str, extra: dict) -> dict[str, dict]:
    out: dict[str, dict] = {}
    for group in chunks(titles, 50):
        d = client.get(api, {"action": "query", "titles": "|".join(group), "prop": prop, "redirects": 1, **extra})["data"]
        redirects = {r["from"]: r["to"] for r in d.get("query", {}).get("redirects", [])}
        pages = {p["title"]: p for p in d.get("query", {}).get("pages", [])}
        for t in group:
            out[t] = pages.get(redirects.get(t, t), {})
    return out


def commons_info(client: WikiClient, files: list[str]) -> dict[str, dict]:
    out: dict[str, dict] = {}
    for group in chunks(sorted(set(files)), 50):
        d = client.get(COMMONS_API, {"action": "query", "titles": "|".join("File:" + f for f in group), "prop": "imageinfo",
                                     "iiprop": "url|extmetadata|mime|size",
                                     "iiextmetadatafilter": "LicenseShortName|Artist|ImageDescription|Categories"})["data"]
        normalized = {n["from"]: n["to"] for n in d.get("query", {}).get("normalized", [])}
        pages = {p["title"]: p for p in d.get("query", {}).get("pages", [])}
        for f in group:
            title = normalized.get("File:" + f, "File:" + f)
            p = pages.get(title, {})
            info = (p.get("imageinfo") or [{}])[0]
            md = {k: v.get("value") for k, v in info.get("extmetadata", {}).items()}
            out[f] = {"missing": p.get("missing", False) or p.get("imagerepository") != "shared" and not info, "url": info.get("url"),
                      "mime": info.get("mime"), "license": re.sub(r"<[^>]+>", "", md.get("LicenseShortName") or ""),
                      "artist": re.sub(r"<[^>]+>", "", md.get("Artist") or ""),
                      "description": re.sub(r"<[^>]+>", "", md.get("ImageDescription") or ""), "categories": md.get("Categories") or ""}
    return out


def image_candidates(wikitext: str) -> list[str]:
    names: list[str] = []
    for m in list(IMAGE_PARAM.finditer(wikitext)) + list(FILE_LINK.finditer(wikitext)):
        f = m.group(1).strip().replace("_", " ")
        f = f[0].upper() + f[1:] if f else f
        if f.lower().endswith(IMAGE_EXT) and f not in names:
            names.append(f)
    return names


def check_coord(c: tuple[float, float] | None, code: str) -> tuple[float, float] | None:
    if not c:
        return None
    la, lo = float(c[0]), float(c[1])
    if not (8.0 <= la <= 23.5 and 102.0 <= lo <= 117.5):
        return None
    if code in PROVINCE_CENTER and km((la, lo), PROVINCE_CENTER[code]) > MAX_KM_FROM_CENTER:
        return None
    return round(la, 6), round(lo, 6)


def geodata(page: dict) -> tuple[float, float] | None:
    for c in page.get("coordinates") or []:
        if c.get("globe", "earth") == "earth" and c.get("primary", True) is not False:
            return c["lat"], c["lon"]
    return None


def collect(client: WikiClient, targets: list[dict]) -> dict[str, dict]:
    titles = sorted({t["article"] for t in targets if t["article"]})
    vi = query_pages(client, VI_API, titles, "coordinates|langlinks|revisions",
                     {"coprimary": "all", "colimit": "max", "lllang": "en", "lllimit": "max", "rvprop": "content", "rvslots": "main"})
    en_titles = sorted({ll["title"] for p in vi.values() for ll in p.get("langlinks") or []})
    en = query_pages(client, EN_API, en_titles, "coordinates|revisions", {"coprimary": "all", "colimit": "max", "rvprop": "content", "rvslots": "main"})

    def text_of(p: dict) -> str:
        return ((p.get("revisions") or [{}])[0].get("slots", {}).get("main", {}).get("content")) or ""

    result: dict[str, dict] = {}
    all_files: list[str] = []
    for t in targets:
        p = vi.get(t["article"] or "", {})
        e = en.get(next((ll["title"] for ll in p.get("langlinks") or []), ""), {})
        r: dict = {"name": t["name"], "article": t["article"]}
        if t["need_coord"]:
            for src, c in (("GeoData bài Wikipedia tiếng Việt", geodata(p)), ("{{coord}} trong bài Wikipedia tiếng Việt", parse_coord_template(text_of(p))),
                           ("GeoData bài Wikipedia tiếng Anh", geodata(e)), ("{{coord}} trong bài Wikipedia tiếng Anh", parse_coord_template(text_of(e)))):
                ok = check_coord(c, t["province_code"])
                if ok:
                    r["latitude"], r["longitude"], r["coord_source"] = ok[0], ok[1], src
                    break
        if t["need_image"]:
            r["candidates"] = (image_candidates(text_of(p)) + image_candidates(text_of(e)))[:12]
            all_files += r["candidates"]
        result[t["slug"]] = r
    infos = commons_info(client, all_files)
    for t in targets:
        r = result[t["slug"]]
        media = []
        for f in r.pop("candidates", []):
            info = infos.get(f) or {}
            if info.get("missing") or not info.get("url") or not (info.get("mime") or "").startswith("image/"):
                continue
            if f.lower().endswith(BAD_IMAGE_EXT) or BAD_IMAGE_NAME.search(f) or BAD_CATEGORY.search(info.get("categories") or ""):
                continue
            lic = (info.get("license") or "").strip()
            if not ALLOWED_LICENSE.search(lic):
                continue
            url = info["url"].split("?", 1)[0]
            if url in t["has_urls"] or any(m["url"] == url for m in media):
                continue
            artist = re.sub(r"\s+", " ", info.get("artist") or "").strip() or "Không rõ tác giả"
            credit = f"{artist} · {lic} · Wikimedia Commons"
            if len(credit) > 255:
                artist = artist[: 255 - len(f" · {lic} · Wikimedia Commons") - 1] + "…"
                credit = f"{artist} · {lic} · Wikimedia Commons"
            desc = re.sub(r"\s+", " ", info.get("description") or "").strip()
            media.append({"media_type": "IMAGE", "url": url, "caption": desc if 0 < len(desc) <= 200 else t["name"], "credit": credit,
                          "sort_order": len(t["has_urls"]) + len(media) + 1, "source_note": "ảnh trong bài Wikipedia"})
            if len(media) >= 2:
                break
        if media:
            r["media"] = media
    return result


def apply(seeds: list[pathlib.Path], found: dict[str, dict]) -> tuple[int, int]:
    n_coord = n_image = 0
    for seed in seeds:
        data = json.loads(seed.read_text(encoding="utf-8"))
        changed = False
        for p in data["provinces"]:
            for h in p["heritages"]:
                r = found.get(h["slug"])
                if not r:
                    continue
                if r.get("latitude") is not None and h.get("latitude") is None and h["heritage_type"] in NEED_COORD:
                    h["latitude"], h["longitude"] = r["latitude"], r["longitude"]
                    h["map_point_note"] = f"Tọa độ từ {r['coord_source']}"
                    n_coord += 1
                    changed = True
                if r.get("media") and not h.get("thumbnail_url"):
                    h.setdefault("media", [])
                    h["media"] += [m for m in r["media"] if all(x.get("url") != m["url"] for x in h["media"])]
                    h["thumbnail_url"] = r["media"][0]["url"]
                    n_image += 1
                    changed = True
        if changed:
            data["meta"]["counts"]["media"] = sum(len(h.get("media") or []) for p in data["provinces"] for h in p["heritages"])
            seed.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
    return n_coord, n_image


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("seeds", nargs="+")
    ap.add_argument("--lam-viec", required=True, help="thư mục làm việc (cache/api, lam-viec/bo-sung-anh-toa-do.json)")
    ap.add_argument("--buoc", choices=["thu-thap", "ap-dung"], required=True)
    a = ap.parse_args()
    work = pathlib.Path(a.lam_viec)
    out = work / "lam-viec" / "bo-sung-anh-toa-do.json"
    seeds = [pathlib.Path(s) for s in a.seeds]
    targets = targets_of(seeds)
    print(f"Mục tiêu: {len(targets)} (thiếu tọa độ {sum(t['need_coord'] for t in targets)}, thiếu ảnh {sum(t['need_image'] for t in targets)},"
          f" không có bài: {sum(1 for t in targets if not t['article'])})")
    if a.buoc == "thu-thap":
        client = WikiClient(work / "cache" / "api")
        found = collect(client, targets)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(found, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"Tìm được tọa độ: {sum(1 for r in found.values() if r.get('latitude') is not None)}; ảnh: {sum(1 for r in found.values() if r.get('media'))};"
              f" lượt API mới {client.calls}, cache {client.cache_hits}. Ghi {out}")
    else:
        n_coord, n_image = apply(seeds, json.loads(out.read_text(encoding="utf-8")))
        print(f"Đã ghi vào seed: {n_coord} tọa độ, {n_image} ảnh đại diện")
    return 0


if __name__ == "__main__":
    sys.exit(main())
