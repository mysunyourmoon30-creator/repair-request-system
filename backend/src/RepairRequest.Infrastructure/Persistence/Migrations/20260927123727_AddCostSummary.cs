using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCostSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cost_summary",
                columns: table => new
                {
                    cost_summary_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    total_amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    currency_code = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    prepared_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    prepared_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cost_summary", x => x.cost_summary_id);
                    table.ForeignKey(
                        name: "FK_cost_summary_prepared_by_user",
                        columns: x => new { x.tenant_id, x.prepared_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cost_summary_reviewed_by_user",
                        columns: x => new { x.tenant_id, x.reviewed_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cost_summary_work_order",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "work_order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cost_summary_tenant_id_prepared_by",
                table: "cost_summary",
                columns: new[] { "tenant_id", "prepared_by" });

            migrationBuilder.CreateIndex(
                name: "IX_cost_summary_tenant_id_reviewed_by",
                table: "cost_summary",
                columns: new[] { "tenant_id", "reviewed_by" });

            migrationBuilder.CreateIndex(
                name: "UQ_cost_summary_work_order_id",
                table: "cost_summary",
                column: "work_order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cost_summary");
        }
    }
}
