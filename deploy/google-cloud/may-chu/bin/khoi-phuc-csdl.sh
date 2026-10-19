#!/usr/bin/env bash
# Nạp CSDL cho máy chủ và tạo đăng nhập SQL riêng của API (dsvhvn_app, chỉ đọc và ghi dữ liệu, không dùng sa).
#   sudo /opt/dsvhvn/bin/khoi-phuc-csdl.sh <tệp .bak>   khôi phục từ bản sao lưu (ví dụ bản hằng đêm của máy demo)
#   sudo /opt/dsvhvn/bin/khoi-phuc-csdl.sh --tao-trong  tạo CSDL trống (bảng do migration của lần triển khai API tạo)
#   sudo /opt/dsvhvn/bin/khoi-phuc-csdl.sh              chỉ cập nhật đăng nhập của API (sau khi đổi mật khẩu trong .env)
#   thêm --ghi-de để thay CSDL đang có: script sao lưu bản hiện tại trước khi thay.
# CSDL đã có mà không có --ghi-de thì giữ nguyên dữ liệu, chỉ cập nhật đăng nhập của API.
# Mật khẩu của dsvhvn_app lấy ở .env (MAT_KHAU_CSDL_UNG_DUNG), trùng với chuỗi kết nối trong appsettings.Production.json.
set -euo pipefail
# shellcheck source=SCRIPTDIR/chung.sh
source "$(dirname "$(readlink -f "$0")")/chung.sh"

tep_bak=
tao_trong=0
ghi_de=0
for thamso in "$@"; do
    case $thamso in
        --tao-trong) tao_trong=1 ;;
        --ghi-de) ghi_de=1 ;;
        -*) loi "Tham số không hợp lệ: $thamso" ;;
        *) tep_bak=$thamso ;;
    esac
done
[[ -z $tep_bak || $tao_trong == 0 ]] || loi "Chỉ chọn một: tệp .bak hoặc --tao-trong."
da_cau_hinh || loi "Chưa có cấu hình (chạy tao-cau-hinh-may-chu.ps1 -TaiLen trên máy dev)."
mat_khau_ung_dung=$(doc_bien MAT_KHAU_CSDL_UNG_DUNG) || loi "Chưa có MAT_KHAU_CSDL_UNG_DUNG trong .env."
# Mật khẩu do tao-cau-hinh-may-chu.ps1 sinh chỉ gồm chữ và số: đặt thẳng vào câu lệnh SQL và chuỗi kết nối không cần thoát ký tự.
[[ $mat_khau_ung_dung =~ ^[A-Za-z0-9]{16,}$ ]] || loi "MAT_KHAU_CSDL_UNG_DUNG phải gồm ít nhất 16 chữ hoặc số."

buoc "Bật SQL Server"
bat_sql || loi "SQL Server không chạy."

