using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phần của CSDL v4 mà luồng "Bài học cho lớp theo cấp" cần. Thêm bảng <c>lesson_sections</c> (các phần nội dung của bài theo khung của
    /// cấp: chuyện kể, hình, bài đọc, mốc thời gian, trích tư liệu gốc, câu hỏi gợi suy nghĩ; kèm CHECK mốc có năm, đoạn trích có nguồn,
    /// hình có đường dẫn). Đổi <c>ai_generations</c> như v4: <c>lesson_id</c> được NULL, thêm <c>heritage_id</c> (di sản được dịch hoặc
    /// tóm tắt), <c>task_type</c> nhận thêm HERITAGE_TRANSLATION và HERITAGE_SUMMARY, và CHECK <c>CK_ai_generations_subject</c> bắt mỗi dòng
    /// thuộc đúng một bài học hoặc một di sản. Dữ liệu cũ (mọi dòng đều có bài học) thỏa CHECK mới. Các bảng và cột khác của v4 thuộc luồng khác.
    /// </summary>
    public partial class ThemPhanNoiDungBaiHocTheoCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations");

            migrationBuilder.AlterColumn<long>(
                name: "lesson_id",
                table: "ai_generations",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "heritage_id",
                table: "ai_generations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lesson_sections",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    lesson_id = table.Column<long>(type: "bigint", nullable: false),
                    section_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    image_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    milestone_year = table.Column<int>(type: "int", nullable: true),
                    event_id = table.Column<long>(type: "bigint", nullable: true),
                    source_citation = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_sections", x => x.id);
                    table.CheckConstraint("CK_lesson_sections_required", "([section_type] <> 'MILESTONE' OR [milestone_year] IS NOT NULL) AND ([section_type] <> 'SOURCE_EXCERPT' OR [source_citation] IS NOT NULL) AND ([section_type] <> 'IMAGE' OR [image_url] IS NOT NULL)");
                    table.CheckConstraint("CK_lesson_sections_section_type", "[section_type] IN ('STORY', 'IMAGE', 'READING', 'MILESTONE', 'SOURCE_EXCERPT', 'REFLECTION')");
                    table.ForeignKey(
                        name: "fk_lesson_sections_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lesson_sections_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_generations_heritage_id",
                table: "ai_generations",
                column: "heritage_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_generations_subject",
                table: "ai_generations",
                sql: "([lesson_id] IS NOT NULL AND [heritage_id] IS NULL AND [task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION')) OR ([lesson_id] IS NULL AND [heritage_id] IS NOT NULL AND [task_type] IN ('HERITAGE_TRANSLATION', 'HERITAGE_SUMMARY'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations",
                sql: "[task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION', 'HERITAGE_TRANSLATION', 'HERITAGE_SUMMARY')");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_sections_event_id",
                table: "lesson_sections",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_sections_lesson_order",
                table: "lesson_sections",
                columns: new[] { "lesson_id", "sort_order" });

            migrationBuilder.AddForeignKey(
                name: "fk_ai_generations_heritage_id",
                table: "ai_generations",
                column: "heritage_id",
                principalTable: "heritages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ai_generations_heritage_id",
                table: "ai_generations");

            migrationBuilder.DropTable(
                name: "lesson_sections");

            migrationBuilder.DropIndex(
                name: "IX_ai_generations_heritage_id",
                table: "ai_generations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_generations_subject",
                table: "ai_generations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations");

            migrationBuilder.DropColumn(
                name: "heritage_id",
                table: "ai_generations");

            migrationBuilder.AlterColumn<long>(
                name: "lesson_id",
                table: "ai_generations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations",
                sql: "[task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION')");
        }
    }
}
