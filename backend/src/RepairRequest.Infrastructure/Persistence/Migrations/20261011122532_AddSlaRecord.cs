using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_repair_request_tenant_id_repair_request_id",
                table: "repair_request",
                columns: new[] { "tenant_id", "repair_request_id" });

            migrationBuilder.CreateTable(
                name: "sla_record",
                columns: table => new
                {
                    sla_record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    repair_request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sla_policy_version = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    original_due_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    risk_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    stopped_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    stop_reason = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sla_record", x => x.sla_record_id);
                    table.CheckConstraint("CK_sla_record_due_after_start", "[original_due_at] = DATEADD(hour, 24, [started_at])");
                    table.CheckConstraint("CK_sla_record_risk_before_due", "[risk_at] = DATEADD(hour, -4, [original_due_at])");
                    table.CheckConstraint("CK_sla_record_stop_pair", "([stopped_at] IS NULL AND [stop_reason] IS NULL) OR ([stopped_at] IS NOT NULL AND [stop_reason] IS NOT NULL)");
                    table.CheckConstraint("CK_sla_record_stopped_after_start", "[stopped_at] IS NULL OR [stopped_at] >= [started_at]");
                    table.CheckConstraint("CK_sla_record_stop_reason", "[stop_reason] IS NULL OR [stop_reason] IN ('REQUEST_REJECTED', 'REQUEST_CANCELLED', 'WO_CLOSED', 'WO_CANCELLED')");
                    table.ForeignKey(
                        name: "FK_sla_record_repair_request",
                        columns: x => new { x.tenant_id, x.repair_request_id },
                        principalTable: "repair_request",
                        principalColumns: new[] { "tenant_id", "repair_request_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sla_record_tenant_id_repair_request_id",
                table: "sla_record",
                columns: new[] { "tenant_id", "repair_request_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_sla_record_repair_request_id",
                table: "sla_record",
                column: "repair_request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sla_record");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_repair_request_tenant_id_repair_request_id",
                table: "repair_request");
        }
    }
}
