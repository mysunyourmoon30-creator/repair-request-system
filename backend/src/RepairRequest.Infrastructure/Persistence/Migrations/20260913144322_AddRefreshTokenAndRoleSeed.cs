using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenAndRoleSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateTable(
                name: "refresh_token",
                columns: table => new
                {
                    refresh_token_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    family_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    revoked_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_token", x => x.refresh_token_id);
                    table.CheckConstraint("CK_refresh_token_expiry", "[expires_at] > [created_at]");
                    table.ForeignKey(
                        name: "FK_refresh_token_user",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("3c9a0af9-4bb1-4a64-8782-6968282e3c36"), "e95d41d6-cf75-4568-a5c4-c5da78db8ad0", "SUPERVISOR", "SUPERVISOR" },
                    { new Guid("9ee9b352-1949-449c-9300-f7d4b01a21a5"), "cace5479-410b-4bf9-9bb7-ab5feb52753d", "TEAM_LEAD", "TEAM_LEAD" },
                    { new Guid("9f243ea7-6b1b-4b3e-944d-04fdfe0eebea"), "f3bbbaf9-c570-470f-8479-bc252e92ff01", "COORDINATOR", "COORDINATOR" },
                    { new Guid("a582c130-e149-44a5-a656-0546180f57df"), "175eb74e-d0c8-49ff-af60-2a3e583ec5f9", "REQUESTER", "REQUESTER" },
                    { new Guid("a622c08a-dda2-4f2f-beca-9ef955badf5d"), "251cd2b6-b672-4b61-881d-4d6030e2680c", "APPROVER", "APPROVER" },
                    { new Guid("b997f2eb-3afe-4aef-8e56-f9af82addcdb"), "62bf1329-2ef9-4805-9e74-36aad21f3cce", "TECHNICIAN", "TECHNICIAN" },
                    { new Guid("e6164ab9-892c-4f25-ae24-02b6a2450558"), "a38e6701-cd90-4a02-9910-b826198d4662", "ADMINISTRATOR", "ADMINISTRATOR" }
                });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_token_family_id",
                table: "refresh_token",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_token_tenant_id_user_id",
                table: "refresh_token",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_refresh_token_token_hash",
                table: "refresh_token",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_token");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("3c9a0af9-4bb1-4a64-8782-6968282e3c36"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("9ee9b352-1949-449c-9300-f7d4b01a21a5"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("9f243ea7-6b1b-4b3e-944d-04fdfe0eebea"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a582c130-e149-44a5-a656-0546180f57df"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("a622c08a-dda2-4f2f-beca-9ef955badf5d"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("b997f2eb-3afe-4aef-8e56-f9af82addcdb"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("e6164ab9-892c-4f25-ae24-02b6a2450558"));

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");
        }
    }
}
