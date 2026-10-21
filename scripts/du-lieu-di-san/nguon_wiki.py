# -*- coding: utf-8 -*-
"""Gọi API MediaWiki (Wikipedia tiếng Việt, Wikidata, Wikimedia Commons) có bộ nhớ đệm trên đĩa.

- Mọi lượt gọi tuần tự, nghỉ tối thiểu một giây giữa hai lượt, gửi User-Agent mô tả dự án và tham số maxlag.
- Máy chủ trả 429 hoặc lỗi maxlag thì chờ theo Retry-After (mặc định 5 giây) rồi thử lại, tối đa 6 lần.
- Phản hồi lưu vào thư mục cache theo mã băm của địa chỉ; chạy lại không tải lại. Nội dung tải về là dữ liệu
  không tin cậy: chỉ đọc làm dữ liệu, không bao giờ thực thi.
"""
from __future__ import annotations

import hashlib
import json
import pathlib
import time
import urllib.error
import urllib.parse
import urllib.request

USER_AGENT = "DiVietCapstone/1.0 (student project; quangnho278@gmail.com)"
VI_API = "https://vi.wikipedia.org/w/api.php"
WIKIDATA_API = "https://www.wikidata.org/w/api.php"
COMMONS_API = "https://commons.wikimedia.org/w/api.php"


class WikiClient:
    def __init__(self, cache_dir: pathlib.Path, pause_seconds: float = 1.0) -> None:
        self.cache_dir = cache_dir
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self.pause = pause_seconds
        self._last = 0.0
        self.calls = 0
        self.cache_hits = 0

    def get(self, api: str, params: dict) -> dict:
        query = urllib.parse.urlencode({**params, "format": "json", "formatversion": 2, "maxlag": 5})
        url = api + "?" + query
        key = hashlib.sha256(url.encode("utf-8")).hexdigest()
        path = self.cache_dir / f"{key}.json"
        if path.exists():
            self.cache_hits += 1
            return json.loads(path.read_text(encoding="utf-8"))
        data = self._fetch(url)
        path.write_text(json.dumps({"url": url, "data": data}, ensure_ascii=False), encoding="utf-8")
        return {"url": url, "data": data}

    def _fetch(self, url: str) -> dict:
        for attempt in range(6):
            wait = self.pause - (time.monotonic() - self._last)
            if wait > 0:
                time.sleep(wait)
            self._last = time.monotonic()
            self.calls += 1
            try:
                req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
                with urllib.request.urlopen(req, timeout=120) as r:
                    body = json.load(r)
            except urllib.error.HTTPError as e:
                if e.code in (429, 503):
                    retry = int(e.headers.get("Retry-After", "5") or 5)
                    time.sleep(min(max(retry, 5), 60))
                    continue
                raise
            except (urllib.error.URLError, TimeoutError):
                time.sleep(5 * (attempt + 1))
                continue
            if "error" in body and body["error"].get("code") == "maxlag":
                time.sleep(5 * (attempt + 1))
                continue
            return body
        raise RuntimeError(f"Gọi API thất bại sau nhiều lần: {url[:120]}")


def chunks(items: list, size: int):
    for i in range(0, len(items), size):
        yield items[i:i + size]
