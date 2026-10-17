#!/usr/bin/env bash
# Hàm dùng chung cho các script trên máy chủ (nạp bằng "source"). Gốc mặc định /opt/dsvhvn. Kiểm thử trong CI đặt DSVHVN_GOC
# sang thư mục tạm, DSVHVN_KHONG_KEO_ANH=1 (dùng ảnh dựng tại chỗ, không đăng nhập Artifact Registry) và DSVHVN_BO_QUA_EDGE=1
# (không bật Caddy).

# shellcheck disable=SC2034  # các hằng dưới đây dùng ở script nạp tệp này
GOC=${DSVHVN_GOC:-/opt/dsvhvn}
TEP_BIEN="$GOC/.env"
TEN_CSDL=dsvhvn
DANG_NHAP_UNG_DUNG=dsvhvn_app
UID_API=1654   # người dùng "app" trong ảnh ASP.NET
UID_SQL=10001  # người dùng "mssql" trong ảnh SQL Server
export DSVHVN_GOC="$GOC"

loi() {
    echo "LỖI: $*" >&2
    exit 1
}

buoc() {
    echo
    echo "[$(date '+%H:%M:%S')] $*"
}

compose() {
    docker compose --project-directory "$GOC" -f "$GOC/compose.yml" "$@"
}

# Đọc một biến trong .env (in giá trị, trả mã 1 nếu chưa có). Không in ra màn hình ở nơi gọi với biến bí mật.
doc_bien() {
    local dong
    dong=$(grep -E "^$1=" "$TEP_BIEN" 2>/dev/null | tail -n 1) || return 1
    printf '%s' "${dong#*=}"
}

# Ghi KHOA=GIA_TRI vào .env: thay dòng cũ hoặc thêm mới, giữ quyền 600.
dat_bien() {
    local khoa=$1 gia_tri=$2 tam
    tam=$(mktemp "$GOC/.env.XXXXXX")
    { grep -vE "^$khoa=" "$TEP_BIEN" 2>/dev/null || true; printf '%s=%s\n' "$khoa" "$gia_tri"; } > "$tam"
    chmod 600 "$tam"
    mv "$tam" "$TEP_BIEN"
}

da_cau_hinh() {
    [[ -f $TEP_BIEN ]] && doc_bien MSSQL_SA_PASSWORD > /dev/null && [[ -f $GOC/cau-hinh/appsettings.Production.json ]]
}

trang_thai_suc_khoe() {
    local ma
    ma=$(compose ps -q "$1" 2>/dev/null)
    [[ -n $ma ]] || { echo khong-chay; return; }
    docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$ma" 2>/dev/null || echo khong-ro
}

# Bật SQL Server (nếu chưa) và chờ tới khi kiểm sức khỏe đạt, tối đa khoảng 4 phút.
bat_sql() {
    compose up -d sql > /dev/null || return 1
    for _ in $(seq 1 80); do
        [[ $(trang_thai_suc_khoe sql) == healthy ]] && return 0
        sleep 3
    done
    echo "SQL Server chưa sẵn sàng sau 4 phút (trạng thái: $(trang_thai_suc_khoe sql))." >&2
    return 1
}

# sqlcmd bằng tài khoản sa bên trong container: mật khẩu lấy từ biến của container, không đi qua dòng lệnh trên máy chủ.
sqlcmd_sa() {
    compose exec -T sql bash -c '
        s=/opt/mssql-tools18/bin/sqlcmd; c=-C
        [ -x "$s" ] || { s=/opt/mssql-tools/bin/sqlcmd; c=; }
        SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec "$s" $c -S localhost -U sa -b -I "$@"' bash "$@"
}

# Như sqlcmd_sa nhưng chạy tệp lệnh đọc từ đầu vào chuẩn (tệp migration, câu lệnh chứa mật khẩu).
sqlcmd_sa_dau_vao() {
    compose exec -T sql bash -c '
        s=/opt/mssql-tools18/bin/sqlcmd; c=-C
        [ -x "$s" ] || { s=/opt/mssql-tools/bin/sqlcmd; c=; }
        f=$(mktemp); cat > "$f"
        SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" "$s" $c -S localhost -U sa -b -I -i "$f" "$@"; r=$?
        rm -f "$f"; exit $r' bash "$@"
}

# Mã truy cập của tài khoản dịch vụ gắn với máy ảo (lấy từ máy chủ metadata, hạn 1 giờ), in ra đầu ra chuẩn.
ma_truy_cap_google() {
    curl -fsS -H 'Metadata-Flavor: Google' \
        'http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token' |
        python3 -c 'import json, sys; print(json.load(sys.stdin)["access_token"])'
}

# Kéo ảnh từ Artifact Registry. Kiểm thử trong CI (DSVHVN_KHONG_KEO_ANH=1) chỉ kiểm ảnh dựng tại chỗ đã có.
keo_anh() {
    if [[ ${DSVHVN_KHONG_KEO_ANH:-} == 1 ]]; then
        docker image inspect "$1" > /dev/null
        return
    fi
    local ma_truy_cap
    ma_truy_cap=$(ma_truy_cap_google) || return 1
    printf '%s' "$ma_truy_cap" | docker login -u oauth2accesstoken --password-stdin "https://${1%%/*}" > /dev/null || return 1
    docker pull -q "$1" > /dev/null
}
