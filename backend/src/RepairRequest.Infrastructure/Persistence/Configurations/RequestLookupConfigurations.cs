using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Infrastructure.Persistence.Conversions;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>Tenant-scoped Category lookup (DEC-PRE-S1-007-01): primary key (tenant_id, request_category_code).</summary>
internal sealed class RequestCategoryConfiguration : IEntityTypeConfiguration<RequestCategory>
{
    public void Configure(EntityTypeBuilder<RequestCategory> builder)
    {
        builder.ToTable("request_category", table =>
            table.HasCheckConstraint("CK_request_category_status", SqlCheck.In<MasterDataStatus>("status")));

        builder.HasKey(category => new { category.TenantId, category.Code });

        builder.Property(category => category.TenantId).HasColumnName("tenant_id");

        builder.Property(category => category.Code)
            .HasColumnName("request_category_code")
            .HasMaxLength(RequestCategory.CodeMaxLength)
            .IsUnicode(false);

        builder.Property(category => category.Name)
            .HasColumnName("name")
            .HasMaxLength(RequestCategory.NameMaxLength);

        builder.Property(category => category.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<MasterDataStatus>>()
            .HasMaxLength(MasterDataMapping.StatusCodeMaxLength)
            .IsUnicode(false);
    }
}

/// <summary>Tenant-scoped Priority lookup (DEC-PRE-S1-007-02): primary key (tenant_id, priority_code).</summary>
internal sealed class RequestPriorityConfiguration : IEntityTypeConfiguration<RequestPriority>
{
    public void Configure(EntityTypeBuilder<RequestPriority> builder)
    {
        builder.ToTable("request_priority", table =>
            table.HasCheckConstraint("CK_request_priority_status", SqlCheck.In<MasterDataStatus>("status")));

        builder.HasKey(priority => new { priority.TenantId, priority.Code });

        builder.Property(priority => priority.TenantId).HasColumnName("tenant_id");

        builder.Property(priority => priority.Code)
            .HasColumnName("priority_code")
            .HasMaxLength(RequestPriority.CodeMaxLength)
            .IsUnicode(false);

        builder.Property(priority => priority.Name)
            .HasColumnName("name")
            .HasMaxLength(RequestPriority.NameMaxLength);

        builder.Property(priority => priority.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<MasterDataStatus>>()
            .HasMaxLength(MasterDataMapping.StatusCodeMaxLength)
            .IsUnicode(false);
    }
}

/// <summary>Request No sequence per tenant and UTC year (DEC-PRE-S1-007-11): primary key (tenant_id, request_year).</summary>
internal sealed class RequestNumberCounterConfiguration : IEntityTypeConfiguration<RequestNumberCounter>
{
    public void Configure(EntityTypeBuilder<RequestNumberCounter> builder)
    {
        builder.ToTable("request_no_counter", table =>
        {
            table.HasCheckConstraint("CK_request_no_counter_last_value", "[last_value] >= 0");
            table.HasCheckConstraint("CK_request_no_counter_request_year", "[request_year] BETWEEN 1 AND 9999");
        });

        builder.HasKey(counter => new { counter.TenantId, counter.RequestYear });

        builder.Property(counter => counter.TenantId).HasColumnName("tenant_id");
        builder.Property(counter => counter.RequestYear).HasColumnName("request_year");
        builder.Property(counter => counter.LastValue).HasColumnName("last_value");
    }
}
