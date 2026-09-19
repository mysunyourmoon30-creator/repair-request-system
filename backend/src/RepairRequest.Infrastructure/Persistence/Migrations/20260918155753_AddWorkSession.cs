using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_session",
                columns: table => new
                {
                    work_session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_visit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    technician_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    check_in_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    pause_start_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    resume_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    check_out_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_session", x => x.work_session_id);
                    table.CheckConstraint("CK_work_session_status", "[status] IN ('CHECKED_IN', 'PAUSED', 'CHECKED_OUT')");
                    table.ForeignKey(
                        name: "FK_work_session_service_visit",
                        column: x => x.service_visit_id,
                        principalTable: "service_visit",
                        principalColumn: "service_visit_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_session_technician_user",
                        columns: x => new { x.tenant_id, x.technician_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_session_service_visit_id",
                table: "work_session",
                column: "service_visit_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_session_tenant_id_technician_id_active",
                table: "work_session",
                columns: new[] { "tenant_id", "technician_id" },
                unique: true,
                filter: "[status] <> 'CHECKED_OUT'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_session");
        }
    }
}
