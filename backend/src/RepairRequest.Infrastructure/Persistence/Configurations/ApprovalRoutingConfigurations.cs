using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>
/// approval_route header (RR-DBD-001 section 2; RR-DD-001 ARC-001..004/008). Filtered unique indexes enforce at most one
/// ACTIVE route per tenant + Category + Site and per tenant + Category default (DEC-PRE-S1-007R-06); inactive history
/// may coexist. The same indexes serve the routing lookup.
/// </summary>
internal sealed class ApprovalRouteConfiguration : IEntityTypeConfiguration<ApprovalRoute>
{
    public void Configure(EntityTypeBuilder<ApprovalRoute> builder)
    {
        builder.ToTable("approval_route");

        builder.HasKey(route => route.Id);

        builder.Property(route => route.Id).HasColumnName("approval_route_id");
        builder.Property(route => route.TenantId).HasColumnName("tenant_id");

        builder.Property(route => route.RequestCategoryCode)
            .HasColumnName("request_category_code")
            .HasMaxLength(ApprovalRoute.CategoryCodeMaxLength)
            .IsUnicode(false);

        builder.Property(route => route.SiteId).HasColumnName("site_id");
        builder.Property(route => route.IsActive).HasColumnName("is_active");

        builder.Property(route => route.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        // Principal key for tenant-consistent foreign keys from approval_route_step and repair_request_approval.
        builder.HasAlternateKey(route => new { route.TenantId, route.Id })
            .HasName("AK_approval_route_tenant_id_approval_route_id");

        builder.HasIndex(route => new { route.TenantId, route.RequestCategoryCode, route.SiteId })
            .IsUnique()
            .HasFilter("[is_active] = 1 AND [site_id] IS NOT NULL")
            .HasDatabaseName("UQ_approval_route_active_site");

        builder.HasIndex(route => new { route.TenantId, route.RequestCategoryCode })
            .IsUnique()
            .HasFilter("[is_active] = 1 AND [site_id] IS NULL")
            .HasDatabaseName("UQ_approval_route_active_default");

        builder.HasOne<RequestCategory>()
            .WithMany()
            .HasForeignKey(route => new { route.TenantId, route.RequestCategoryCode })
            .HasPrincipalKey(category => new { category.TenantId, category.Code })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_approval_route_request_category");

        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(route => new { route.TenantId, route.SiteId })
            .HasPrincipalKey(site => new { site.TenantId, site.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_approval_route_site");
    }
}

/// <summary>
/// approval_route_step detail (RR-DD-001 ARC-005..007; ERD-DR-02 UNIQUE(route_id, step_no)). The primary key is
/// (tenant_id, approval_route_id, step_no): the tenant-composite route foreign key pins tenant_id to the route's tenant, so
/// UNIQUE(route, step) still holds, and the key also serves that foreign key without a separate index.
/// </summary>
internal sealed class ApprovalRouteStepConfiguration : IEntityTypeConfiguration<ApprovalRouteStep>
{
    public void Configure(EntityTypeBuilder<ApprovalRouteStep> builder)
    {
        builder.ToTable("approval_route_step", table =>
        {
            table.HasCheckConstraint("CK_approval_route_step_step_no", "[step_no] >= 1");

            // DEC-PRE-S1-007R-05: only the APPROVER role may be configured as a route approver.
            table.HasCheckConstraint("CK_approval_route_step_approver_role_code", $"[approver_role_code] = '{RoleCodes.Approver}'");
        });

        builder.HasKey(step => new { step.TenantId, step.ApprovalRouteId, step.StepNo });

        builder.Property(step => step.ApprovalRouteId).HasColumnName("approval_route_id");
        builder.Property(step => step.StepNo).HasColumnName("step_no");
        builder.Property(step => step.TenantId).HasColumnName("tenant_id");

        builder.Property(step => step.ApproverRoleCode)
            .HasColumnName("approver_role_code")
            .HasMaxLength(ApprovalRouteStep.RoleCodeMaxLength)
            .IsUnicode(false);

        builder.Property(step => step.ApproverUserId).HasColumnName("approver_user_id");

        builder.HasOne<ApprovalRoute>()
            .WithMany()
            .HasForeignKey(step => new { step.TenantId, step.ApprovalRouteId })
            .HasPrincipalKey(route => new { route.TenantId, route.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_approval_route_step_approval_route");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(step => new { step.TenantId, step.ApproverUserId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_approval_route_step_approver_user");
    }
}

/// <summary>
/// repair_request_approval (RR-DD-001 APR-001..011; RR-DBD-001 UNIQUE(request, step), amended to UNIQUE(request, step,
/// approval_cycle_no) by DEC-PRE-S1-010-01 so each resubmission gets a new cycle). A row is either assigned or carries
/// an approver-level routing failure (DEC-PRE-S1-007R-09); approval_route_id and approval_step_no are always required.
/// The RR-DBD-001 approval inbox index (assigned_approver_id, status, routed_at) is deferred to the approval inbox ticket:
/// no S1-007R query uses it. Foreign-key indexes are not duplicated: (tenant_id, approval_route_id, approval_step_no)
/// serves both the step and the route foreign keys.
/// </summary>
internal sealed class RepairRequestApprovalConfiguration : IEntityTypeConfiguration<RepairRequestApproval>
{
    public void Configure(EntityTypeBuilder<RepairRequestApproval> builder)
    {
        builder.ToTable("repair_request_approval", table =>
        {
            table.HasCheckConstraint("CK_repair_request_approval_status", SqlCheck.In<ApprovalStatus>("status"));
            table.HasCheckConstraint("CK_repair_request_approval_step_no", "[approval_step_no] >= 1");
            table.HasCheckConstraint("CK_repair_request_approval_cycle_no", "[approval_cycle_no] >= 1");
            table.HasCheckConstraint(
                "CK_repair_request_approval_assignment",
                "[assigned_approver_id] IS NOT NULL OR [routing_failure_code] IS NOT NULL");
            table.HasCheckConstraint(
                "CK_repair_request_approval_routing_failure",
                "[routing_failure_code] IS NULL OR " +
                $"([assigned_approver_id] IS NULL AND [status] = '{SqlCheck.Code(ApprovalStatus.Pending)}')");
        });

        builder.HasKey(approval => approval.Id);

        builder.Property(approval => approval.Id).HasColumnName("approval_id");
        builder.Property(approval => approval.TenantId).HasColumnName("tenant_id");
        builder.Property(approval => approval.RepairRequestId).HasColumnName("repair_request_id");
        builder.Property(approval => approval.ApprovalRouteId).HasColumnName("approval_route_id");
        builder.Property(approval => approval.ApprovalStepNo).HasColumnName("approval_step_no");
        builder.Property(approval => approval.ApprovalCycleNo).HasColumnName("approval_cycle_no");
        builder.Property(approval => approval.AssignedApproverId).HasColumnName("assigned_approver_id");

        builder.Property(approval => approval.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<ApprovalStatus>>()
            .HasMaxLength(MasterDataMapping.StatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(approval => approval.DecisionReason)
            .HasColumnName("decision_reason")
            .HasMaxLength(RepairRequestApproval.DecisionReasonMaxLength);

        builder.Property(approval => approval.RoutedAt).HasColumnName("routed_at");
        builder.Property(approval => approval.DecidedAt).HasColumnName("decided_at");

        builder.Property(approval => approval.RoutingFailureCode)
            .HasColumnName("routing_failure_code")
            .HasMaxLength(RoutingFailureCodes.MaxLength)
            .IsUnicode(false);

        builder.Property(approval => approval.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        // One row per request, step and approval cycle (DEC-PRE-S1-010-01). The key order also serves the "current cycle of
        // a step" lookup as one backward range seek.
        builder.HasIndex(approval => new { approval.RepairRequestId, approval.ApprovalStepNo, approval.ApprovalCycleNo })
            .IsUnique()
            .HasDatabaseName("UQ_repair_request_approval_request_step_cycle");

        builder.HasOne<RepairRequestAggregate>()
            .WithMany()
            .HasForeignKey(approval => approval.RepairRequestId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_approval_repair_request");

        builder.HasOne<ApprovalRoute>()
            .WithMany()
            .HasForeignKey(approval => new { approval.TenantId, approval.ApprovalRouteId })
            .HasPrincipalKey(route => new { route.TenantId, route.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_approval_approval_route");

        builder.HasOne<ApprovalRouteStep>()
            .WithMany()
            .HasForeignKey(approval => new { approval.TenantId, approval.ApprovalRouteId, approval.ApprovalStepNo })
            .HasPrincipalKey(step => new { step.TenantId, step.ApprovalRouteId, step.StepNo })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_approval_approval_route_step");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(approval => new { approval.TenantId, approval.AssignedApproverId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_approval_assigned_approver_user");
    }
}
