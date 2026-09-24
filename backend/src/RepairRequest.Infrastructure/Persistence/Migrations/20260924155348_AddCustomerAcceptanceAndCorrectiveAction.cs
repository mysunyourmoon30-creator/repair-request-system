using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerAcceptanceAndCorrectiveAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_acceptance",
                columns: table => new
                {
                    acceptance_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    acceptance_round_no = table.Column<short>(type: "smallint", nullable: false),
                    acceptance_contact_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    decision = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    decision_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_acceptance", x => x.acceptance_id);
                    table.CheckConstraint("CK_customer_acceptance_decision", "[decision] IN ('ACCEPT', 'REJECT')");
                    table.ForeignKey(
                        name: "FK_customer_acceptance_contact_user",
                        columns: x => new { x.tenant_id, x.acceptance_contact_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_acceptance_work_order",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "work_order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "corrective_action",
                columns: table => new
                {
                    corrective_action_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    acceptance_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cycle_no = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    owner_team_lead_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    plan_text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    plan_file_asset_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    approved_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    approved_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    corrective_service_visit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corrective_action", x => x.corrective_action_id);
                    table.CheckConstraint("CK_corrective_action_status", "[status] IN ('DRAFT', 'PENDING_PLAN_APPROVAL', 'APPROVED')");
                    table.ForeignKey(
                        name: "FK_corrective_action_acceptance",
                        column: x => x.acceptance_id,
                        principalTable: "customer_acceptance",
                        principalColumn: "acceptance_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_corrective_action_approved_by_user",
                        columns: x => new { x.tenant_id, x.approved_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_corrective_action_corrective_service_visit",
                        column: x => x.corrective_service_visit_id,
                        principalTable: "service_visit",
                        principalColumn: "service_visit_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_corrective_action_owner_team_lead_user",
                        columns: x => new { x.tenant_id, x.owner_team_lead_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_corrective_action_work_order",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "work_order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_corrective_action_corrective_service_visit_id",
                table: "corrective_action",
                column: "corrective_service_visit_id");

            migrationBuilder.CreateIndex(
                name: "IX_corrective_action_tenant_id_approved_by",
                table: "corrective_action",
                columns: new[] { "tenant_id", "approved_by" });

            migrationBuilder.CreateIndex(
                name: "IX_corrective_action_tenant_id_owner_team_lead_id",
                table: "corrective_action",
                columns: new[] { "tenant_id", "owner_team_lead_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_corrective_action_acceptance_id",
                table: "corrective_action",
                column: "acceptance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_corrective_action_work_order_id_cycle_no",
                table: "corrective_action",
                columns: new[] { "work_order_id", "cycle_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_acceptance_tenant_id_acceptance_contact_id",
                table: "customer_acceptance",
                columns: new[] { "tenant_id", "acceptance_contact_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_customer_acceptance_work_order_id_round_no",
                table: "customer_acceptance",
                columns: new[] { "work_order_id", "acceptance_round_no" },
                unique: true);

            // Backfill (docs/13 §4.17 migration strategy): before this ticket, Accept (ST-WO-005) wrote no
            // customer_acceptance row — only work_order.acceptance_contact_id/status and a WORK_ORDER_ACCEPTED
            // audit_history row. Every field used below is read from data that already exists with certainty
            // (the Work Order's own designated contact, and the audit row's own occurred_at/action_code) — no
            // value is invented. acceptance_round_no is always 1: before this ticket the only reachable path out
            // of AWAITING_CUSTOMER_ACCEPTANCE was Accept, so no Work Order could have more than one round.
            migrationBuilder.Sql(
                """
                INSERT INTO [customer_acceptance] (acceptance_id, tenant_id, work_order_id, acceptance_round_no, acceptance_contact_id, decision, decision_reason, decided_at)
                SELECT NEWID(), wo.tenant_id, wo.work_order_id, 1, wo.acceptance_contact_id, 'ACCEPT', NULL, ah.occurred_at
                FROM [work_order] wo
                INNER JOIN [audit_history] ah ON ah.entity_type = 'WORK_ORDER' AND ah.entity_id = wo.work_order_id AND ah.action_code = 'WORK_ORDER_ACCEPTED'
                WHERE wo.status = 'COMPLETED'
                  AND wo.acceptance_contact_id IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM [customer_acceptance] ca WHERE ca.work_order_id = wo.work_order_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "corrective_action");

            migrationBuilder.DropTable(
                name: "customer_acceptance");
        }
    }
}
