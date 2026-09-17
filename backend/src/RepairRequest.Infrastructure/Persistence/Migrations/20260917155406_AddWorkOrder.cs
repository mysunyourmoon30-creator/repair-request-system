using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_order",
                columns: table => new
                {
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_no = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    repair_request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    owner_team_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    team_lead_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    acceptance_contact_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    acceptance_contact_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    cancel_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    closed_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order", x => x.work_order_id);
                    table.CheckConstraint("CK_work_order_status", "[status] IN ('OPEN', 'SCHEDULED', 'IN_PROGRESS', 'AWAITING_SUPERVISOR_REVIEW', 'AWAITING_CUSTOMER_ACCEPTANCE', 'COMPLETED', 'CORRECTIVE_ACTION_REQUIRED', 'CORRECTIVE_PLAN_PENDING', 'CORRECTIVE_PLAN_APPROVED', 'CLOSED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "FK_work_order_acceptance_contact_user",
                        columns: x => new { x.tenant_id, x.acceptance_contact_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_order_closed_by_user",
                        columns: x => new { x.tenant_id, x.closed_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_order_repair_request",
                        column: x => x.repair_request_id,
                        principalTable: "repair_request",
                        principalColumn: "repair_request_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_order_team_lead_user",
                        columns: x => new { x.tenant_id, x.team_lead_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_list_search",
                table: "work_order",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_tenant_id_acceptance_contact_id",
                table: "work_order",
                columns: new[] { "tenant_id", "acceptance_contact_id" });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_tenant_id_closed_by",
                table: "work_order",
                columns: new[] { "tenant_id", "closed_by" });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_tenant_id_team_lead_id",
                table: "work_order",
                columns: new[] { "tenant_id", "team_lead_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_work_order_repair_request_id",
                table: "work_order",
                column: "repair_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_work_order_tenant_id_work_order_no",
                table: "work_order",
                columns: new[] { "tenant_id", "work_order_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order");
        }
    }
}
