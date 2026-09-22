using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkSessionPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_session_pause",
                columns: table => new
                {
                    work_session_pause_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    paused_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    pause_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    resumed_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_session_pause", x => x.work_session_pause_id);
                    table.CheckConstraint("CK_work_session_pause_period", "[resumed_at] IS NULL OR [resumed_at] >= [paused_at]");
                    table.CheckConstraint("CK_work_session_pause_reason_not_blank", "LEN(LTRIM(RTRIM([pause_reason]))) > 0");
                    table.ForeignKey(
                        name: "FK_work_session_pause_work_session",
                        column: x => x.work_session_id,
                        principalTable: "work_session",
                        principalColumn: "work_session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_session_pause_open",
                table: "work_session_pause",
                column: "work_session_id",
                unique: true,
                filter: "[resumed_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_work_session_pause_work_session_id_paused_at",
                table: "work_session_pause",
                columns: new[] { "work_session_id", "paused_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_session_pause");
        }
    }
}
