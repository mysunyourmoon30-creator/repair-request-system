using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approval_route",
                columns: table => new
                {
                    approval_route_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_category_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_route", x => x.approval_route_id);
                    table.UniqueConstraint("AK_approval_route_tenant_id_approval_route_id", x => new { x.tenant_id, x.approval_route_id });
                    table.ForeignKey(
                        name: "FK_approval_route_request_category",
                        columns: x => new { x.tenant_id, x.request_category_code },
                        principalTable: "request_category",
                        principalColumns: new[] { "tenant_id", "request_category_code" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_approval_route_site",
                        columns: x => new { x.tenant_id, x.site_id },
                        principalTable: "site",
                        principalColumns: new[] { "tenant_id", "site_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approval_route_step",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    approval_route_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    step_no = table.Column<short>(type: "smallint", nullable: false),
                    approver_role_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    approver_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_route_step", x => new { x.tenant_id, x.approval_route_id, x.step_no });
                    table.CheckConstraint("CK_approval_route_step_approver_role_code", "[approver_role_code] = 'APPROVER'");
                    table.CheckConstraint("CK_approval_route_step_step_no", "[step_no] >= 1");
                    table.ForeignKey(
                        name: "FK_approval_route_step_approval_route",
                        columns: x => new { x.tenant_id, x.approval_route_id },
                        principalTable: "approval_route",
                        principalColumns: new[] { "tenant_id", "approval_route_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_approval_route_step_approver_user",
                        columns: x => new { x.tenant_id, x.approver_user_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repair_request_approval",
                columns: table => new
                {
                    approval_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    repair_request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    approval_route_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    approval_step_no = table.Column<short>(type: "smallint", nullable: false),
                    assigned_approver_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    decision_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    routed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    routing_failure_code = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repair_request_approval", x => x.approval_id);
                    table.CheckConstraint("CK_repair_request_approval_assignment", "[assigned_approver_id] IS NOT NULL OR [routing_failure_code] IS NOT NULL");
                    table.CheckConstraint("CK_repair_request_approval_routing_failure", "[routing_failure_code] IS NULL OR ([assigned_approver_id] IS NULL AND [status] = 'PENDING')");
                    table.CheckConstraint("CK_repair_request_approval_status", "[status] IN ('PENDING', 'APPROVED', 'REJECTED', 'RETURNED_FOR_CORRECTION')");
                    table.CheckConstraint("CK_repair_request_approval_step_no", "[approval_step_no] >= 1");
                    table.ForeignKey(
                        name: "FK_repair_request_approval_approval_route",
                        columns: x => new { x.tenant_id, x.approval_route_id },
                        principalTable: "approval_route",
                        principalColumns: new[] { "tenant_id", "approval_route_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repair_request_approval_approval_route_step",
                        columns: x => new { x.tenant_id, x.approval_route_id, x.approval_step_no },
                        principalTable: "approval_route_step",
                        principalColumns: new[] { "tenant_id", "approval_route_id", "step_no" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repair_request_approval_assigned_approver_user",
                        columns: x => new { x.tenant_id, x.assigned_approver_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repair_request_approval_repair_request",
                        column: x => x.repair_request_id,
                        principalTable: "repair_request",
                        principalColumn: "repair_request_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approval_route_tenant_id_site_id",
                table: "approval_route",
                columns: new[] { "tenant_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_approval_route_active_default",
                table: "approval_route",
                columns: new[] { "tenant_id", "request_category_code" },
                unique: true,
                filter: "[is_active] = 1 AND [site_id] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_approval_route_active_site",
                table: "approval_route",
                columns: new[] { "tenant_id", "request_category_code", "site_id" },
                unique: true,
                filter: "[is_active] = 1 AND [site_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_approval_route_step_tenant_id_approver_user_id",
                table: "approval_route_step",
                columns: new[] { "tenant_id", "approver_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_approval_tenant_id_approval_route_id_approval_step_no",
                table: "repair_request_approval",
                columns: new[] { "tenant_id", "approval_route_id", "approval_step_no" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_approval_tenant_id_assigned_approver_id",
                table: "repair_request_approval",
                columns: new[] { "tenant_id", "assigned_approver_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_repair_request_approval_request_step",
                table: "repair_request_approval",
                columns: new[] { "repair_request_id", "approval_step_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repair_request_approval");

            migrationBuilder.DropTable(
                name: "approval_route_step");

            migrationBuilder.DropTable(
                name: "approval_route");
        }
    }
}
