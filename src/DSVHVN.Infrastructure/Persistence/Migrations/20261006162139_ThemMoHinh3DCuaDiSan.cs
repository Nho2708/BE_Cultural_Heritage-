using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemMoHinh3DCuaDiSan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "heritage_models",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    file_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    file_format = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    poster_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    license_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    license_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    author = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    source_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    credit = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heritage_models", x => x.id);
                    table.CheckConstraint("CK_heritage_models_file_format", "[file_format] IN ('GLB')");
                    table.CheckConstraint("CK_heritage_models_file_size", "[file_size_bytes] > 0");
                    table.CheckConstraint("CK_heritage_models_status", "[status] IN ('DRAFT', 'PUBLISHED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "fk_heritage_models_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_heritage_models_heritage_status",
                table: "heritage_models",
                columns: new[] { "heritage_id", "status" });

            migrationBuilder.CreateIndex(
                name: "UX_heritage_models_visible",
                table: "heritage_models",
                column: "heritage_id",
                unique: true,
                filter: "[status] = 'PUBLISHED' AND [deleted_at] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "heritage_models");
        }
    }
}
