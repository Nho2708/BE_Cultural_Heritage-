using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemKiemTungCauVaBaiHocDaXong : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "try_count",
                table: "attempt_answers",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "lesson_completions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    lesson_id = table.Column<long>(type: "bigint", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_completions", x => x.id);
                    table.ForeignKey(
                        name: "fk_lesson_completions_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lesson_completions_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_completions_lesson_id",
                table: "lesson_completions",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "UX_lesson_completions_student_lesson",
                table: "lesson_completions",
                columns: new[] { "student_id", "lesson_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lesson_completions");

            migrationBuilder.DropColumn(
                name: "try_count",
                table: "attempt_answers");
        }
    }
}
