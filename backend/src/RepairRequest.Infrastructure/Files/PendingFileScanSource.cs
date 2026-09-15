using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Files;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Files;

/// <summary>
/// PENDING file ids for the Development/Testing-only scan runner (DEC-PRE-S1-007-08). Registered only by that runner's
/// opt-in registration, never by <c>AddInfrastructure</c>. The status filter has no dedicated index because this bounded
/// query only runs in Development/Testing.
/// </summary>
public sealed class PendingFileScanSource : IPendingFileScanSource
{
    private readonly RepairRequestDbContext _db;

    public PendingFileScanSource(RepairRequestDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Guid>> ListPendingFileIdsAsync(int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        return await _db.FileAssets
            .AsNoTracking()
            .Where(file => file.MalwareScanStatus == MalwareScanStatus.Pending)
            .OrderBy(file => file.UploadedAt)
            .ThenBy(file => file.Id)
            .Select(file => file.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
    }
}
