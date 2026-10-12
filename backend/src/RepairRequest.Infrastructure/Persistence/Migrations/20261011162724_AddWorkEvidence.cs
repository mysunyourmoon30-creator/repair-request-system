using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_work_summary_round",
                table: "work_summary",
                columns: new[] { "work_summary_id", "tenant_id", "work_order_id", "service_visit_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_work_order_tenant_id_work_order_id",
                table: "work_order",
                columns: new[] { "tenant_id", "work_order_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_service_visit_work_order_id_service_visit_id",
                table: "service_visit",
                columns: new[] { "work_order_id", "service_visit_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_file_asset_tenant_id_file_asset_id",
                table: "file_asset",
                columns: new[] { "tenant_id", "file_asset_id" });

            migrationBuilder.CreateTable(
                name: "work_evidence_upload",
                columns: table => new
                {
                    work_evidence_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_visit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_asset_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    evidence_type_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    upload_seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_evidence_upload", x => x.work_evidence_id);
                    table.UniqueConstraint("AK_work_evidence_upload_snapshot", x => new { x.work_evidence_id, x.tenant_id, x.work_order_id, x.service_visit_id, x.file_asset_id, x.evidence_type_code });
                    table.CheckConstraint("CK_work_evidence_upload_evidence_type", "[evidence_type_code] IN ('BEFORE_WORK', 'AFTER_WORK', 'OTHER')");
                    table.ForeignKey(
                        name: "FK_work_evidence_upload_file_asset",
                        columns: x => new { x.tenant_id, x.file_asset_id },
                        principalTable: "file_asset",
                        principalColumns: new[] { "tenant_id", "file_asset_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_evidence_upload_service_visit",
                        columns: x => new { x.work_order_id, x.service_visit_id },
                        principalTable: "service_visit",
                        principalColumns: new[] { "work_order_id", "service_visit_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_evidence_upload_work_order",
                        columns: x => new { x.tenant_id, x.work_order_id },
                        principalTable: "work_order",
                        principalColumns: new[] { "tenant_id", "work_order_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_summary_evidence",
                columns: table => new
                {
                    evidence_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_visit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_summary_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    evidence_type_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    file_asset_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    slot_no = table.Column<byte>(type: "tinyint", nullable: false),
                    bound_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_summary_evidence", x => x.evidence_id);
                    table.CheckConstraint("CK_work_summary_evidence_evidence_type", "[evidence_type_code] IN ('BEFORE_WORK', 'AFTER_WORK', 'OTHER')");
                    table.CheckConstraint("CK_work_summary_evidence_slot_no", "[slot_no] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_work_summary_evidence_upload",
                        columns: x => new { x.evidence_id, x.tenant_id, x.work_order_id, x.service_visit_id, x.file_asset_id, x.evidence_type_code },
                        principalTable: "work_evidence_upload",
                        principalColumns: new[] { "work_evidence_id", "tenant_id", "work_order_id", "service_visit_id", "file_asset_id", "evidence_type_code" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_summary_evidence_work_summary",
                        columns: x => new { x.work_summary_id, x.tenant_id, x.work_order_id, x.service_visit_id },
                        principalTable: "work_summary",
                        principalColumns: new[] { "work_summary_id", "tenant_id", "work_order_id", "service_visit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_evidence_upload_tenant_id_file_asset_id",
                table: "work_evidence_upload",
                columns: new[] { "tenant_id", "file_asset_id" });

            migrationBuilder.CreateIndex(
                name: "IX_work_evidence_upload_tenant_id_work_order_id",
                table: "work_evidence_upload",
                columns: new[] { "tenant_id", "work_order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_work_evidence_upload_work_order_visit",
                table: "work_evidence_upload",
                columns: new[] { "work_order_id", "service_visit_id", "upload_seq" });

            migrationBuilder.CreateIndex(
                name: "UQ_work_evidence_upload_file_asset_id",
                table: "work_evidence_upload",
                column: "file_asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_summary_evidence_round",
                table: "work_summary_evidence",
                columns: new[] { "work_summary_id", "tenant_id", "work_order_id", "service_visit_id" });

            migrationBuilder.CreateIndex(
                name: "IX_work_summary_evidence_upload",
                table: "work_summary_evidence",
                columns: new[] { "evidence_id", "tenant_id", "work_order_id", "service_visit_id", "file_asset_id", "evidence_type_code" });

            migrationBuilder.CreateIndex(
                name: "UQ_work_summary_evidence_work_summary_id_slot_no",
                table: "work_summary_evidence",
                columns: new[] { "work_summary_id", "slot_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_summary_evidence");

            migrationBuilder.DropTable(
                name: "work_evidence_upload");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_work_summary_round",
                table: "work_summary");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_work_order_tenant_id_work_order_id",
                table: "work_order");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_service_visit_work_order_id_service_visit_id",
                table: "service_visit");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_file_asset_tenant_id_file_asset_id",
                table: "file_asset");
        }
    }
}
