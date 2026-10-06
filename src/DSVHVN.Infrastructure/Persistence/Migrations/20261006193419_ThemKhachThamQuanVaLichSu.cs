using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemKhachThamQuanVaLichSu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guest_devices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    device_key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    platform = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "vi"),
                    location_allowed = table.Column<bool>(type: "bit", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_devices", x => x.id);
                    table.CheckConstraint("CK_guest_devices_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.CheckConstraint("CK_guest_devices_platform", "[platform] IN ('ANDROID', 'IOS', 'WEB')");
                });

            migrationBuilder.CreateTable(
                name: "guest_activities",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    guest_device_id = table.Column<long>(type: "bigint", nullable: false),
                    activity_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    heritage_id = table.Column<long>(type: "bigint", nullable: true),
                    search_query = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    language_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_activities", x => x.id);
                    table.CheckConstraint("CK_guest_activities_activity_type", "[activity_type] IN ('SEARCH', 'VIEW', 'READ_SUMMARY', 'LISTEN', 'VIEW_3D')");
                    table.CheckConstraint("CK_guest_activities_language_code", "[language_code] IN ('vi', 'en', 'ko', 'zh', 'ja')");
                    table.CheckConstraint("CK_guest_activities_target", "([activity_type] = 'SEARCH' AND [search_query] IS NOT NULL) OR ([activity_type] <> 'SEARCH' AND [heritage_id] IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_guest_activities_guest_device_id",
                        column: x => x.guest_device_id,
                        principalTable: "guest_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_guest_activities_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guest_activities_device_time",
                table: "guest_activities",
                columns: new[] { "guest_device_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_guest_activities_heritage_id",
                table: "guest_activities",
                column: "heritage_id");

            migrationBuilder.CreateIndex(
                name: "UX_guest_devices_device_key",
                table: "guest_devices",
                column: "device_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guest_activities");

            migrationBuilder.DropTable(
                name: "guest_devices");
        }
    }
}
