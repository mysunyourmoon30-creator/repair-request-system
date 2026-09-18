using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>
/// EF Core implementation of the Work Order read port (S2-001). Every read starts from
/// <see cref="IDataScope.WorkOrders"/>, so tenant/site scope is part of the same SQL statement. The originating
/// Repair Request is joined (WO-004 is a required, unique FK); Site/Customer/Equipment codes are resolved by
/// correlated projections. No navigation properties and no full aggregate materialization (RR-ARCH-001 section 15;
/// docs/09 section 9 "focused summary DTOs/projections").
/// </summary>
internal sealed class WorkOrderStore : IWorkOrderStore
{
    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public WorkOrderStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<PagedResult<WorkOrderDto>> ListAsync(CurrentUser user, WorkOrderListQuery query, CancellationToken cancellationToken)
    {
        var source = _scope.WorkOrders(user).AsNoTracking();

        if (query.Status is { } status)
        {
            source = source.Where(workOrder => workOrder.Status == status);
        }

        var paging = query.Paging;
        var totalCount = await source.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;

        IReadOnlyList<WorkOrderDto> items = skip >= totalCount
            ? []
            : await Project(source
                    // Newest-first: fixed sort for S2-001 (DEC-S2-001-04), no client-selectable sort.
                    .OrderByDescending(workOrder => workOrder.CreatedAt)
                    .ThenByDescending(workOrder => workOrder.Id)
                    .Skip((int)skip)
                    .Take(paging.PageSize))
                .ToListAsync(cancellationToken);

        return new PagedResult<WorkOrderDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    public Task<WorkOrderDto?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        Project(_scope.WorkOrders(user).AsNoTracking().Where(workOrder => workOrder.Id == workOrderId))
            .SingleOrDefaultAsync(cancellationToken);

    private IQueryable<WorkOrderDto> Project(IQueryable<WorkOrder> workOrders) =>
        from workOrder in workOrders
        join request in _db.RepairRequests on workOrder.RepairRequestId equals request.Id
        select new WorkOrderDto(
            workOrder.Id,
            workOrder.WorkOrderNo,
            workOrder.Status,
            workOrder.RepairRequestId,
            request.RequestNo,
            _db.Customers
                .Where(customer => customer.Id ==
                    _db.Sites.Where(site => site.Id == request.SiteId).Select(site => site.CustomerId).FirstOrDefault())
                .Select(customer => customer.CustomerCode)
                .FirstOrDefault(),
            _db.Sites.Where(site => site.Id == request.SiteId).Select(site => site.SiteCode).FirstOrDefault(),
            _db.Equipment.Where(item => item.Id == request.EquipmentId).Select(item => item.EquipmentCode).FirstOrDefault(),
            workOrder.RowVersion,
            _db.ServiceVisits
                .Where(visit => visit.WorkOrderId == workOrder.Id)
                .OrderByDescending(visit => visit.ScheduledStartAt)
                .ThenByDescending(visit => visit.Id)
                .Select(visit => new ServiceVisitDto(
                    visit.Id,
                    visit.WorkOrderId,
                    visit.VisitType,
                    visit.Status,
                    visit.AssignedTeamId,
                    visit.AssignedTechnicianId,
                    visit.ScheduledStartAt,
                    visit.ScheduledEndAt,
                    visit.RescheduleReason,
                    visit.ReassignReason,
                    visit.CancelReason,
                    visit.MissedReason,
                    visit.CompletedAt,
                    visit.SourceMissedVisitId,
                    visit.MissedDecisionCode,
                    visit.MissedDecidedAt,
                    visit.RowVersion))
                .ToList());
}
