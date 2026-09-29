using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Thêm hai cột tìm kiếm không dấu cho <c>heritages</c> (luồng Khám phá di sản công khai). Lý do: collation Vietnamese_CI_AI chỉ bỏ dấu
    /// thanh, còn coi ă, â, ê, ô, ơ, ư, đ là chữ riêng nên "thang long" không tìm ra "Thăng Long". <c>search_name</c> là tên đã gấp
    /// (chữ thường không dấu, dấu cách đứng đầu) để xếp kết quả khớp tên lên trước; <c>search_text</c> là tên, mô tả ngắn, địa chỉ, nội dung
    /// đã gấp và nối lại để tìm từ khóa. Cột NULL được phép: ứng dụng tự tính mỗi khi lưu di sản và điền cho dữ liệu cũ lúc khởi động.
    /// </summary>
    public partial class ThemCotTimKiemKhongDauChoDiSan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "search_name",
                table: "heritages",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "search_text",
                table: "heritages",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "search_name",
                table: "heritages");

            migrationBuilder.DropColumn(
                name: "search_text",
                table: "heritages");
        }
    }
}
