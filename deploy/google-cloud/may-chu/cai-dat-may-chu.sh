#!/usr/bin/env bash
# Cài máy chủ Google Cloud lần đầu (Ubuntu 24.04, chạy bằng root). tao-ha-tang.ps1 chép thư mục này lên máy ảo rồi gọi:
#   sudo bash cai-dat-may-chu.sh --registry asia-southeast1-docker.pkg.dev/<dự án>/dsvhvn \
#       --dia-chi-hoc-tap 34.87.10.20 --dia-chi-quan-tri 34.87.10.20:8443 [--bucket-sao-luu <bucket>]
# Làm: múi giờ Việt Nam, 2 GB swap, Docker Engine + Compose, xoay vòng nhật ký Docker, thư mục /opt/dsvhvn, tệp hạ tầng,
# lịch sao lưu. Chạy lại an toàn. Không tạo bí mật: tao-cau-hinh-may-chu.ps1 -TaiLen (máy dev) tải cấu hình lên sau.
# --bo-qua-he-thong: chỉ dựng thư mục và tệp (kiểm thử trong CI, gốc đặt bằng DSVHVN_GOC).
set -euo pipefail
nguon=$(dirname "$(readlink -f "$0")")
# shellcheck source=SCRIPTDIR/bin/chung.sh
source "$nguon/bin/chung.sh"

kho_anh=
dia_chi_hoc_tap=
dia_chi_quan_tri=
bucket=
he_thong=1
while [[ $# -gt 0 ]]; do
    case $1 in
        --registry) kho_anh=$2; shift 2 ;;
        --dia-chi-hoc-tap) dia_chi_hoc_tap=$2; shift 2 ;;
        --dia-chi-quan-tri) dia_chi_quan_tri=$2; shift 2 ;;
        --bucket-sao-luu) bucket=$2; shift 2 ;;
        --bo-qua-he-thong) he_thong=0; shift ;;
        *) loi "Tham số không hợp lệ: $1" ;;
    esac
done
[[ -n $kho_anh && -n $dia_chi_hoc_tap && -n $dia_chi_quan_tri ]] ||
    loi "Cần --registry, --dia-chi-hoc-tap và --dia-chi-quan-tri."
[[ $(id -u) == 0 ]] || loi "Chạy bằng root (sudo)."

if [[ $he_thong == 1 ]]; then
    buoc "Múi giờ Việt Nam"
    timedatectl set-timezone Asia/Ho_Chi_Minh

    buoc "Swap 2 GB (SQL Server và lúc kéo ảnh cần thêm bộ nhớ)"
    if ! swapon --show=NAME --noheadings | grep -q .; then
        fallocate -l 2G /swapfile
        chmod 600 /swapfile
        mkswap /swapfile > /dev/null
        swapon /swapfile
        grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
        echo 'vm.swappiness=10' > /etc/sysctl.d/90-dsvhvn.conf
        sysctl -q -p /etc/sysctl.d/90-dsvhvn.conf
    fi
    swapon --show

    buoc "Docker Engine và Docker Compose"
    if ! command -v docker > /dev/null; then
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -q
        apt-get install -y -q ca-certificates curl
        install -d -m 755 /etc/apt/keyrings
        curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
        chmod a+r /etc/apt/keyrings/docker.asc
        # shellcheck source=/dev/null
        echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
            > /etc/apt/sources.list.d/docker.list
        apt-get update -q
        apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
    fi
    # Nhật ký container xoay vòng (mỗi container tối đa 5 tệp 10 MB) để không đầy đĩa.
    if [[ ! -f /etc/docker/daemon.json ]]; then
        printf '%s\n' '{ "log-driver": "json-file", "log-opts": { "max-size": "10m", "max-file": "5" } }' > /etc/docker/daemon.json
        systemctl restart docker
    fi
    systemctl enable --now docker > /dev/null 2>&1
    docker --version
    docker compose version
fi

buoc "Thư mục $GOC"
install -d -m 755 "$GOC" "$GOC/bin" "$GOC/caddy" "$GOC/nhat-ky" "$GOC/du-lieu"
install -d -m 700 "$GOC/cau-hinh"
install -d -o "$UID_API" -g "$UID_API" -m 750 "$GOC/du-lieu/storage"
install -d -o "$UID_SQL" -g 0 -m 770 "$GOC/sao-luu" "$GOC/sao-luu/hang-dem" "$GOC/sao-luu/truoc-trien-khai" "$GOC/sao-luu/nhap"
[[ -f $TEP_BIEN ]] || install -m 600 /dev/null "$TEP_BIEN"
dat_bien REGISTRY "$kho_anh"
dat_bien DIA_CHI_HOC_TAP "$dia_chi_hoc_tap"
dat_bien DIA_CHI_QUAN_TRI "$dia_chi_quan_tri"
[[ -n $bucket ]] && dat_bien BUCKET_SAO_LUU "$bucket"
ls -la "$GOC"

buoc "Tệp hạ tầng"
if [[ $he_thong == 1 ]]; then
    bash "$nguon/cap-nhat-ha-tang.sh"
else
    bash "$nguon/cap-nhat-ha-tang.sh" --khong-systemd
fi

echo
echo "Cài xong. Bước tiếp theo (trên máy dev):"
echo "  1. tao-cau-hinh-may-chu.ps1 -TaiLen     (cấu hình bí mật)"
echo "  2. chuyen-du-lieu.ps1                   (CSDL và kho tệp của máy demo)"
echo "  3. Chạy CD của backend, Dịch vụ AI và frontend (hoặc gộp vào main)."