co_csdl=$(sqlcmd_sa -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$TEN_CSDL'" | tr -d '[:space:]')
if [[ -z $tep_bak && $tao_trong == 0 ]]; then
    [[ $co_csdl == 1 ]] || loi "Chưa có CSDL $TEN_CSDL: cần tệp .bak hoặc --tao-trong."
    echo "Chỉ cập nhật đăng nhập của API."
elif [[ $co_csdl == 1 && $ghi_de == 0 ]]; then
    echo "CSDL $TEN_CSDL đã có: giữ nguyên dữ liệu, chỉ cập nhật đăng nhập của API (thêm --ghi-de để thay)."
else
    if [[ $co_csdl == 1 ]]; then
        buoc "Sao lưu CSDL hiện tại trước khi thay"
        "$GOC/bin/sao-luu-csdl.sh" truoc-trien-khai
        # API đang chạy thì dừng trong lúc thay CSDL (tác vụ nền của API có thể chiếm kết nối duy nhất của chế độ một người dùng).
        if [[ -n $(compose ps -q --status running api 2> /dev/null) ]]; then
            compose stop api > /dev/null
            trap 'compose start api > /dev/null || true' EXIT
        fi
    fi

    if [[ $tao_trong == 1 ]]; then
        buoc "Tạo CSDL trống $TEN_CSDL"
        if [[ $co_csdl == 1 ]]; then
            sqlcmd_sa -Q "ALTER DATABASE [$TEN_CSDL] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$TEN_CSDL];" > /dev/null
        fi
        sqlcmd_sa -Q "CREATE DATABASE [$TEN_CSDL]" > /dev/null
    else
        [[ -f $tep_bak ]] || loi "Không thấy tệp $tep_bak."
        # SQL Server chỉ đọc được tệp trong thư mục sao-luu (gắn vào container ở /sao-luu).
        thu_muc_nhap="$GOC/sao-luu/nhap"
        install -d -o "$UID_SQL" -g 0 -m 770 "$thu_muc_nhap"
        if [[ $(readlink -f "$(dirname "$tep_bak")") != "$(readlink -f "$thu_muc_nhap")" ]]; then
            install -o "$UID_SQL" -g 0 -m 640 "$tep_bak" "$thu_muc_nhap/"
        else
            chown "$UID_SQL":0 "$tep_bak"
        fi
        tep_trong_sql="/sao-luu/nhap/$(basename "$tep_bak")"

        buoc "Đọc danh sách tệp trong bản sao lưu"
        # Mỗi dòng: tên logic|loại (D dữ liệu, L nhật ký).
        mapfile -t tep_logic < <(sqlcmd_sa -h -1 -W -s '|' -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$tep_trong_sql'" |
            awk -F'|' 'NF >= 3 && ($3 == "D" || $3 == "L") { print $1 "|" $3 }')
        [[ ${#tep_logic[@]} -gt 0 ]] || loi "Không đọc được danh sách tệp của bản sao lưu."
        di_chuyen=
        so_d=0
        so_l=0
        for dong in "${tep_logic[@]}"; do
            ten=${dong%|*}
            kieu=${dong##*|}
            if [[ $kieu == D ]]; then
                duoi=$([[ $so_d == 0 ]] && echo ".mdf" || echo "_$so_d.ndf")
                so_d=$((so_d + 1))
            else
                duoi=$([[ $so_l == 0 ]] && echo "_log.ldf" || echo "_log$so_l.ldf")
                so_l=$((so_l + 1))
            fi
            ten_sql=$(printf '%s' "$ten" | sed "s/'/''/g")
            di_chuyen+="MOVE N'$ten_sql' TO N'/var/opt/mssql/data/$TEN_CSDL$duoi', "
            echo "  $ten ($kieu) -> /var/opt/mssql/data/$TEN_CSDL$duoi"
        done

        buoc "Khôi phục $(basename "$tep_bak") thành CSDL $TEN_CSDL"
        # Chế độ một người dùng và RESTORE trong cùng một phiên để không kết nối nào chen vào giữa.
        mot_nguoi=
        [[ $co_csdl == 1 ]] && mot_nguoi="ALTER DATABASE [$TEN_CSDL] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
        sqlcmd_sa -Q "${mot_nguoi}RESTORE DATABASE [$TEN_CSDL] FROM DISK = N'$tep_trong_sql' WITH ${di_chuyen}REPLACE, RECOVERY, STATS = 25"
        sqlcmd_sa -Q "ALTER DATABASE [$TEN_CSDL] SET MULTI_USER; ALTER AUTHORIZATION ON DATABASE::[$TEN_CSDL] TO [sa];" > /dev/null
    fi
fi

buoc "Đăng nhập SQL của API ($DANG_NHAP_UNG_DUNG): chỉ đọc và ghi dữ liệu"
# Câu lệnh chứa mật khẩu đi qua đầu vào chuẩn, không nằm trên dòng lệnh.
sqlcmd_sa_dau_vao > /dev/null <<SQL
IF SUSER_ID(N'$DANG_NHAP_UNG_DUNG') IS NULL
    CREATE LOGIN [$DANG_NHAP_UNG_DUNG] WITH PASSWORD = N'$mat_khau_ung_dung', CHECK_POLICY = ON, DEFAULT_DATABASE = [$TEN_CSDL];
ELSE
    ALTER LOGIN [$DANG_NHAP_UNG_DUNG] WITH PASSWORD = N'$mat_khau_ung_dung', DEFAULT_DATABASE = [$TEN_CSDL];
GO
USE [$TEN_CSDL];
IF USER_ID(N'$DANG_NHAP_UNG_DUNG') IS NULL
    CREATE USER [$DANG_NHAP_UNG_DUNG] FOR LOGIN [$DANG_NHAP_UNG_DUNG];
ELSE
    ALTER USER [$DANG_NHAP_UNG_DUNG] WITH LOGIN = [$DANG_NHAP_UNG_DUNG];
ALTER ROLE db_datareader ADD MEMBER [$DANG_NHAP_UNG_DUNG];
ALTER ROLE db_datawriter ADD MEMBER [$DANG_NHAP_UNG_DUNG];
GO
SQL

so_bang=$(sqlcmd_sa -d "$TEN_CSDL" -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables" | tr -d '[:space:]')
echo
echo "Xong: CSDL $TEN_CSDL có $so_bang bảng; API đăng nhập bằng $DANG_NHAP_UNG_DUNG."
