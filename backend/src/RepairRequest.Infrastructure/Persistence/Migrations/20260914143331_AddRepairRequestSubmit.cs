using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRepairRequestSubmit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_repair_request_request_no",
                table: "repair_request");

            migrationBuilder.CreateTable(
                name: "request_category",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_category_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_category", x => new { x.tenant_id, x.request_category_code });
                    table.CheckConstraint("CK_request_category_status", "[status] IN ('ACTIVE', 'INACTIVE')");
                });

            migrationBuilder.CreateTable(
                name: "request_no_counter",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_year = table.Column<short>(type: "smallint", nullable: false),
                    last_value = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_no_counter", x => new { x.tenant_id, x.request_year });
                    table.CheckConstraint("CK_request_no_counter_last_value", "[last_value] >= 0");
                    table.CheckConstraint("CK_request_no_counter_request_year", "[request_year] BETWEEN 1 AND 9999");
                });

            migrationBuilder.CreateTable(
                name: "request_priority",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    priority_code = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_priority", x => new { x.tenant_id, x.priority_code });
                    table.CheckConstraint("CK_request_priority_status", "[status] IN ('ACTIVE', 'INACTIVE')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_tenant_id_priority_code",
                table: "repair_request",
                columns: new[] { "tenant_id", "priority_code" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_tenant_id_request_category_code",
                table: "repair_request",
                columns: new[] { "tenant_id", "request_category_code" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_tenant_id_request_contact_id",
                table: "repair_request",
                columns: new[] { "tenant_id", "request_contact_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_repair_request_tenant_id_request_no",
                table: "repair_request",
                columns: new[] { "tenant_id", "request_no" },
                unique: true,
                filter: "[request_no] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_repair_request_contact_requires_site",
                table: "repair_request",
                sql: "[request_contact_id] IS NULL OR [site_id] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_repair_request_request_category",
                table: "repair_request",
                columns: new[] { "tenant_id", "request_category_code" },
                principalTable: "request_category",
                principalColumns: new[] { "tenant_id", "request_category_code" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_repair_request_request_contact_user",
                table: "repair_request",
                columns: new[] { "tenant_id", "request_contact_id" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_repair_request_request_priority",
                table: "repair_request",
                columns: new[] { "tenant_id", "priority_code" },
                principalTable: "request_priority",
                principalColumns: new[] { "tenant_id", "priority_code" },
                onDelete: ReferentialAction.Restrict);

            // DEC-PRE-S1-007-03: idempotent placeholder demo/reference lookups for every existing tenant (a tenant exists as
            // tenant_id on users). Literal values so this migration never changes when the runtime seeder data changes;
            // tenants created later are seeded by RequestLookupSeeder at API startup.
            migrationBuilder.Sql("""
                INSERT INTO [request_category] ([tenant_id], [request_category_code], [name], [status])
                SELECT [tenants].[TenantId], [seed].[code], [seed].[name], 'ACTIVE'
                FROM (SELECT DISTINCT [TenantId] FROM [AspNetUsers]) AS [tenants]
                CROSS JOIN (VALUES ('ELECTRICAL', N'Electrical'), ('MECHANICAL', N'Mechanical'), ('PLUMBING', N'Plumbing'),
                                   ('HVAC', N'HVAC'), ('IT', N'IT'), ('OTHER', N'Other')) AS [seed] ([code], [name])
                WHERE NOT EXISTS (
                    SELECT 1 FROM [request_category] AS [existing]
                    WHERE [existing].[tenant_id] = [tenants].[TenantId] AND [existing].[request_category_code] = [seed].[code]);
                """);

            migrationBuilder.Sql("""
                INSERT INTO [request_priority] ([tenant_id], [priority_code], [name], [status])
                SELECT [tenants].[TenantId], [seed].[code], [seed].[name], 'ACTIVE'
                FROM (SELECT DISTINCT [TenantId] FROM [AspNetUsers]) AS [tenants]
                CROSS JOIN (VALUES ('LOW', N'Low'), ('MEDIUM', N'Medium'), ('HIGH', N'High'), ('URGENT', N'Urgent')) AS [seed] ([code], [name])
                WHERE NOT EXISTS (
                    SELECT 1 FROM [request_priority] AS [existing]
                    WHERE [existing].[tenant_id] = [tenants].[TenantId] AND [existing].[priority_code] = [seed].[code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_repair_request_request_category",
                table: "repair_request");

            migrationBuilder.DropForeignKey(
                name: "FK_repair_request_request_contact_user",
                table: "repair_request");

            migrationBuilder.DropForeignKey(
                name: "FK_repair_request_request_priority",
                table: "repair_request");

            migrationBuilder.DropTable(
                name: "request_category");

            migrationBuilder.DropTable(
                name: "request_no_counter");

            migrationBuilder.DropTable(
                name: "request_priority");

            migrationBuilder.DropIndex(
                name: "IX_repair_request_tenant_id_priority_code",
                table: "repair_request");

            migrationBuilder.DropIndex(
                name: "IX_repair_request_tenant_id_request_category_code",
                table: "repair_request");

            migrationBuilder.DropIndex(
                name: "IX_repair_request_tenant_id_request_contact_id",
                table: "repair_request");

            migrationBuilder.DropIndex(
                name: "UQ_repair_request_tenant_id_request_no",
                table: "repair_request");

            migrationBuilder.DropCheckConstraint(
                name: "CK_repair_request_contact_requires_site",
                table: "repair_request");

            migrationBuilder.CreateIndex(
                name: "UQ_repair_request_request_no",
                table: "repair_request",
                column: "request_no",
                unique: true,
                filter: "[request_no] IS NOT NULL");
        }
    }
}
