using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemLuotMoKhoaQuangCao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ad_unlocks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    guest_device_id = table.Column<long>(type: "bigint", nullable: false),
                    heritage_id = table.Column<long>(type: "bigint", nullable: false),
                    platform = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ad_network = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    transaction_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ad_unit_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    unlocked_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ad_unlocks", x => x.id);
                    table.CheckConstraint("CK_ad_unlocks_ad_network", "[ad_network] IN ('ADMOB', 'AD_MANAGER')");
                    table.CheckConstraint("CK_ad_unlocks_expiry", "[expires_at] > [unlocked_at]");
                    table.CheckConstraint("CK_ad_unlocks_network_platform", "([ad_network] = 'ADMOB' AND [platform] IN ('ANDROID', 'IOS')) OR ([ad_network] = 'AD_MANAGER' AND [platform] = 'WEB')");
                    table.CheckConstraint("CK_ad_unlocks_platform", "[platform] IN ('ANDROID', 'IOS', 'WEB')");
                    table.ForeignKey(
                        name: "fk_ad_unlocks_guest_device_id",
                        column: x => x.guest_device_id,
                        principalTable: "guest_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ad_unlocks_heritage_id",
                        column: x => x.heritage_id,
                        principalTable: "heritages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ad_unlocks_device_heritage",
                table: "ad_unlocks",
                columns: new[] { "guest_device_id", "heritage_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ad_unlocks_heritage_id",
                table: "ad_unlocks",
                column: "heritage_id");

            migrationBuilder.CreateIndex(
                name: "UX_ad_unlocks_network_transaction",
                table: "ad_unlocks",
                columns: new[] { "ad_network", "transaction_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ad_unlocks");
        }
    }
}
