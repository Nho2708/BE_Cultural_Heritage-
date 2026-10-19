#!/usr/bin/env bash
# Cài cấu hình bí mật do tao-cau-hinh-may-chu.ps1 (máy dev) tải lên một thư mục tạm, rồi xóa thư mục tạm:
#   sudo /opt/dsvhvn/bin/nap-cau-hinh.sh /tmp/dsvhvn-cau-hinh-xxxx
# Thư mục tạm gồm appsettings.Production.json (API), ai.env (Dịch vụ AI), bi-mat.env (MSSQL_SA_PASSWORD, MAT_KHAU_CSDL_UNG_DUNG).
# API và Dịch vụ AI đã triển khai thì tạo lại container để đọc cấu hình mới. Không in giá trị bí mật.
set -euo pipefail
# shellcheck source=SCRIPTDIR/chung.sh
source "$(dirname "$(readlink -f "$0")")/chung.sh"

nguon=${1:-}
[[ -d $nguon ]] || loi "Cần thư mục chứa tệp cấu hình đã tải lên."
trap 'rm -rf -- "$nguon"' EXIT
for tep in appsettings.Production.json ai.env bi-mat.env; do
    [[ -s $nguon/$tep ]] || loi "Thiếu $tep trong $nguon."
done
[[ -f $TEP_BIEN ]] || loi "Chưa cài máy chủ (chạy cai-dat-may-chu.sh trước)."

# Bỏ ký tự CR nếu tệp được soạn trên Windows.
sed -i 's/\r$//' "$nguon/ai.env" "$nguon/bi-mat.env"

doc_trong() { grep -E "^$1=" "$nguon/bi-mat.env" | tail -n 1 | cut -d= -f2-; }
mat_khau_sa=$(doc_trong MSSQL_SA_PASSWORD)
mat_khau_ung_dung=$(doc_trong MAT_KHAU_CSDL_UNG_DUNG)
[[ -n $mat_khau_sa && -n $mat_khau_ung_dung ]] || loi "bi-mat.env thiếu MSSQL_SA_PASSWORD hoặc MAT_KHAU_CSDL_UNG_DUNG."

# Mật khẩu sa chỉ đặt được ở lần khởi động đầu của SQL Server: đã có dữ liệu thì không nhận mật khẩu sa khác.
sa_cu=$(doc_bien MSSQL_SA_PASSWORD || true)
if [[ -n $sa_cu && $sa_cu != "$mat_khau_sa" ]] && docker volume inspect dsvhvn_sql-data > /dev/null 2>&1; then
    loi "Mật khẩu sa mới khác mật khẩu đang dùng mà SQL Server đã có dữ liệu. Giữ bi-mat.json cũ trên máy dev, hoặc đổi bằng ALTER LOGIN sa trước."
fi
ung_dung_cu=$(doc_bien MAT_KHAU_CSDL_UNG_DUNG || true)

install -d -m 700 "$GOC/cau-hinh"
install -o root -g "$UID_API" -m 640 "$nguon/appsettings.Production.json" "$GOC/cau-hinh/appsettings.Production.json"
install -o root -g root -m 600 "$nguon/ai.env" "$GOC/cau-hinh/ai.env"
dat_bien MSSQL_SA_PASSWORD "$mat_khau_sa"
dat_bien MAT_KHAU_CSDL_UNG_DUNG "$mat_khau_ung_dung"
echo "Đã cài appsettings.Production.json, ai.env và mật khẩu CSDL vào $GOC."

# Đổi mật khẩu đăng nhập của API khi CSDL đã có.
if [[ -n $ung_dung_cu && $ung_dung_cu != "$mat_khau_ung_dung" && $(trang_thai_suc_khoe sql) == healthy ]]; then
    "$GOC/bin/khoi-phuc-csdl.sh" || echo "CẢNH BÁO: chưa cập nhật được mật khẩu đăng nhập của API." >&2
fi

for dich_vu in api ai; do
    khoa=$([[ $dich_vu == api ]] && echo API_TAG || echo AI_TAG)
    if doc_bien "$khoa" > /dev/null && [[ -n $(compose ps -q "$dich_vu" 2> /dev/null) ]]; then
        compose up -d --no-deps --force-recreate "$dich_vu" > /dev/null
        echo "Đã tạo lại $dich_vu để đọc cấu hình mới."
    fi
done
