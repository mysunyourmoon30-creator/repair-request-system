using Microsoft.EntityFrameworkCore;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;

namespace RepairRequest.ApiTests;

/// <summary>
/// Explicit test arrangement for the CLEAN evidence guard of Work Summary Submit (`docs/13` §4.28). Every test that expects a Submit to
/// succeed calls <see cref="CreateCleanWorkEvidenceForCurrentVisitAsync"/> and sends the returned ids as the request's <c>evidenceIds</c>;
/// nothing in the API host does this implicitly, and a raw Submit without evidence receives the real evidence validation response. The rows it
/// inserts are exactly what a real upload plus a CLEAN scan result leave behind (a CLEAN file asset and one evidence upload for the Service
/// Visit the Submit will describe: the Visit of the caller's latest own Work Session on the Work Order). The guard itself is never bypassed.
/// </summary>
internal static class WorkEvidenceArrangement
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    /// <summary>Inserts <paramref name="count"/> CLEAN evidence uploads for the Technician's current Visit and returns their ids in upload order.</summary>
    public static async Task<IReadOnlyList<Guid>> CreateCleanWorkEvidenceForCurrentVisitAsync(
        this AuthApiFactory factory,
        Guid workOrderId,
        Guid technicianUserId,
        int count = 1,
        WorkEvidenceType type = WorkEvidenceType.AfterWork)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        return await factory.WithDbAsync(async db =>
        {
            var visitId = await (
                from session in db.WorkSessions
                join visit in db.ServiceVisits on session.ServiceVisitId equals visit.Id
                where visit.WorkOrderId == workOrderId && session.TechnicianId == technicianUserId
                orderby session.CheckInAt descending
                select (Guid?)visit.Id)
                .FirstOrDefaultAsync();
            Assert.NotNull(visitId);

            var tenantId = await db.WorkOrders.Where(workOrder => workOrder.Id == workOrderId).Select(workOrder => workOrder.TenantId).SingleAsync();
            var now = DateTime.UtcNow;
            now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));

            var ids = new List<Guid>(count);
            for (var i = 0; i < count; i++)
            {
                var fileAsset = new FileAsset(
                    tenantId, $"arranged-evidence-{i + 1}.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", technicianUserId, now);
                fileAsset.MarkClean();
                db.FileAssets.Add(fileAsset);
                await db.SaveChangesAsync();

                var upload = WorkEvidenceUpload.Create(tenantId, workOrderId, visitId.Value, fileAsset.Id, type, now);
                db.WorkEvidenceUploads.Add(upload);
                await db.SaveChangesAsync();
                ids.Add(upload.Id);
            }

            return (IReadOnlyList<Guid>)ids;
        });
    }
}
