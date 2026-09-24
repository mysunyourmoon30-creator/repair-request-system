using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_summary",
                columns: table => new
                {
                    work_summary_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_visit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    revision_no = table.Column<int>(type: "int", nullable: false),
                    summary_text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    repair_outcome_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_summary", x => x.work_summary_id);
                    table.ForeignKey(
                        name: "FK_work_summary_service_visit",
                        column: x => x.service_visit_id,
                        principalTable: "service_visit",
                        principalColumn: "service_visit_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_summary_work_order",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "work_order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_summary_work_order_id",
                table: "work_summary",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "UQ_work_summary_service_visit_id_revision_no",
                table: "work_summary",
                columns: new[] { "service_visit_id", "revision_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_summary");
        }
    }
}
