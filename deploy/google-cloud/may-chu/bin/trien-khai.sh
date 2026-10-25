#!/usr/bin/env bash
# Triển khai một dịch vụ bằng ảnh đã đẩy lên Artifact Registry; hỏng thì tự chạy lại ảnh trước. CD của ba repo gọi script này
# qua SSH, khi cần vẫn chạy tay được:
#   sudo /opt/dsvhvn/bin/trien-khai.sh api|ai|web <mã ảnh, là mã commit>
# api: sao lưu CSDL, dừng API, áp migrations.sql (idempotent, lấy trong ảnh mới), chạy bản mới, kiểm /health.
# ai, web: chạy bản mới, kiểm sức khỏe. CSDL không tự khôi phục khi hỏng: script in lệnh khôi phục từ bản vừa sao lưu.
set -euo pipefail
# shellcheck source=SCRIPTDIR/chung.sh
source "$(dirname "$(readlink -f "$0")")/chung.sh"

dich_vu=${1:-}
ma_anh=${2:-}
case $dich_vu in
    api) khoa=API_TAG ;;
    ai) khoa=AI_TAG ;;
    web) khoa=WEB_TAG ;;
    *) loi "Dịch vụ phải là api, ai hoặc web." ;;
esac
[[ $ma_anh =~ ^[A-Za-z0-9._-]{1,128}$ ]] || loi "Mã ảnh không hợp lệ: '$ma_anh'."
da_cau_hinh || loi "Chưa có cấu hình (chạy tao-cau-hinh-may-chu.ps1 -TaiLen trên máy dev)."

# Mỗi lúc chỉ một lượt triển khai (ba repo có thể gộp cùng lúc).
install -d -m 755 "$GOC/nhat-ky"
exec 9> "$GOC/nhat-ky/.trien-khai.lock"
flock -w 900 9 || loi "Chờ quá 15 phút mà lượt triển khai khác chưa xong."

kho=$(doc_bien REGISTRY) || loi "Chưa có REGISTRY trong .env."
anh="$kho/$dich_vu:$ma_anh"
ma_cu=$(doc_bien "$khoa" || true)
tep_sao_luu=

kiem_suc_khoe() {
    for _ in $(seq 1 45); do
        case $dich_vu in
            api) curl -fsS -o /dev/null --max-time 5 http://127.0.0.1:8080/health && return 0 ;;
            ai) curl -fsS -o /dev/null --max-time 5 http://127.0.0.1:8090/health && return 0 ;;
            web) compose exec -T web wget -q -O /dev/null http://127.0.0.1:8080/ 2> /dev/null &&
                compose exec -T web wget -q -O /dev/null http://127.0.0.1:8081/ 2> /dev/null && return 0 ;;
        esac
        sleep 2
    done
    return 1
}

ap_migration() {
    local tam ma_tam
    tam=$(mktemp -d)
    ma_tam=$(docker create "$anh") || return 1
    docker cp "$ma_tam:/app/migrations.sql" "$tam/migrations.sql" > /dev/null || { docker rm "$ma_tam" > /dev/null; return 1; }
    docker rm "$ma_tam" > /dev/null
    # Tệp có chữ tiếng Việt (UTF-8): -f 65001 như script triển khai của máy demo.
    sqlcmd_sa_dau_vao -d "$TEN_CSDL" -f 65001 < "$tam/migrations.sql" > /dev/null || { rm -rf "$tam"; return 1; }
    rm -rf "$tam"
}

trien_khai() {
    buoc "Kéo ảnh $anh"
    keo_anh "$anh" || return 1

    if [[ $dich_vu == api ]]; then
        buoc "Sao lưu CSDL"
        tep_sao_luu=$("$GOC/bin/sao-luu-csdl.sh" truoc-trien-khai | tee /dev/stderr | tail -n 1) || return 1
        echo "Bản sao lưu: $tep_sao_luu"

        buoc "Dừng API"
        compose stop api > /dev/null || return 1

        buoc "Áp migration (migrations.sql trong ảnh mới, idempotent)"
        ap_migration || return 1
    fi

    buoc "Chạy bản mới ($khoa=$ma_anh)"
    dat_bien "$khoa" "$ma_anh"
    [[ $dich_vu == ai ]] && chuan_bi_mo_hinh
    compose up -d --no-deps "$dich_vu" > /dev/null || return 1

    buoc "Kiểm sức khỏe"
    kiem_suc_khoe || { echo "Dịch vụ $dich_vu không khỏe sau 90 giây." >&2; return 1; }
    echo "Dịch vụ $dich_vu đã khỏe."
}

tra_ban_truoc() {
    echo
    echo "TRIỂN KHAI HỎNG: trả bản trước." >&2
    if [[ -n $ma_cu && $ma_cu != "$ma_anh" ]]; then
        dat_bien "$khoa" "$ma_cu"
        compose up -d --no-deps "$dich_vu" > /dev/null && kiem_suc_khoe &&
            echo "Đã chạy lại bản trước ($khoa=$ma_cu)." >&2 ||
            echo "Bản trước ($ma_cu) cũng không khỏe: xem 'docker compose logs $dich_vu' trong $GOC." >&2
    else
        echo "Không có bản trước để trả (lần triển khai đầu tiên hoặc triển khai lại cùng mã ảnh)." >&2
    fi
    compose logs --no-color --tail 40 "$dich_vu" >&2 || true
    if [[ -n $tep_sao_luu ]]; then
        echo >&2
        echo "CSDL KHÔNG được tự khôi phục. Migration của dự án chỉ thêm nên bản trước thường vẫn chạy được." >&2
        echo "Cần khôi phục CSDL về lúc trước triển khai thì chạy:" >&2
        echo "  sudo $GOC/bin/khoi-phuc-csdl.sh --ghi-de $tep_sao_luu" >&2
    fi
}

echo "Triển khai $dich_vu: $ma_anh (bản đang chạy: ${ma_cu:-chưa có})"
if ! trien_khai; then
    tra_ban_truoc
    exit 1
fi

# Cửa vào luôn chạy (lần triển khai đầu, hoặc sau khi khởi động lại máy mà edge chưa lên). Kiểm thử trong CI bỏ qua bước này
# (DSVHVN_BO_QUA_EDGE=1) để Caddy không xin chứng chỉ cho địa chỉ giả.
if [[ ${DSVHVN_BO_QUA_EDGE:-} != 1 ]]; then
    compose up -d --no-deps edge > /dev/null 2>&1 || echo "CẢNH BÁO: chưa bật được edge (Caddy)." >&2
fi

# Giữ 3 ảnh mới nhất của dịch vụ này trên đĩa (ảnh đang dùng không xóa được thì bỏ qua).
docker image ls "$kho/$dich_vu" --format '{{.CreatedAt}}|{{.Repository}}:{{.Tag}}' | sort -r | tail -n +4 | cut -d'|' -f2 |
    while read -r cu; do docker image rm "$cu" > /dev/null 2>&1 || true; done

echo
echo "Triển khai $dich_vu thành công ($ma_anh)."
