using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemPhanDuocDocCuaThuyetMinh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_heritage_narrations_heritage_language",
                table: "heritage_narrations");

            migrationBuilder.AddColumn<string>(
                name: "part",
                table: "heritage_narrations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "SUMMARY");

            migrationBuilder.CreateIndex(
                name: "UX_heritage_narrations_heritage_language_part",
                table: "heritage_narrations",
                columns: new[] { "heritage_id", "language_code", "part" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_heritage_narrations_part",
                table: "heritage_narrations",
                sql: "[part] IN ('SUMMARY', 'CONTENT')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_heritage_narrations_heritage_language_part",
                table: "heritage_narrations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_heritage_narrations_part",
                table: "heritage_narrations");

            migrationBuilder.DropColumn(
                name: "part",
                table: "heritage_narrations");

            migrationBuilder.CreateIndex(
                name: "UX_heritage_narrations_heritage_language",
                table: "heritage_narrations",
                columns: new[] { "heritage_id", "language_code" },
                unique: true);
        }
    }
}
