using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phần của CSDL v4 mà luồng "Ngân hàng câu hỏi theo dạng" cần: <c>questions</c> thêm hình minh họa, đoạn tư liệu và nguồn, cấp phù hợp
    /// (<c>level_min</c>, <c>level_max</c>); <c>answers</c> thêm hình, vế phải của cặp, vị trí đúng, chỗ trống; <c>question_type</c> nhận đủ 8 dạng;
    /// CHECK cấp hợp lệ, đoạn tư liệu bắt buộc với dạng đọc tư liệu, vị trí và chỗ trống trong 1..20. Câu cũ được gán cấp phù hợp mặc định
    /// theo bảng dạng câu theo cấp: một đáp án đúng dùng ở cấp 2-3; Đúng/Sai dùng ở cấp 2 (chưa có hình nên chưa dùng được ở cấp 1 cho tới
    /// khi người tạo thêm hình). Các cột <c>attempt_answers.parts_total</c>, <c>parts_correct</c> và bảng <c>attempt_answer_parts</c> của v4
    /// thuộc luồng làm bài nên chưa thêm ở đây.
    /// </summary>
    public partial class ThemDangCauHoiVaCapPhuHop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_questions_question_type",
                table: "questions");

            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "questions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "level_max",
                table: "questions",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)3);

            migrationBuilder.AddColumn<byte>(
                name: "level_min",
                table: "questions",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<string>(
                name: "passage",
                table: "questions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "passage_source",
                table: "questions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "blank_index",
                table: "answers",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "correct_position",
                table: "answers",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "answers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "match_content",
                table: "answers",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.Sql("UPDATE questions SET level_min = 2, level_max = 3 WHERE question_type = 'SINGLE_CHOICE'");
            migrationBuilder.Sql("UPDATE questions SET level_min = 2, level_max = 2 WHERE question_type = 'TRUE_FALSE'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_questions_levels",
                table: "questions",
                sql: "[level_min] BETWEEN 1 AND 3 AND [level_max] BETWEEN 1 AND 3 AND [level_min] <= [level_max]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_questions_passage",
                table: "questions",
                sql: "[question_type] <> 'SOURCE_READING' OR [passage] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_questions_question_type",
                table: "questions",
                sql: "[question_type] IN ('SINGLE_CHOICE', 'TRUE_FALSE', 'IMAGE_CHOICE', 'MATCHING', 'ORDERING', 'FILL_BLANK', 'MULTI_TRUE_FALSE', 'SOURCE_READING')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_answers_positions",
                table: "answers",
                sql: "([correct_position] IS NULL OR [correct_position] BETWEEN 1 AND 20) AND ([blank_index] IS NULL OR [blank_index] BETWEEN 1 AND 20)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_questions_levels",
                table: "questions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_questions_passage",
                table: "questions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_questions_question_type",
                table: "questions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_answers_positions",
                table: "answers");

            migrationBuilder.DropColumn(
                name: "image_url",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "level_max",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "level_min",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "passage",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "passage_source",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "blank_index",
                table: "answers");

            migrationBuilder.DropColumn(
                name: "correct_position",
                table: "answers");

            migrationBuilder.DropColumn(
                name: "image_url",
                table: "answers");

            migrationBuilder.DropColumn(
                name: "match_content",
                table: "answers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_questions_question_type",
                table: "questions",
                sql: "[question_type] IN ('SINGLE_CHOICE', 'TRUE_FALSE')");
        }
    }
}
