using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Chỉ sửa dữ liệu, không đổi lược đồ (model snapshot giữ nguyên). Quy tắc nguồn tham chiếu chỉ nhận loại nguồn
    /// GOV_DOC, WEBSITE, BOOK; lần nạp di sản đợt 1 trước đó ghi thêm GOV_WEBSITE và UNESCO của tệp dữ liệu. Đổi hai loại
    /// này về WEBSITE (cơ quan đã ghi trong tiêu đề nguồn) để mọi dòng hiện có sửa và kiểm được theo quy tắc mới.
    /// </summary>
    public partial class ChuanHoaLoaiNguonThamChieu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE heritage_references SET source_type = N'WEBSITE' WHERE source_type IN (N'GOV_WEBSITE', N'UNESCO');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Không khôi phục được loại nguồn cũ (GOV_WEBSITE hay UNESCO) vì đã gộp về WEBSITE; giá trị WEBSITE vẫn hợp lệ.
        }
    }
}
