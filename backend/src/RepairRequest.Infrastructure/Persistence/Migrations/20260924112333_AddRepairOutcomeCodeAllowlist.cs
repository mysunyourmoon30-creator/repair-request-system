using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRepairOutcomeCodeAllowlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_work_summary_repair_outcome_code",
                table: "work_summary",
                sql: "[repair_outcome_code] IN ('REPAIRED', 'TEMPORARY_FIX', 'PARTS_REQUIRED', 'NO_FAULT_FOUND', 'NOT_REPAIRABLE', 'FOLLOW_UP_REQUIRED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_work_summary_repair_outcome_code",
                table: "work_summary");
        }
    }
}
