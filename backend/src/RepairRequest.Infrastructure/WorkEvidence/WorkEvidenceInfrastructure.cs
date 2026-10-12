using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkEvidence;

/// <summary>
/// Per-Work-Order evidence lock: an exclusive, transaction-owned application lock (`sp_getapplock`) taken by evidence upload and by Work
/// Summary Submit, so the two are serialized for one Work Order and never block other Work Orders. The wait is bounded below the default
/// SqlClient command timeout, so a controlled 409 always decides before an uncontrolled timeout could.
/// </summary>
internal static class WorkEvidenceLock
{
    public const int TimeoutMilliseconds = 5_000;

    /// <summary>Lock resource of one Work Order's evidence round. 42 characters (limit 255).</summary>
    public static string Resource(Guid workOrderId) => string.Create(CultureInfo.InvariantCulture, $"WO-EVIDENCE|{workOrderId:N}");

    public static async Task<bool> TryAcquireAsync(RepairRequestDbContext db, Guid workOrderId, CancellationToken cancellationToken)
    {
        var resource = Resource(workOrderId);
        var timeout = TimeoutMilliseconds;

        var results = await db.Database.SqlQuery<int>($"""
            DECLARE @lock_result int;
            EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = {timeout};
            SELECT @lock_result AS [Value];
            """).ToListAsync(cancellationToken);

        return results.Single() >= 0;
    }
}

/// <summary>
/// Resolves the caller's audience for one Work Order's evidence and Service Report from existing scope rules only (`docs/16` §4.1):
/// in-scope Team Lead/Supervisor (<see cref="IDataScope.WorkOrders"/>), Technician assigned to a Visit of it
/// (<see cref="IDataScope.AssignedServiceVisits"/>), or exactly the designated Acceptance Contact. Each check is one bounded EXISTS.
/// </summary>
internal static class ReportAudienceResolver
{
    public static async Task<ReportAudience> ResolveAsync(
        RepairRequestDbContext db, IDataScope scope, CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        if ((user.IsInRole(RoleCodes.TeamLead) || user.IsInRole(RoleCodes.Supervisor))
            && await scope.WorkOrders(user).AnyAsync(workOrder => workOrder.Id == workOrderId, cancellationToken))
        {
            return ReportAudience.TeamLeadSupervisor;
        }

        if (user.IsTechnician
            && await scope.AssignedServiceVisits(user).AnyAsync(visit => visit.WorkOrderId == workOrderId, cancellationToken))
        {
            return ReportAudience.Technician;
        }

        if (user.IsRequester)
        {
            var tenantId = user.TenantId;
            var userId = user.UserId;

            // Exactly the contact designated on the Work Order; being a Requester or Site Contact alone grants nothing.
            if (await db.WorkOrders.AnyAsync(
                    workOrder => workOrder.TenantId == tenantId && workOrder.Id == workOrderId && workOrder.AcceptanceContactId == userId,
                    cancellationToken))
            {
                return ReportAudience.AcceptanceContact;
            }
        }

        return ReportAudience.None;
    }
}
