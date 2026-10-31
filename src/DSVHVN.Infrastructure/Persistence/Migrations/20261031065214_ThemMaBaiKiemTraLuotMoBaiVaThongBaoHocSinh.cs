using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemMaBaiKiemTraLuotMoBaiVaThongBaoHocSinh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "access_code",
                table: "quizzes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lesson_visits",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    lesson_id = table.Column<long>(type: "bigint", nullable: false),
                    first_opened_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    last_opened_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_visits", x => x.id);
                    table.ForeignKey(
                        name: "fk_lesson_visits_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lesson_visits_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "student_notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    lesson_id = table.Column<long>(type: "bigint", nullable: false),
                    quiz_id = table.Column<long>(type: "bigint", nullable: true),
                    title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    sent_by = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    read_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_notifications", x => x.id);
                    table.CheckConstraint("CK_student_notifications_kind", "[kind] IN ('LESSON', 'QUIZ')");
                    table.ForeignKey(
                        name: "fk_student_notifications_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_notifications_quiz_id",
                        column: x => x.quiz_id,
                        principalTable: "quizzes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_notifications_sent_by",
                        column: x => x.sent_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_notifications_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_quizzes_access_code",
                table: "quizzes",
                column: "access_code",
                unique: true,
                filter: "[access_code] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_visits_lesson_id",
                table: "lesson_visits",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "UX_lesson_visits_student_lesson",
                table: "lesson_visits",
                columns: new[] { "student_id", "lesson_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_student_notifications_lesson_id",
                table: "student_notifications",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_notifications_quiz_id",
                table: "student_notifications",
                column: "quiz_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_notifications_sent_by",
                table: "student_notifications",
                column: "sent_by");

            migrationBuilder.CreateIndex(
                name: "IX_student_notifications_student_read",
                table: "student_notifications",
                columns: new[] { "student_id", "read_at" });

            // Bài đã phát hành trước đó cũng có mã: KT + 6 ký tự của bảng 32 ký tự dùng chung với mã bài học.
            migrationBuilder.Sql("""
                DECLARE @bang char(32) = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';
                UPDATE quizzes SET access_code = 'KT'
                    + SUBSTRING(@bang, 1 + ABS(CHECKSUM(NEWID()) % 32), 1) + SUBSTRING(@bang, 1 + ABS(CHECKSUM(NEWID()) % 32), 1)
                    + SUBSTRING(@bang, 1 + ABS(CHECKSUM(NEWID()) % 32), 1) + SUBSTRING(@bang, 1 + ABS(CHECKSUM(NEWID()) % 32), 1)
                    + SUBSTRING(@bang, 1 + ABS(CHECKSUM(NEWID()) % 32), 1) + SUBSTRING(@bang, 1 + ABS(CHECKSUM(NEWID()) % 32), 1)
                WHERE status IN ('PUBLISHED', 'CLOSED');
                """);

            // Hạn mức tăng theo giá gói: gói trả tiền phải nhiều quyền lợi hơn gói rẻ hơn, gói Miễn phí đủ dùng thử mà không tốn chi phí AI.
            migrationBuilder.Sql("""
                UPDATE plans SET max_teachers = 3, max_ai_requests = 50 WHERE price = 0;
                UPDATE plans SET max_teachers = 10, max_ai_requests = 500 WHERE name = N'Cơ bản';
                UPDATE plans SET max_teachers = 30, max_ai_requests = 1500 WHERE name = N'Nâng cao';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE plans SET max_teachers = 10, max_ai_requests = 300 WHERE price = 0;
                UPDATE plans SET max_teachers = 5, max_ai_requests = 200 WHERE name = N'Cơ bản';
                UPDATE plans SET max_teachers = 20, max_ai_requests = 1000 WHERE name = N'Nâng cao';
                """);

            migrationBuilder.DropTable(
                name: "lesson_visits");

            migrationBuilder.DropTable(
                name: "student_notifications");

            migrationBuilder.DropIndex(
                name: "UX_quizzes_access_code",
                table: "quizzes");

            migrationBuilder.DropColumn(
                name: "access_code",
                table: "quizzes");
        }
    }
}
