using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemThuVienBaiMauVaVietLaiMotPhan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_generations_subject",
                table: "ai_generations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations");

            migrationBuilder.AddColumn<string>(
                name: "sample_note",
                table: "lessons",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sample_requested_at",
                table: "lessons",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sample_reviewed_at",
                table: "lessons",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "sample_reviewed_by",
                table: "lessons",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sample_status",
                table: "lessons",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_lesson_id",
                table: "lessons",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "task_type",
                table: "ai_generations",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_lessons_sample_reviewed_by",
                table: "lessons",
                column: "sample_reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_sample_status",
                table: "lessons",
                column: "sample_status",
                filter: "[sample_status] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_source_lesson_id",
                table: "lessons",
                column: "source_lesson_id",
                filter: "[source_lesson_id] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_lessons_sample_status",
                table: "lessons",
                sql: "[sample_status] IN ('PENDING', 'APPROVED', 'REJECTED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_generations_subject",
                table: "ai_generations",
                sql: "([lesson_id] IS NOT NULL AND [heritage_id] IS NULL AND [task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION', 'LESSON_SECTION_REWRITE')) OR ([lesson_id] IS NULL AND [heritage_id] IS NOT NULL AND [task_type] IN ('HERITAGE_TRANSLATION', 'HERITAGE_SUMMARY'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations",
                sql: "[task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION', 'HERITAGE_TRANSLATION', 'HERITAGE_SUMMARY', 'LESSON_SECTION_REWRITE')");

            migrationBuilder.AddForeignKey(
                name: "fk_lessons_sample_reviewed_by",
                table: "lessons",
                column: "sample_reviewed_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_lessons_source_lesson_id",
                table: "lessons",
                column: "source_lesson_id",
                principalTable: "lessons",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_lessons_sample_reviewed_by",
                table: "lessons");

            migrationBuilder.DropForeignKey(
                name: "fk_lessons_source_lesson_id",
                table: "lessons");

            migrationBuilder.DropIndex(
                name: "IX_lessons_sample_reviewed_by",
                table: "lessons");

            migrationBuilder.DropIndex(
                name: "IX_lessons_sample_status",
                table: "lessons");

            migrationBuilder.DropIndex(
                name: "IX_lessons_source_lesson_id",
                table: "lessons");

            migrationBuilder.DropCheckConstraint(
                name: "CK_lessons_sample_status",
                table: "lessons");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_generations_subject",
                table: "ai_generations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations");

            migrationBuilder.DropColumn(
                name: "sample_note",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "sample_requested_at",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "sample_reviewed_at",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "sample_reviewed_by",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "sample_status",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "source_lesson_id",
                table: "lessons");

            migrationBuilder.AlterColumn<string>(
                name: "task_type",
                table: "ai_generations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_generations_subject",
                table: "ai_generations",
                sql: "([lesson_id] IS NOT NULL AND [heritage_id] IS NULL AND [task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION')) OR ([lesson_id] IS NULL AND [heritage_id] IS NOT NULL AND [task_type] IN ('HERITAGE_TRANSLATION', 'HERITAGE_SUMMARY'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_generations_task_type",
                table: "ai_generations",
                sql: "[task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION', 'HERITAGE_TRANSLATION', 'HERITAGE_SUMMARY')");
        }
    }
}
