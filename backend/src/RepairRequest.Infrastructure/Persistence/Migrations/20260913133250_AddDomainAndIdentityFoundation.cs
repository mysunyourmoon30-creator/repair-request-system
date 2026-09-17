using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepairRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainAndIdentityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                    table.UniqueConstraint("AK_AspNetUsers_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "audit_history",
                columns: table => new
                {
                    audit_history_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity_type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    action_code = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    from_state = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    to_state = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    old_value_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    new_value_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    actor_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    correlation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_history", x => x.audit_history_id);
                    table.CheckConstraint("CK_audit_history_new_value_json", "[new_value_json] IS NULL OR ISJSON([new_value_json]) = 1");
                    table.CheckConstraint("CK_audit_history_old_value_json", "[old_value_json] IS NULL OR ISJSON([old_value_json]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "customer",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    customer_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    deactivate_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer", x => x.customer_id);
                    table.UniqueConstraint("AK_customer_tenant_id_customer_id", x => new { x.tenant_id, x.customer_id });
                    table.CheckConstraint("CK_customer_deactivate_reason", "[status] <> 'INACTIVE' OR ([deactivate_reason] IS NOT NULL AND LEN(LTRIM(RTRIM([deactivate_reason]))) > 0)");
                    table.CheckConstraint("CK_customer_status", "[status] IN ('ACTIVE', 'INACTIVE')");
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_asset",
                columns: table => new
                {
                    file_asset_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    storage_reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    malware_scan_status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_asset", x => x.file_asset_id);
                    table.CheckConstraint("CK_file_asset_malware_scan_status", "[malware_scan_status] IN ('PENDING', 'CLEAN', 'FAILED')");
                    table.CheckConstraint("CK_file_asset_size_bytes", "[size_bytes] >= 0");
                    table.ForeignKey(
                        name: "FK_file_asset_uploaded_by_user",
                        columns: x => new { x.tenant_id, x.uploaded_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "site",
                columns: table => new
                {
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    customer_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    site_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    deactivate_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site", x => x.site_id);
                    table.UniqueConstraint("AK_site_tenant_id_site_id", x => new { x.tenant_id, x.site_id });
                    table.CheckConstraint("CK_site_deactivate_reason", "[status] <> 'INACTIVE' OR ([deactivate_reason] IS NOT NULL AND LEN(LTRIM(RTRIM([deactivate_reason]))) > 0)");
                    table.CheckConstraint("CK_site_status", "[status] IN ('ACTIVE', 'INACTIVE')");
                    table.ForeignKey(
                        name: "FK_site_customer",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalTable: "customer",
                        principalColumns: new[] { "tenant_id", "customer_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "equipment",
                columns: table => new
                {
                    equipment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    equipment_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    deactivate_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment", x => x.equipment_id);
                    table.UniqueConstraint("AK_equipment_site_id_equipment_id", x => new { x.site_id, x.equipment_id });
                    table.CheckConstraint("CK_equipment_deactivate_reason", "[status] <> 'INACTIVE' OR ([deactivate_reason] IS NOT NULL AND LEN(LTRIM(RTRIM([deactivate_reason]))) > 0)");
                    table.CheckConstraint("CK_equipment_status", "[status] IN ('ACTIVE', 'INACTIVE')");
                    table.ForeignKey(
                        name: "FK_equipment_site",
                        columns: x => new { x.tenant_id, x.site_id },
                        principalTable: "site",
                        principalColumns: new[] { "tenant_id", "site_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_site_scope",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_site_scope", x => new { x.tenant_id, x.user_id, x.site_id });
                    table.ForeignKey(
                        name: "FK_user_site_scope_site",
                        columns: x => new { x.tenant_id, x.site_id },
                        principalTable: "site",
                        principalColumns: new[] { "tenant_id", "site_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_site_scope_user",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repair_request",
                columns: table => new
                {
                    repair_request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_no = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    equipment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    location_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    request_category_code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    priority_code = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    duplicate_continuation_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    cancel_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    reject_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    request_contact_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    preferred_start_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    preferred_end_at = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repair_request", x => x.repair_request_id);
                    table.CheckConstraint("CK_repair_request_equipment_requires_site", "[equipment_id] IS NULL OR [site_id] IS NOT NULL");
                    table.CheckConstraint("CK_repair_request_location_requires_site", "[location_id] IS NULL OR [site_id] IS NOT NULL");
                    table.CheckConstraint("CK_repair_request_preferred_window", "[preferred_start_at] IS NULL OR [preferred_end_at] IS NULL OR [preferred_end_at] >= [preferred_start_at]");
                    table.CheckConstraint("CK_repair_request_status", "[status] IN ('DRAFT', 'SUBMITTED', 'UNDER_REVIEW', 'APPROVED', 'REJECTED', 'CANCELLED', 'CONVERTED')");
                    table.ForeignKey(
                        name: "FK_repair_request_created_by_user",
                        columns: x => new { x.tenant_id, x.created_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repair_request_equipment",
                        columns: x => new { x.site_id, x.equipment_id },
                        principalTable: "equipment",
                        principalColumns: new[] { "site_id", "equipment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repair_request_site",
                        columns: x => new { x.tenant_id, x.site_id },
                        principalTable: "site",
                        principalColumns: new[] { "tenant_id", "site_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repair_request_submitted_by_user",
                        columns: x => new { x.tenant_id, x.submitted_by },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "request_attachment",
                columns: table => new
                {
                    attachment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    repair_request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_asset_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    access_scope_code = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_attachment", x => x.attachment_id);
                    table.ForeignKey(
                        name: "FK_request_attachment_file_asset",
                        column: x => x.file_asset_id,
                        principalTable: "file_asset",
                        principalColumn: "file_asset_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_request_attachment_repair_request",
                        column: x => x.repair_request_id,
                        principalTable: "repair_request",
                        principalColumn: "repair_request_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_audit_history_timeline",
                table: "audit_history",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "occurred_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "UQ_customer_tenant_id_customer_code",
                table: "customer",
                columns: new[] { "tenant_id", "customer_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_equipment_tenant_id_site_id",
                table: "equipment",
                columns: new[] { "tenant_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_equipment_site_id_equipment_code",
                table: "equipment",
                columns: new[] { "site_id", "equipment_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_asset_tenant_id_uploaded_by",
                table: "file_asset",
                columns: new[] { "tenant_id", "uploaded_by" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_duplicate_check",
                table: "repair_request",
                columns: new[] { "tenant_id", "site_id", "request_category_code", "submitted_at" })
                .Annotation("SqlServer:Include", new[] { "equipment_id", "location_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_list_search",
                table: "repair_request",
                columns: new[] { "tenant_id", "site_id", "status", "submitted_at" })
                .Annotation("SqlServer:Include", new[] { "request_no", "request_category_code", "priority_code" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_site_id_equipment_id",
                table: "repair_request",
                columns: new[] { "site_id", "equipment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_tenant_id_created_by",
                table: "repair_request",
                columns: new[] { "tenant_id", "created_by" });

            migrationBuilder.CreateIndex(
                name: "IX_repair_request_tenant_id_submitted_by",
                table: "repair_request",
                columns: new[] { "tenant_id", "submitted_by" });

            migrationBuilder.CreateIndex(
                name: "UQ_repair_request_request_no",
                table: "repair_request",
                column: "request_no",
                unique: true,
                filter: "[request_no] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_request_attachment_file_asset_id",
                table: "request_attachment",
                column: "file_asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_request_attachment_repair_request_id",
                table: "request_attachment",
                column: "repair_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_site_tenant_id_customer_id",
                table: "site",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "UQ_site_customer_id_site_code",
                table: "site",
                columns: new[] { "customer_id", "site_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_site_scope_tenant_id_site_id",
                table: "user_site_scope",
                columns: new[] { "tenant_id", "site_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "audit_history");

            migrationBuilder.DropTable(
                name: "request_attachment");

            migrationBuilder.DropTable(
                name: "user_site_scope");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "file_asset");

            migrationBuilder.DropTable(
                name: "repair_request");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "equipment");

            migrationBuilder.DropTable(
                name: "site");

            migrationBuilder.DropTable(
                name: "customer");
        }
    }
}
