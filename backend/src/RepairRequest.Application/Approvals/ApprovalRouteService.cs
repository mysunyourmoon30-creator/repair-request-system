using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Approval route configuration by ADMINISTRATOR (DEC-PRE-S1-007R-04/05/06). Role capability (MasterData.Manage) is
/// enforced by the API policy; every lookup is tenant-scoped. Rules: the Category must be an ACTIVE code of the tenant; a
/// given Site must be an ACTIVE Site of the tenant; the approver role must be APPROVER; a specific approver must be an
/// APPROVER of the tenant; at most one ACTIVE route per tenant + Category + Site (or tenant default). Configuration never
/// grants Approve/Reject.
/// </summary>
public sealed class ApprovalRouteService
{
    private readonly IApprovalRouteStore _store;
    private readonly TimeProvider _clock;

    public ApprovalRouteService(IApprovalRouteStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<PagedResult<ApprovalRouteDto>> ListAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken) =>
        _store.ListAsync(user, query, cancellationToken);

    public Task<ApprovalRouteDto?> GetAsync(CurrentUser user, Guid approvalRouteId, CancellationToken cancellationToken) =>
        _store.GetAsync(user, approvalRouteId, cancellationToken);

    public async Task<CommandResult<ApprovalRouteDto>> CreateAsync(CommandContext context, ApprovalRouteFields fields, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var category = RepairRequestSelectionRules.Code(
            fields.RequestCategoryCode, ApprovalRoute.CategoryCodeMaxLength, ApprovalRouteFieldNames.RequestCategoryCode, errors);
        if (category is null && !errors.ContainsKey(ApprovalRouteFieldNames.RequestCategoryCode))
        {
            errors[ApprovalRouteFieldNames.RequestCategoryCode] = ["A Category is required."];
        }

        if (fields.SiteId == Guid.Empty)
        {
            errors[ApprovalRouteFieldNames.SiteId] = ["The Site identifier is invalid."];
        }

        var roleCode = fields.ApproverRoleCode?.Trim();
        if (string.IsNullOrEmpty(roleCode))
        {
            errors[ApprovalRouteFieldNames.ApproverRoleCode] = ["An approver role is required."];
        }
        else if (!string.Equals(roleCode, RoleCodes.Approver, StringComparison.Ordinal))
        {
            errors[ApprovalRouteFieldNames.ApproverRoleCode] = [$"The approver role must be {RoleCodes.Approver}."];
        }

        if (fields.ApproverUserId == Guid.Empty)
        {
            errors[ApprovalRouteFieldNames.ApproverUserId] = ["The approver user identifier is invalid."];
        }

        if (errors.Count > 0)
        {
            return CommandError.Validation(errors);
        }

        // Tenant is always the caller's database-resolved tenant (D-11 / BR-16), never client input.
        var tenantId = context.User.TenantId;
        var references = await _store.GetReferencesAsync(context.User, category!, fields.SiteId, fields.ApproverUserId, cancellationToken);
        CheckReferences(references, fields.SiteId, fields.ApproverUserId, errors);
        if (errors.Count > 0)
        {
            return CommandError.Validation(errors);
        }

        var canonicalCategory = references.Category!.Code;
        if (await _store.OtherActiveRouteExistsAsync(tenantId, canonicalCategory, fields.SiteId, Guid.Empty, cancellationToken))
        {
            return DuplicateActiveRoute();
        }

        var route = ApprovalRoute.Create(tenantId, canonicalCategory, fields.SiteId);
        _store.Add(route);
        var step = ApprovalRouteStep.CreateFirstStep(route, RoleCodes.Approver, fields.ApproverUserId);
        _store.AddStep(step);
        _store.AddAudit(ApprovalRouteAudit.Created(context, route, step, UtcNow()));

        return await SaveAsync(route, step, null, cancellationToken);
    }

