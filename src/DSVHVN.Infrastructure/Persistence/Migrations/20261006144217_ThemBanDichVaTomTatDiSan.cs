using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phần của CSDL v4 mà luồng "Đa ngôn ngữ, AI dịch và tóm tắt di sản" cần: năm bảng <c>heritage_translations</c> (đơn vị duyệt của bản dịch, tiếng Anh qua duyệt,
    /// tiếng Hàn, Trung, Nhật luôn là bản dịch máy), <c>timeline_translations</c>, <c>event_translations</c>, <c>media_translations</c> (xóa theo bản gốc: Cascade) và
    /// <c>heritage_summaries</c> (tóm tắt ngắn và chi tiết theo ngôn ngữ, tiếng Việt và tiếng Anh qua duyệt). Mỗi bảng không trùng (bản gốc, ngôn ngữ); CHECK trạng thái theo ngôn ngữ.
    /// <c>ai_generations</c> đã có <c>heritage_id</c> và hai loại tác vụ di sản từ migration trước nên không đổi.
    /// </summary>
    public partial class ThemBanDichVaTomTatDiSan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_translations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_translations", x => x.id);
                    table.CheckConstraint("CK_event_translations_language", "[language_code] <> 'vi'");
                    table.CheckConstraint("CK_event_translations_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.ForeignKey(
                        name: "fk_event_translations_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "heritage_summaries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    short_text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    detailed_text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    is_outdated = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    ai_generation_id = table.Column<long>(type: "bigint", nullable: true),
                    reviewed_by = table.Column<long>(type: "bigint", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heritage_summaries", x => x.id);
                    table.CheckConstraint("CK_heritage_summaries_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.CheckConstraint("CK_heritage_summaries_status", "([language_code] IN ('vi', 'en') AND [status] IN ('PENDING', 'APPROVED', 'REJECTED')) OR ([language_code] IN ('ko', 'zh', 'ja') AND [status] = 'MACHINE')");
                    table.ForeignKey(
                        name: "fk_heritage_summaries_ai_generation_id",
                        column: x => x.ai_generation_id,
                        principalTable: "ai_generations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_heritage_summaries_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_heritage_summaries_reviewed_by",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "heritage_translations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    short_description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    content = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    is_outdated = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    ai_generation_id = table.Column<long>(type: "bigint", nullable: true),
                    reviewed_by = table.Column<long>(type: "bigint", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heritage_translations", x => x.id);
                    table.CheckConstraint("CK_heritage_translations_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.CheckConstraint("CK_heritage_translations_status", "([language_code] = 'en' AND [status] IN ('PENDING', 'APPROVED', 'REJECTED')) OR ([language_code] IN ('ko', 'zh', 'ja') AND [status] = 'MACHINE')");
                    table.ForeignKey(
                        name: "fk_heritage_translations_ai_generation_id",
                        column: x => x.ai_generation_id,
                        principalTable: "ai_generations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_heritage_translations_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_heritage_translations_reviewed_by",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_translations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    media_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    caption = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_translations", x => x.id);
                    table.CheckConstraint("CK_media_translations_language", "[language_code] <> 'vi'");
                    table.CheckConstraint("CK_media_translations_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.ForeignKey(
                        name: "fk_media_translations_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timeline_translations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    timeline_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timeline_translations", x => x.id);
                    table.CheckConstraint("CK_timeline_translations_language", "[language_code] <> 'vi'");
                    table.CheckConstraint("CK_timeline_translations_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.ForeignKey(
                        name: "fk_timeline_translations_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_event_translations_event_language",
                table: "event_translations",
                columns: new[] { "event_id", "language_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_heritage_summaries_ai_generation_id",
                table: "heritage_summaries",
                column: "ai_generation_id");

            migrationBuilder.CreateIndex(
                name: "IX_heritage_summaries_reviewed_by",
                table: "heritage_summaries",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "UX_heritage_summaries_heritage_language",
                table: "heritage_summaries",
                columns: new[] { "heritage_id", "language_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_heritage_translations_ai_generation_id",
                table: "heritage_translations",
                column: "ai_generation_id");

            migrationBuilder.CreateIndex(
                name: "IX_heritage_translations_reviewed_by",
                table: "heritage_translations",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "UX_heritage_translations_heritage_language",
                table: "heritage_translations",
                columns: new[] { "heritage_id", "language_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_media_translations_media_language",
                table: "media_translations",
                columns: new[] { "media_id", "language_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_timeline_translations_timeline_language",
                table: "timeline_translations",
                columns: new[] { "timeline_id", "language_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_translations");

            migrationBuilder.DropTable(
                name: "heritage_summaries");

            migrationBuilder.DropTable(
                name: "heritage_translations");

            migrationBuilder.DropTable(
                name: "media_translations");

            migrationBuilder.DropTable(
                name: "timeline_translations");
        }
    }
}
