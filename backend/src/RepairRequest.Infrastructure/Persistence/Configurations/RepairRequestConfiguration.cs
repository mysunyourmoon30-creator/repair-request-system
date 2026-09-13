using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class RepairRequestConfiguration : IEntityTypeConfiguration<RepairRequestAggregate>
{
    private const int StatusCodeMaxLength = 30;

    public void Configure(EntityTypeBuilder<RepairRequestAggregate> builder)
    {
        builder.ToTable("repair_request", table =>
        {
            table.HasCheckConstraint("CK_repair_request_status", SqlCheck.In<RepairRequestStatus>("status"));

            // RR-DD-001 RR-020: preferred_end_at >= preferred_start_at.
            table.HasCheckConstraint(
                "CK_repair_request_preferred_window",
                "[preferred_start_at] IS NULL OR [preferred_end_at] IS NULL OR [preferred_end_at] >= [preferred_start_at]");

            // RR-DD-001 RR-006 / RR-007: Equipment and Location belong to the selected Site.
            table.HasCheckConstraint(
                "CK_repair_request_equipment_requires_site",
                "[equipment_id] IS NULL OR [site_id] IS NOT NULL");
            table.HasCheckConstraint(
                "CK_repair_request_location_requires_site",
                "[location_id] IS NULL OR [site_id] IS NOT NULL");
        });

        builder.HasKey(request => request.Id);

        builder.Property(request => request.Id).HasColumnName("repair_request_id");
        builder.Property(request => request.TenantId).HasColumnName("tenant_id");

        builder.Property(request => request.RequestNo)
            .HasColumnName("request_no")
            .HasMaxLength(RepairRequestAggregate.RequestNoMaxLength)
            .IsUnicode(false);

        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<RepairRequestStatus>>()
            .HasMaxLength(StatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(request => request.SiteId).HasColumnName("site_id");
        builder.Property(request => request.EquipmentId).HasColumnName("equipment_id");

        // Location and request contact masters are not Sprint 1 entities: stored
        // without a foreign key until their baseline tables exist.
        builder.Property(request => request.LocationId).HasColumnName("location_id");

        builder.Property(request => request.RequestCategoryCode)
            .HasColumnName("request_category_code")
            .HasMaxLength(RepairRequestAggregate.RequestCategoryCodeMaxLength)
            .IsUnicode(false);

        builder.Property(request => request.Description)
            .HasColumnName("description")
            .HasMaxLength(RepairRequestAggregate.DescriptionMaxLength);

        builder.Property(request => request.PriorityCode)
            .HasColumnName("priority_code")
            .HasMaxLength(RepairRequestAggregate.PriorityCodeMaxLength)
            .IsUnicode(false);

        builder.Property(request => request.DuplicateContinuationReason)
            .HasColumnName("duplicate_continuation_reason")
            .HasMaxLength(RepairRequestAggregate.ReasonMaxLength);

        builder.Property(request => request.CreatedBy).HasColumnName("created_by");
        builder.Property(request => request.SubmittedBy).HasColumnName("submitted_by");
        builder.Property(request => request.SubmittedAt).HasColumnName("submitted_at");

        builder.Property(request => request.CancelReason)
            .HasColumnName("cancel_reason")
            .HasMaxLength(RepairRequestAggregate.ReasonMaxLength);

        builder.Property(request => request.RejectReason)
            .HasColumnName("reject_reason")
            .HasMaxLength(RepairRequestAggregate.ReasonMaxLength);

        builder.Property(request => request.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        builder.Property(request => request.RequestContactId).HasColumnName("request_contact_id");
        builder.Property(request => request.PreferredStartAt).HasColumnName("preferred_start_at");
        builder.Property(request => request.PreferredEndAt).HasColumnName("preferred_end_at");

        // RR-DD-001 RR-003: unique once generated at SUBMITTED; DRAFT rows have no number yet.
        builder.HasIndex(request => request.RequestNo)
            .IsUnique()
            .HasFilter("[request_no] IS NOT NULL")
            .HasDatabaseName("UQ_repair_request_request_no");

        // RR-DBD-001 section 5 "Request List/Search": tenant_id + site_id + status + submitted_at,
        // including the summary columns required by RepairRequestSummary (RR-API-001 section 6).
        builder.HasIndex(request => new { request.TenantId, request.SiteId, request.Status, request.SubmittedAt })
            .IncludeProperties(request => new { request.RequestNo, request.RequestCategoryCode, request.PriorityCode })
            .HasDatabaseName("IX_repair_request_list_search");

        // RR-DBD-001 section 5 "Duplicate warning": tenant + site + category + submitted_at,
        // with equipment/location/status available for the BR-14 / D-12 residual predicate.
        builder.HasIndex(request => new { request.TenantId, request.SiteId, request.RequestCategoryCode, request.SubmittedAt })
            .IncludeProperties(request => new { request.EquipmentId, request.LocationId, request.Status })
            .HasDatabaseName("IX_repair_request_duplicate_check");

        // Site must belong to the request's Tenant (D-11 / BR-16).
        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(request => new { request.TenantId, request.SiteId })
            .HasPrincipalKey(site => new { site.TenantId, site.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_site");

        // Equipment must belong to the request's Site (RR-DD-001 RR-006).
        builder.HasOne<Equipment>()
            .WithMany()
            .HasForeignKey(request => new { request.SiteId, request.EquipmentId })
            .HasPrincipalKey(equipment => new { equipment.SiteId, equipment.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_equipment");

        // Requester (created_by) and submit actor must be users of the same Tenant.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(request => new { request.TenantId, request.CreatedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_created_by_user");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(request => new { request.TenantId, request.SubmittedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_repair_request_submitted_by_user");
    }
}