    public async Task<CommandResult<ApprovalRouteDto>> ActivateAsync(
        CommandContext context,
        Guid approvalRouteId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var route = await _store.FindAsync(context.User, approvalRouteId, cancellationToken);
        if (route is null)
        {
            return CommandError.NotFound;
        }

        if (!route.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (route.IsActive)
        {
            return CommandError.StateConflict("The approval route is already active.");
        }

        var step = await _store.FindFirstStepAsync(route.TenantId, route.Id, cancellationToken);
        if (step is null)
        {
            return CommandError.StateConflict("The approval route has no approver step.");
        }

        // Activation re-validates the references: inactive masters and non-APPROVER users are never selectable again.
        var errors = new Dictionary<string, string[]>();
        var references = await _store.GetReferencesAsync(context.User, route.RequestCategoryCode, route.SiteId, step.ApproverUserId, cancellationToken);
        CheckReferences(references, route.SiteId, step.ApproverUserId, errors);
        if (errors.Count > 0)
        {
            return CommandError.Validation(errors);
        }

        if (await _store.OtherActiveRouteExistsAsync(route.TenantId, route.RequestCategoryCode, route.SiteId, route.Id, cancellationToken))
        {
            return DuplicateActiveRoute();
        }

        route.Activate();
        _store.AddAudit(ApprovalRouteAudit.Activated(context, route, UtcNow()));

        return await SaveAsync(route, step, expectedRowVersion, cancellationToken);
    }

    public async Task<CommandResult<ApprovalRouteDto>> DeactivateAsync(
        CommandContext context,
        Guid approvalRouteId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var route = await _store.FindAsync(context.User, approvalRouteId, cancellationToken);
        if (route is null)
        {
            return CommandError.NotFound;
        }

        if (!route.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!route.IsActive)
        {
            return CommandError.StateConflict("The approval route is already inactive.");
        }

        var step = await _store.FindFirstStepAsync(route.TenantId, route.Id, cancellationToken);
        if (step is null)
        {
            return CommandError.StateConflict("The approval route has no approver step.");
        }

        route.Deactivate();
        _store.AddAudit(ApprovalRouteAudit.Deactivated(context, route, UtcNow()));

        return await SaveAsync(route, step, expectedRowVersion, cancellationToken);
    }

    private static void CheckReferences(ApprovalRouteReferences references, Guid? siteId, Guid? approverUserId, IDictionary<string, string[]> errors)
    {
        if (references.Category is null)
        {
            errors[ApprovalRouteFieldNames.RequestCategoryCode] = ["The Category is not valid."];
        }
        else if (references.Category.Status != MasterDataStatus.Active)
        {
            errors[ApprovalRouteFieldNames.RequestCategoryCode] = ["The Category is inactive."];
        }

        if (siteId is not null)
        {
            if (references.SiteStatus is null)
            {
                errors[ApprovalRouteFieldNames.SiteId] = ["The Site is not valid."];
            }
            else if (references.SiteStatus != MasterDataStatus.Active)
            {
                errors[ApprovalRouteFieldNames.SiteId] = ["The Site is inactive."];
            }
        }

        if (approverUserId is not null && !references.ApproverUserIsApprover)
        {
            errors[ApprovalRouteFieldNames.ApproverUserId] = [$"The approver user must hold the {RoleCodes.Approver} role in this tenant."];
        }
    }

    private static CommandError DuplicateActiveRoute() =>
        CommandError.Validation(
            ApprovalRouteFieldNames.RequestCategoryCode,
            "An active approval route already exists for this Category and Site.");

    private async Task<CommandResult<ApprovalRouteDto>> SaveAsync(
        ApprovalRoute route,
        ApprovalRouteStep step,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var outcome = await _store.SaveChangesAsync(route, expectedRowVersion, cancellationToken);

        return outcome switch
        {
            ApprovalRouteSaveOutcome.Saved => CommandResult<ApprovalRouteDto>.Success(ToDto(route, step)),
            ApprovalRouteSaveOutcome.DuplicateActiveRoute => DuplicateActiveRoute(),
            _ => CommandError.ConcurrencyConflict
        };
    }

    private static ApprovalRouteDto ToDto(ApprovalRoute route, ApprovalRouteStep step) =>
        new(
            route.Id,
            route.RequestCategoryCode,
            route.SiteId,
            route.IsActive ? MasterDataStatus.Active : MasterDataStatus.Inactive,
            step.StepNo,
            step.ApproverRoleCode,
            step.ApproverUserId,
            route.RowVersion);

    private DateTime UtcNow() => MasterDataCommandSteps.UtcNow(_clock);
}
