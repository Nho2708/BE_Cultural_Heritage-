using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSVHVN.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemRangBuocGoiVaThanhToan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscriptions_organization_id",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_payments_invoice_id",
                table: "payments");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_organization_status",
                table: "subscriptions",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "UX_subscriptions_one_pending",
                table: "subscriptions",
                column: "organization_id",
                unique: true,
                filter: "[status] = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "UX_plans_name",
                table: "plans",
                column: "name",
                unique: true,
                filter: "[name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payments_status_created_at",
                table: "payments",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "UX_payments_one_pending_per_invoice",
                table: "payments",
                column: "invoice_id",
                unique: true,
                filter: "[status] = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_status_due_at",
                table: "invoices",
                columns: new[] { "status", "due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscriptions_organization_status",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "UX_subscriptions_one_pending",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "UX_plans_name",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "IX_payments_status_created_at",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "UX_payments_one_pending_per_invoice",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_invoices_status_due_at",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_organization_id",
                table: "subscriptions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_invoice_id",
                table: "payments",
                column: "invoice_id");
        }
    }
}
