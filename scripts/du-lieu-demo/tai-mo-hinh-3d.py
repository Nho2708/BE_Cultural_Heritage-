"""Tải mô hình 3D công khai trong seed-data/mo-hinh-3d.json từ Sketchfab rồi nén thành .glb vừa giới hạn của nền tảng.

Tệp ra nằm ở seed-data/mo-hinh-3d/ (bỏ qua khỏi git vì nặng); nap-du-lieu-demo.py đọc từ đó để gắn mô hình thật cho di sản.

Nền tảng chỉ nhận .glb glTF 2.0 một tệp, tối đa 15 MB, không tham chiếu tài nguyên ngoài. Script:
  1. xin đường tải bản glTF (tệp zip) bằng khóa API Sketchfab của tài khoản người dùng;
  2. giải nén vào thư mục tạm riêng cho từng mô hình (dữ liệu tải về coi như không tin cậy, không chạy gì trong đó);
  3. nén bằng glTF Transform: giảm lưới về khoảng 300 nghìn mặt, ảnh bề mặt WebP tối đa 2048 px, lượng tử hóa đỉnh
     (trình xem <model-viewer> đọc được cả hai mà không cần tải bộ giải mã ngoài); không dùng nén Meshopt và nhân bản lưới
     (EXT_mesh_gpu_instancing) vì trình xem không mở được mô hình có hai phần này;
  4. còn quá 14,5 MB thì nén lại với ảnh 1024 px, rồi giảm lưới thêm;
  5. kiểm tệp ra giống máy chủ (đầu tệp, phiên bản 2.0, không tài nguyên ngoài) và ghi kết quả vào seed-data/mo-hinh-3d/ket-qua-tai.json.

Khóa API: Sketchfab → Settings → Password & API → API Token. Đưa vào bằng một trong ba cách:
  biến môi trường SKETCHFAB_TOKEN, tệp %USERPROFILE%\\.sketchfab_token, hoặc --token.

Cách chạy (cần Node.js, script gọi glTF Transform 4 qua npx):
  python scripts/du-lieu-demo/tai-mo-hinh-3d.py                 # chỉ các mô hình chọn chính (mỗi di sản một mô hình)
  python scripts/du-lieu-demo/tai-mo-hinh-3d.py --tat-ca        # cả mô hình dự phòng
  python scripts/du-lieu-demo/tai-mo-hinh-3d.py --chi den-hung  # một di sản (theo slug) hoặc một mô hình (theo mã Sketchfab)
Mô hình đã có tệp thì bỏ qua; thêm --lam-lai để tải và nén lại.
"""
import argparse, json, os, shutil, struct, subprocess, sys, tempfile, time, urllib.request, zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
MODEL_LIST = REPO / "seed-data" / "mo-hinh-3d.json"
MODEL_DIR = Path(os.environ.get("MO_HINH_DIR") or REPO / "seed-data" / "mo-hinh-3d")
WORK_DIR = Path(tempfile.gettempdir()) / "dsvhvn-mo-hinh-3d"
MAX_BYTES = 15 * 1024 * 1024
TARGET_BYTES = int(14.5 * 1024 * 1024)
TARGET_FACES = 300_000
CLI = ["@gltf-transform/cli@4"]


def token_from(args):
    if args.token: return args.token.strip()
    if os.environ.get("SKETCHFAB_TOKEN"): return os.environ["SKETCHFAB_TOKEN"].strip()
    f = Path.home() / ".sketchfab_token"
    if f.exists(): return f.read_text(encoding="utf-8").strip()
    sys.exit("Chưa có khóa API Sketchfab (SKETCHFAB_TOKEN, tệp ~/.sketchfab_token hoặc --token).")


def download_url(uid, token):
    req = urllib.request.Request(f"https://api.sketchfab.com/v3/models/{uid}/download", headers={"Authorization": f"Token {token}"})
    with urllib.request.urlopen(req, timeout=60) as r:
        d = json.load(r)
    if "gltf" in d: return d["gltf"]["url"], "zip"
    if "glb" in d: return d["glb"]["url"], "glb"
    raise RuntimeError("Sketchfab không trả bản glTF hay GLB")


def fetch(url, dest):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=600) as r, open(dest, "wb") as f:
                shutil.copyfileobj(r, f)
            return
        except Exception:
            if attempt == 2: raise
            time.sleep(5)


def safe_extract(zip_path, dest):
    """Giải nén nhưng bỏ mọi mục trỏ ra ngoài thư mục đích."""
    root = dest.resolve()
    with zipfile.ZipFile(zip_path) as z:
        for m in z.infolist():
            target = (dest / m.filename).resolve()
            if root not in target.parents and target != root: continue
            z.extract(m, dest)


def drop_materials(gltf_path, names):
    """Bỏ các phần lưới dùng vật liệu trong `names` (ví dụ tấm ảnh bầu trời làm nền) rồi ghi bản .gltf mới cạnh bản gốc."""
    doc = json.loads(gltf_path.read_text(encoding="utf-8"))
    mats = doc.get("materials", [])
    for mesh in doc.get("meshes", []):
        mesh["primitives"] = [p for p in mesh["primitives"]
                              if "material" not in p or mats[p["material"]].get("name") not in names]
    for node in doc.get("nodes", []):
        if "mesh" in node and not doc["meshes"][node["mesh"]]["primitives"]:
            del node["mesh"]
    out = gltf_path.with_name("da-bo-nen.gltf")
    out.write_text(json.dumps(doc), encoding="utf-8")
    return out


def npx():
    exe = shutil.which("npx.cmd") or shutil.which("npx")
    if not exe: sys.exit("Không thấy npx (cần Node.js).")
    return exe


