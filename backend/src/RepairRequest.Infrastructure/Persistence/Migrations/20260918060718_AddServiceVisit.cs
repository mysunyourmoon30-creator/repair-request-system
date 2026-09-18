using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceVisit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_visit",
                columns: table => new
                {
                    service_visit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    visit_type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    assigned_team_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    assigned_technician_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    scheduled_start_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    scheduled_end_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    reschedule_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    reassign_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    cancel_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    missed_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    source_missed_visit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    missed_decision_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    missed_decided_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_visit", x => x.service_visit_id);
                    table.CheckConstraint("CK_service_visit_missed_decision_code", "[missed_decision_code] IS NULL OR [missed_decision_code] IN ('RESCHEDULE', 'FOLLOW_UP', 'REASSIGN', 'NO_FOLLOW_UP')");
                    table.CheckConstraint("CK_service_visit_status", "[status] IN ('SCHEDULED', 'RESCHEDULED', 'IN_PROGRESS', 'COMPLETED', 'MISSED', 'CANCELLED')");
                    table.CheckConstraint("CK_service_visit_visit_type", "[visit_type] IN ('INITIAL', 'FOLLOW_UP', 'CORRECTIVE')");
                    table.ForeignKey(
                        name: "FK_service_visit_assigned_technician_user",
                        columns: x => new { x.tenant_id, x.assigned_technician_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_visit_source_missed_visit",
                        column: x => x.source_missed_visit_id,
                        principalTable: "service_visit",
                        principalColumn: "service_visit_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_visit_work_order",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "work_order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_visit_source_missed_visit_id",
                table: "service_visit",
                column: "source_missed_visit_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_visit_tenant_id_assigned_technician_id",
                table: "service_visit",
                columns: new[] { "tenant_id", "assigned_technician_id" });

            migrationBuilder.CreateIndex(
                name: "IX_service_visit_work_order_id",
                table: "service_visit",
                column: "work_order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_visit");
        }
    }
}
