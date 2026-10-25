using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemBoNhoDemGiongDoc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "speech_clips",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    text_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    voice = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    storage_key = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: false),
                    duration_ms = table.Column<int>(type: "int", nullable: false),
                    byte_size = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_speech_clips", x => x.id);
                    table.CheckConstraint("CK_speech_clips_byte_size", "[byte_size] > 0");
                    table.CheckConstraint("CK_speech_clips_duration", "[duration_ms] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "UX_speech_clips_hash_voice",
                table: "speech_clips",
                columns: new[] { "text_hash", "voice" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "speech_clips");
        }
    }
}
