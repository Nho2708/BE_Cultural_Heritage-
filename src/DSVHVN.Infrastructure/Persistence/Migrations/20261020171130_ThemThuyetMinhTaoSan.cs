using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemThuyetMinhTaoSan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "heritage_narrations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    text_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    audio_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    duration_ms = table.Column<int>(type: "int", nullable: false),
                    sentences = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    voice = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heritage_narrations", x => x.id);
                    table.CheckConstraint("CK_heritage_narrations_duration", "[duration_ms] > 0");
                    table.CheckConstraint("CK_heritage_narrations_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.ForeignKey(
                        name: "fk_heritage_narrations_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_heritage_narrations_heritage_language",
                table: "heritage_narrations",
                columns: new[] { "heritage_id", "language_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "heritage_narrations");
        }
    }
}
