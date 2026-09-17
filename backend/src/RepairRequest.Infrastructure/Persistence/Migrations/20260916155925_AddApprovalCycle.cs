using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalCycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_repair_request_approval_request_step",
                table: "repair_request_approval");

            // Every existing approval row belongs to the first approval cycle (DEC-PRE-S1-010-01).
            migrationBuilder.AddColumn<short>(
                name: "approval_cycle_no",
                table: "repair_request_approval",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.CreateIndex(
                name: "UQ_repair_request_approval_request_step_cycle",
                table: "repair_request_approval",
                columns: new[] { "repair_request_id", "approval_step_no", "approval_cycle_no" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_repair_request_approval_cycle_no",
                table: "repair_request_approval",
                sql: "[approval_cycle_no] >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_repair_request_approval_request_step_cycle",
                table: "repair_request_approval");

            migrationBuilder.DropCheckConstraint(
                name: "CK_repair_request_approval_cycle_no",
                table: "repair_request_approval");

            migrationBuilder.DropColumn(
                name: "approval_cycle_no",
                table: "repair_request_approval");

            migrationBuilder.CreateIndex(
                name: "UQ_repair_request_approval_request_step",
                table: "repair_request_approval",
                columns: new[] { "repair_request_id", "approval_step_no" },
                unique: true);
        }
    }
}