def optimize(src, out, ratio, size):
    cmd = [npx(), "-y", *CLI, "optimize", str(src), str(out), "--compress", "quantize", "--texture-compress", "webp",
           "--texture-size", str(size), "--instance", "false"]
    cmd += ["--simplify", "false"] if ratio >= 0.95 else ["--simplify-ratio", f"{ratio:.3f}", "--simplify-error", "0.01"]
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0 or not out.exists():
        raise RuntimeError("glTF Transform lỗi: " + (p.stderr or p.stdout)[-600:])


def inspect_glb(path):
    """Kiểm giống máy chủ: đầu tệp glTF, phiên bản 2, độ dài khớp, asset.version 2.0, không uri ra ngoài."""
    b = path.read_bytes()
    if len(b) < 20 or struct.unpack_from("<I", b, 0)[0] != 0x46546C67: return "không phải glb"
    if struct.unpack_from("<I", b, 4)[0] != 2: return "không phải glTF 2"
    if struct.unpack_from("<I", b, 8)[0] != len(b): return "độ dài sai"
    n, kind = struct.unpack_from("<II", b, 12)
    if kind != 0x4E4F534A: return "thiếu khối JSON"
    doc = json.loads(b[20:20 + n])
    if doc.get("asset", {}).get("version") != "2.0": return "asset.version khác 2.0"
    for key in ("buffers", "images"):
        for item in doc.get(key, []):
            if "uri" in item and not str(item["uri"]).lower().startswith("data:"): return "có tài nguyên ngoài"
    if len(b) > MAX_BYTES: return f"quá 15 MB ({len(b) / 1e6:.1f} MB)"
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tat-ca", action="store_true", help="tải cả mô hình dự phòng")
    ap.add_argument("--chi", help="slug di sản hoặc mã Sketchfab (đầu mã cũng được)")
    ap.add_argument("--token")
    ap.add_argument("--giu-tep-goc", action="store_true", help="không xóa tệp tải về sau khi nén")
    ap.add_argument("--lam-lai", action="store_true", help="tải và nén lại cả mô hình đã có tệp")
    args = ap.parse_args()

    items = json.load(open(MODEL_LIST, encoding="utf-8"))
    if args.chi:
        items = [i for i in items if i["heritageSlug"] == args.chi or i["sketchfabUid"].startswith(args.chi)]
    elif not args.tat_ca:
        items = [i for i in items if i["role"] == "chinh"]
    if not items: sys.exit("Không có mô hình nào khớp.")
    token = token_from(args)

    MODEL_DIR.mkdir(parents=True, exist_ok=True)
    result_path = MODEL_DIR / "ket-qua-tai.json"
    results = json.load(open(result_path, encoding="utf-8")) if result_path.exists() else {}

    for it in items:
        uid, name = it["sketchfabUid"], it["title"]
        out = MODEL_DIR / it["file"]
        print(f"\n== {name} ({uid[:8]})", flush=True)
        if out.exists() and not args.lam_lai:
            print(f"   đã có {out.name}, bỏ qua", flush=True)
            continue
        try:
            work = WORK_DIR / uid
            work.mkdir(parents=True, exist_ok=True)
            url, kind = download_url(uid, token)
            archive = work / ("ban-goc.zip" if kind == "zip" else "ban-goc.glb")
            if not archive.exists():
                fetch(url, archive)
            print(f"   tải về {archive.stat().st_size / 1e6:.1f} MB", flush=True)
            if kind == "zip":
                safe_extract(archive, work / "giai-nen")
                found = [p for p in sorted((work / "giai-nen").rglob("*.gltf")) if p.name != "da-bo-nen.gltf"]
                if not found: raise RuntimeError("không thấy tệp .gltf trong gói tải về")
                src = found[0]
            else:
                src = archive
            if it.get("excludeMaterials"):
                if src.suffix != ".gltf": raise RuntimeError("chỉ bỏ được vật liệu khi tải bản .gltf")
                src = drop_materials(src, set(it["excludeMaterials"]))

            faces = it.get("faces") or TARGET_FACES
            ratio = min(1.0, TARGET_FACES / faces)
            attempts = [(ratio, 2048), (ratio, 1024), (ratio * 0.5, 1024), (ratio * 0.25, 1024), (ratio * 0.25, 512)]
            tmp = work / "tam.glb"
            for r, size in attempts:
                if tmp.exists(): tmp.unlink()
                optimize(src, tmp, r, size)
                print(f"   nén (giữ {r:.0%} đỉnh, ảnh {size}px) → {tmp.stat().st_size / 1e6:.1f} MB", flush=True)
                if tmp.stat().st_size <= TARGET_BYTES: break
            problem = inspect_glb(tmp)
            if problem: raise RuntimeError("tệp nén chưa đạt: " + problem)
            shutil.move(tmp, out)
            results[uid] = {"heritageSlug": it["heritageSlug"], "title": name, "file": out.name,
                            "bytes": out.stat().st_size, "keptVertexRatio": round(r, 3), "textureSize": size, "ok": True}
            print(f"   ĐẠT: {out.name} ({out.stat().st_size / 1e6:.1f} MB)", flush=True)
            if not args.giu_tep_goc: shutil.rmtree(work, ignore_errors=True)
        except Exception as e:
            results[uid] = {"heritageSlug": it["heritageSlug"], "title": name, "ok": False, "error": str(e)[:500]}
            print(f"   LỖI: {e}", flush=True)
        json.dump(results, open(result_path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)

    ok = sum(1 for i in items if (MODEL_DIR / i["file"]).exists())
    print(f"\nXong: {ok}/{len(items)} mô hình có tệp trong {MODEL_DIR}.")


if __name__ == "__main__":
    main()
