using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>
/// Seeds the approved role catalog (<see cref="RoleCodes"/>) through EF Core migrations.
/// Identifiers and concurrency stamps are fixed literals, so the seed is deterministic and
/// idempotent: re-running migrations never duplicates or rewrites roles. No users or
/// credentials are seeded.
/// </summary>
internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    internal static readonly IReadOnlyList<(string Code, Guid Id, string ConcurrencyStamp)> Seed =
    [
        (RoleCodes.Requester, new Guid("a582c130-e149-44a5-a656-0546180f57df"), "175eb74e-d0c8-49ff-af60-2a3e583ec5f9"),
        (RoleCodes.Approver, new Guid("a622c08a-dda2-4f2f-beca-9ef955badf5d"), "251cd2b6-b672-4b61-881d-4d6030e2680c"),
        (RoleCodes.Coordinator, new Guid("9f243ea7-6b1b-4b3e-944d-04fdfe0eebea"), "f3bbbaf9-c570-470f-8479-bc252e92ff01"),
        (RoleCodes.Technician, new Guid("b997f2eb-3afe-4aef-8e56-f9af82addcdb"), "62bf1329-2ef9-4805-9e74-36aad21f3cce"),
        (RoleCodes.TeamLead, new Guid("9ee9b352-1949-449c-9300-f7d4b01a21a5"), "cace5479-410b-4bf9-9bb7-ab5feb52753d"),
        (RoleCodes.Supervisor, new Guid("3c9a0af9-4bb1-4a64-8782-6968282e3c36"), "e95d41d6-cf75-4568-a5c4-c5da78db8ad0"),
        (RoleCodes.Administrator, new Guid("e6164ab9-892c-4f25-ae24-02b6a2450558"), "a38e6701-cd90-4a02-9910-b826198d4662"),
    ];

    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.HasData(Seed.Select(role => new ApplicationRole
        {
            Id = role.Id,
            Name = role.Code,
            NormalizedName = role.Code,
            ConcurrencyStamp = role.ConcurrencyStamp
        }));
    }
}
