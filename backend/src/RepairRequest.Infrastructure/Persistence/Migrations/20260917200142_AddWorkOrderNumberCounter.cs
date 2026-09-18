using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrderNumberCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_order_no_counter",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_order_year = table.Column<short>(type: "smallint", nullable: false),
                    last_value = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_no_counter", x => new { x.tenant_id, x.work_order_year });
                    table.CheckConstraint("CK_work_order_no_counter_last_value", "[last_value] >= 0");
                    table.CheckConstraint("CK_work_order_no_counter_work_order_year", "[work_order_year] BETWEEN 1 AND 9999");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_no_counter");
        }
    }
}
