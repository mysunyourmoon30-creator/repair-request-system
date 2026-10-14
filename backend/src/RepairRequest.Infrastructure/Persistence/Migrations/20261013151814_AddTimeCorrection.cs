using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTimeCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_work_session_pause_tenant_id_work_session_id_pause_id",
                table: "work_session_pause",
                columns: new[] { "tenant_id", "work_session_id", "work_session_pause_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_work_session_tenant_id_work_session_id",
                table: "work_session",
                columns: new[] { "tenant_id", "work_session_id" });

            migrationBuilder.CreateTable(
                name: "time_correction",
                columns: table => new
                {
                    time_correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_session_pause_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    timestamp_type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    old_value = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    new_value = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    requested_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    decided_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    decision_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    sla_recalculated = table.Column<bool>(type: "bit", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_time_correction", x => x.time_correction_id);
                    table.CheckConstraint("CK_time_correction_decision_fields", "([status] = 'PENDING' AND [decided_by] IS NULL AND [decided_at] IS NULL AND [decision_reason] IS NULL) OR ([status] <> 'PENDING' AND [decided_by] IS NOT NULL AND [decided_at] IS NOT NULL)");
                    table.CheckConstraint("CK_time_correction_decision_reason_required_on_reject", "[status] <> 'REJECTED' OR ([decision_reason] IS NOT NULL AND LEN(LTRIM(RTRIM([decision_reason]))) > 0)");
                    table.CheckConstraint("CK_time_correction_no_self_decision", "[decided_by] IS NULL OR [decided_by] <> [requested_by]");
                    table.CheckConstraint("CK_time_correction_pause_target", "([timestamp_type] IN ('PAUSE_START', 'RESUME') AND [work_session_pause_id] IS NOT NULL) OR ([timestamp_type] IN ('CHECK_IN', 'CHECK_OUT') AND [work_session_pause_id] IS NULL)");
                    table.CheckConstraint("CK_time_correction_reason_not_blank", "LEN(LTRIM(RTRIM([reason]))) > 0");
                    table.CheckConstraint("CK_time_correction_sla_not_recalculated", "[sla_recalculated] = 0");
                    table.CheckConstraint("CK_time_correction_status", "[status] IN ('PENDING', 'APPROVED', 'REJECTED')");
                    table.CheckConstraint("CK_time_correction_timestamp_type", "[timestamp_type] IN ('CHECK_IN', 'PAUSE_START', 'RESUME', 'CHECK_OUT')");
                    table.ForeignKey(
                        name: "FK_time_correction_decided_by_user",
                        columns: x => new { x.tenant_id, x.decided_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_time_correction_requested_by_user",
                        columns: x => new { x.tenant_id, x.requested_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_time_correction_work_session",
                        columns: x => new { x.tenant_id, x.work_session_id },
                        principalTable: "work_session",
                        principalColumns: new[] { "tenant_id", "work_session_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_time_correction_work_session_pause",
                        columns: x => new { x.tenant_id, x.work_session_id, x.work_session_pause_id },
                        principalTable: "work_session_pause",
                        principalColumns: new[] { "tenant_id", "work_session_id", "work_session_pause_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_time_correction_tenant_id_decided_by",
                table: "time_correction",
                columns: new[] { "tenant_id", "decided_by" });

            migrationBuilder.CreateIndex(
                name: "IX_time_correction_tenant_id_requested_by",
                table: "time_correction",
                columns: new[] { "tenant_id", "requested_by" });

            migrationBuilder.CreateIndex(
                name: "IX_time_correction_tenant_id_status",
                table: "time_correction",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_time_correction_tenant_id_work_session_id_work_session_pause_id",
                table: "time_correction",
                columns: new[] { "tenant_id", "work_session_id", "work_session_pause_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_time_correction_pending_session_type",
                table: "time_correction",
                columns: new[] { "work_session_id", "timestamp_type" },
                unique: true,
                filter: "[status] = 'PENDING' AND [work_session_pause_id] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_time_correction_pending_session_type_pause",
                table: "time_correction",
                columns: new[] { "work_session_id", "timestamp_type", "work_session_pause_id" },
                unique: true,
                filter: "[status] = 'PENDING' AND [work_session_pause_id] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "time_correction");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_work_session_pause_tenant_id_work_session_id_pause_id",
                table: "work_session_pause");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_work_session_tenant_id_work_session_id",
                table: "work_session");
        }
    }
}
