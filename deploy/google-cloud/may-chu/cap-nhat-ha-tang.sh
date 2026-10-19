#!/usr/bin/env bash
# Chép tệp hạ tầng (compose.yml, Caddyfile, script trong bin/, lịch sao lưu) từ thư mục chứa script này vào /opt/dsvhvn.
# cai-dat-may-chu.sh gọi ở lần cài đầu; CD của backend gọi trước mỗi lần triển khai API để máy chủ luôn theo repo.
#   sudo bash cap-nhat-ha-tang.sh [--khong-systemd]
# Dịch vụ đang chạy mà định nghĩa trong compose.yml đổi thì được tạo lại; Caddyfile đổi thì Caddy nạp lại, không ngắt kết nối.
set -euo pipefail
nguon=$(dirname "$(readlink -f "$0")")
# shellcheck source=SCRIPTDIR/bin/chung.sh
source "$nguon/bin/chung.sh"

systemd=1
[[ ${1:-} == --khong-systemd ]] && systemd=0
[[ -d $GOC ]] || loi "Chưa có $GOC (chạy cai-dat-may-chu.sh trước)."

# Bỏ ký tự CR nếu tệp đi qua máy Windows.
sed -i 's/\r$//' "$nguon/compose.yml" "$nguon/Caddyfile" "$nguon"/bin/*.sh "$nguon"/systemd/* 2> /dev/null || true

caddy_cu=$(cat "$GOC/caddy/Caddyfile" 2> /dev/null || true)
install -d -m 755 "$GOC/bin" "$GOC/caddy"
install -m 644 "$nguon/compose.yml" "$GOC/compose.yml"
install -m 644 "$nguon/Caddyfile" "$GOC/caddy/Caddyfile"
install -m 755 "$nguon"/bin/*.sh "$GOC/bin/"
echo "Đã chép compose.yml, Caddyfile và script vào $GOC."

if [[ $systemd == 1 ]]; then
    install -m 644 "$nguon"/systemd/dsvhvn-sao-luu.service "$nguon"/systemd/dsvhvn-sao-luu.timer /etc/systemd/system/
    systemctl daemon-reload
    systemctl enable --now dsvhvn-sao-luu.timer > /dev/null 2>&1
    echo "Lịch sao lưu hằng đêm: $(systemctl list-timers dsvhvn-sao-luu.timer --no-legend | awk '{print $1, $2, $3}')"
fi

da_cau_hinh || { echo "Chưa có cấu hình bí mật: bỏ qua bước cập nhật dịch vụ đang chạy."; exit 0; }

for dich_vu in sql api ai web edge; do
    [[ -n $(compose ps -q "$dich_vu" 2> /dev/null) ]] || continue
    compose up -d --no-deps "$dich_vu" 2>&1 | grep -E 'Recreat|Start' || true
done

if [[ -n $(compose ps -q edge 2> /dev/null) && $caddy_cu != "$(cat "$GOC/caddy/Caddyfile")" ]]; then
    compose exec -T edge caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile && echo "Caddy đã nạp Caddyfile mới."
fi
