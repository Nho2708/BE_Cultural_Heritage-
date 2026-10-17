#!/usr/bin/env bash
# Sao lưu CSDL (BACKUP ... COPY_ONLY) vào /opt/dsvhvn/sao-luu/<loại>/dsvhvn_<thời điểm>.bak và giữ các bản mới nhất.
#   sudo /opt/dsvhvn/bin/sao-luu-csdl.sh hang-dem          lịch hằng đêm (giữ 14 bản), chép thêm lên Cloud Storage nếu có BUCKET_SAO_LUU
#   sudo /opt/dsvhvn/bin/sao-luu-csdl.sh truoc-trien-khai  trước mỗi lần triển khai API (giữ 10 bản)
# In đường dẫn tệp vừa tạo ở dòng cuối.
set -euo pipefail
# shellcheck source=SCRIPTDIR/chung.sh
source "$(dirname "$(readlink -f "$0")")/chung.sh"

loai=${1:-hang-dem}
case $loai in
    hang-dem) giu=14 ;;
    truoc-trien-khai) giu=10 ;;
    *) loi "Loại sao lưu phải là hang-dem hoặc truoc-trien-khai." ;;
esac

thu_muc="$GOC/sao-luu/$loai"
install -d -o "$UID_SQL" -g 0 -m 770 "$thu_muc"
bat_sql || loi "SQL Server không chạy."

co_csdl=$(sqlcmd_sa -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$TEN_CSDL'" | tr -d '[:space:]')
[[ $co_csdl == 1 ]] || loi "Chưa có CSDL $TEN_CSDL (chạy khoi-phuc-csdl.sh trước)."

tep="${TEN_CSDL}_$(date '+%Y%m%d_%H%M%S').bak"
sqlcmd_sa -Q "BACKUP DATABASE [$TEN_CSDL] TO DISK = N'/sao-luu/$loai/$tep' WITH COPY_ONLY, INIT, CHECKSUM" > /dev/null
echo "Đã sao lưu: $thu_muc/$tep ($(du -h "$thu_muc/$tep" | cut -f1))"

# Giữ $giu bản mới nhất.
find "$thu_muc" -maxdepth 1 -name "${TEN_CSDL}_*.bak" -printf '%T@ %p\n' | sort -rn | tail -n +"$((giu + 1))" | cut -d' ' -f2- |
    while read -r cu; do
        rm -f -- "$cu"
        echo "Xóa bản sao lưu cũ: $(basename "$cu")"
    done

# Bản hằng đêm chép thêm ra ngoài máy chủ (bucket tự xóa bản quá 30 ngày). Lỗi chép không làm hỏng bản trên đĩa.
bucket=$(doc_bien BUCKET_SAO_LUU || true)
if [[ $loai == hang-dem && -n $bucket ]]; then
    if ma_truy_cap=$(ma_truy_cap_google) &&
        curl -fsS -o /dev/null -X POST -T "$thu_muc/$tep" \
            -H "Authorization: Bearer $ma_truy_cap" -H 'Content-Type: application/octet-stream' \
            "https://storage.googleapis.com/upload/storage/v1/b/$bucket/o?uploadType=media&name=hang-dem/$tep"; then
        echo "Đã chép lên gs://$bucket/hang-dem/$tep"
    else
        echo "CẢNH BÁO: không chép được bản sao lưu lên gs://$bucket (bản trên đĩa vẫn còn)." >&2
    fi
fi

echo "$thu_muc/$tep"
