# -*- coding: utf-8 -*-
"""Quét ký hiệu viết tắt làm việc nội bộ trong mã nguồn, commit message và tên nhánh.

Nhóm quy ước: mọi định danh, chú thích, chuỗi hiển thị, tên tệp, tài liệu trong repo, commit message
và tên nhánh viết bằng tên chức năng, không dùng ký hiệu làm việc nội bộ. Bản này dùng trong CI;
các biểu thức giữ khớp với script quét của nhóm.

Dùng:
  python .github/scripts/kiem-ky-hieu.py [--base <nhánh gốc>]
Mã thoát: 0 = sạch, 1 = có ký hiệu.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

QUY_TAC = {
    "mã màn hình": r"\bS-\d{1,2}\b",
    "mã luồng": r"\bL(?:10|[0-9])(?:\.\d+)?\b",
    "mã chức năng nền": r"\bNS-\d{1,2}\b",
    "mã tác nhân": r"\bAC-\d{1,2}\b",
    "mã kiểu liệt kê": r"\bE-\d{2}\b",
    "mã quan hệ": r"\bR-\d{2,3}\b",
    "mã hạng mục công việc": r"\bC\d{1,2}\.\d{1,2}\b",
    "mã quy ước": r"\bM-\d{1,2}\b",
    "mã yêu cầu và ràng buộc": r"\b(?:NFR-[A-Z]{1,2}-\d+|KT-\d+|CON-\d+|DF-\d+|EE-\d+|MH-\d+)\b",
    # Không tính tọa độ địa lý dạng chữ cái + độ, phút, giây.
    "mã quyết định": r"\b[QDN]\d{1,2}[a-z]?\b(?![°'\"′″]|\s+\d|\s*=\s*[A-E]\b)",
    "lựa chọn của quyết định": r"\bQ\d{1,2}[a-z]?\s*=\s*[A-E]\b",
    "mã đánh giá cơ sở dữ liệu": r"(?<![-\w])L-\d{2}\b",
    "nhãn phiên bản": r"\bV[23]-\d{1,2}\b",
    # Không tính "Amazon S3", "AWS S3", "S3 bucket", "S3 compatible".
    "mã sprint": r"(?<!Amazon )(?<!AWS )\bS(?:1[0-4]|[1-9])\b(?!\s*(?:bucket|compatible|API|-compatible))",
    "mã mục tiêu": r"\bO-\d\b",
    "mã use case": r"\bUC-\d{1,2}\b",
    "mã quy tắc nghiệp vụ": r"\bBR-\d{1,2}\b",
    "mã thông báo": r"\bMSG\d{2}\b",
    "mã tính năng": r"\bFE-\d{1,2}\b",
    "mã giới hạn": r"\bLI-\d{1,2}\b",
}
DUOI_TEP = {".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".css", ".json", ".html", ".cshtml",
            ".py", ".sql", ".sh", ".ps1", ".yml", ".yaml", ".md", ".csproj", ".toml", ".txt"}
BO_QUA = {"node_modules", "bin", "obj", "dist", ".git", ".vs", "coverage", "TestResults", ".venv", "venv",
          "__pycache__", ".pytest_cache", ".expo", "android", "ios"}


def dem(text: str) -> Counter:
    c = Counter()
    for ten, rx in QUY_TAC.items():
        c[ten] += len(re.findall(rx, text))
    return +c


def git(*args: str) -> str:
    r = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.stdout if r.returncode == 0 else ""


def quet_tep() -> int:
    tong = 0
    tep = git("ls-files", "-z", "--cached", "--others", "--exclude-standard").split("\0")
    for ten in sorted(set(t for t in tep if t)):
        p = Path(ten)
        if p.suffix.lower() not in DUOI_TEP or any(phan in BO_QUA for phan in p.parts):
            continue
        if "Migrations" in p.parts and p.name.endswith(".Designer.cs"):
            continue
        if not p.is_file():
            continue
        c = dem(p.read_text(encoding="utf-8", errors="ignore"))
        if c:
            tong += sum(c.values())
            print(f"  {ten}: " + ", ".join(f"{k} {v}" for k, v in c.items()))
    return tong


def quet_commit(base: str) -> int:
    if not git("rev-parse", "--verify", "--quiet", base).strip():
        print(f"  (không có nhánh gốc '{base}', bỏ qua commit message)")
        return 0
    goc = git("merge-base", "HEAD", base).strip()
    if not goc:
        print(f"  (không tìm được điểm rẽ nhánh với '{base}', bỏ qua commit message)")
        return 0
    tong = 0
    for dong in git("log", f"{goc}..HEAD", "--format=%h %s%n%b%x00").split("\0"):
        dong = dong.strip()
        if dong and dem(dong):
            tong += 1
            print(f"  commit {dong.splitlines()[0]}")
    return tong


def quet_ten_nhanh() -> int:
    nhanh = os.environ.get("GITHUB_HEAD_REF") or os.environ.get("GITHUB_REF_NAME") \
        or git("rev-parse", "--abbrev-ref", "HEAD").strip()
    # Tên nhánh viết thường: đổi "-l0-", "-s10-" thành dạng viết hoa rồi mới so.
    chuan = re.sub(r"(?<=[-/])([ls])(?=-?\d)", lambda m: " " + m.group(1).upper(), nhanh)
    if dem(chuan):
        print(f"  tên nhánh: {nhanh}")
        return 1
    return 0


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description="Quét ký hiệu viết tắt làm việc nội bộ.")
    ap.add_argument("--base", default="origin/main", help="nhánh gốc để lấy commit của nhánh (mặc định origin/main)")
    a = ap.parse_args()
    print("Tệp:")
    n_tep = quet_tep()
    print("Commit message:")
    n_commit = quet_commit(a.base)
    print("Tên nhánh:")
    n_nhanh = quet_ten_nhanh()
    tong = n_tep + n_commit + n_nhanh
    print(f"Kết quả: {n_tep} lần trong tệp, {n_commit} commit, {n_nhanh} tên nhánh.")
    return 1 if tong else 0


if __name__ == "__main__":
    sys.exit(main())
