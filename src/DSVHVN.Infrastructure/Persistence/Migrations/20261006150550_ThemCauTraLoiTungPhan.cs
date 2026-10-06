using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemCauTraLoiTungPhan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "parts_correct",
                table: "attempt_answers",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "parts_total",
                table: "attempt_answers",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.CreateTable(
                name: "attempt_answer_parts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    attempt_answer_id = table.Column<long>(type: "bigint", nullable: false),
                    answer_id = table.Column<long>(type: "bigint", nullable: true),
                    blank_index = table.Column<byte>(type: "tinyint", nullable: true),
                    selected_answer_id = table.Column<long>(type: "bigint", nullable: true),
                    selected_position = table.Column<byte>(type: "tinyint", nullable: true),
                    selected_bool = table.Column<bool>(type: "bit", nullable: true),
                    response_text = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    is_correct = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attempt_answer_parts", x => x.id);
                    table.CheckConstraint("CK_attempt_answer_parts_target", "([answer_id] IS NOT NULL AND [blank_index] IS NULL) OR ([answer_id] IS NULL AND [blank_index] IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_attempt_answer_parts_answer_id",
                        column: x => x.answer_id,
                        principalTable: "answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attempt_answer_parts_attempt_answer_id",
                        column: x => x.attempt_answer_id,
                        principalTable: "attempt_answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attempt_answer_parts_selected_answer_id",
                        column: x => x.selected_answer_id,
                        principalTable: "answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_attempt_answers_parts",
                table: "attempt_answers",
                sql: "[parts_total] >= 1 AND [parts_correct] BETWEEN 0 AND [parts_total]");

            migrationBuilder.CreateIndex(
                name: "IX_attempt_answer_parts_answer_id",
                table: "attempt_answer_parts",
                column: "answer_id");

            migrationBuilder.CreateIndex(
                name: "IX_attempt_answer_parts_selected_answer_id",
                table: "attempt_answer_parts",
                column: "selected_answer_id");

            migrationBuilder.CreateIndex(
                name: "UX_attempt_answer_parts_answer",
                table: "attempt_answer_parts",
                columns: new[] { "attempt_answer_id", "answer_id" },
                unique: true,
                filter: "[answer_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_attempt_answer_parts_blank",
                table: "attempt_answer_parts",
                columns: new[] { "attempt_answer_id", "blank_index" },
                unique: true,
                filter: "[blank_index] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attempt_answer_parts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_attempt_answers_parts",
                table: "attempt_answers");

            migrationBuilder.DropColumn(
                name: "parts_correct",
                table: "attempt_answers");

            migrationBuilder.DropColumn(
                name: "parts_total",
                table: "attempt_answers");
        }
    }
}
