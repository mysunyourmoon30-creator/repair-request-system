using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// Field and selection rules shared by Draft save (ST-RR-001, decision E2) and Submit (ST-RR-002): lookup code format and
/// the database checks of Site, Equipment, Category, Priority and request contact. Messages never reveal whether a
/// referenced user or code exists in another tenant.
/// </summary>
internal static class RepairRequestSelectionRules
{
    /// <summary>Trimmed code or null when absent/blank; format errors are added to <paramref name="errors"/>.</summary>
    public static string? Code(string? value, int maxLength, string field, IDictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            errors[field] = [$"The code must not exceed {maxLength} characters."];
            return null;
        }

        if (trimmed.Any(character => character is < ' ' or > '~'))
        {
            errors[field] = ["The code may contain printable ASCII characters only."];
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Applies the selection result. A requested Site outside business scope is handled by the caller (404) before this runs.
    /// Returns the canonical lookup codes to store.
    /// </summary>
    public static (string? CategoryCode, string? PriorityCode) Check(
        DraftSelectionQuery query,
        DraftSelection selection,
        IDictionary<string, string[]> errors)
    {
        if (query.SiteId is not null && selection.SiteStatus is { } siteStatus && siteStatus != MasterDataStatus.Active)
        {
            errors[RepairRequestFields.SiteId] = ["The Site is inactive."];
        }

        if (query.EquipmentId is not null)
        {
            if (selection.EquipmentStatus is null)
            {
                errors[RepairRequestFields.EquipmentId] = ["The Equipment does not belong to the selected Site."];
            }
            else if (selection.EquipmentStatus != MasterDataStatus.Active)
            {
                errors[RepairRequestFields.EquipmentId] = ["The Equipment is inactive."];
            }
        }

        CheckLookup(query.RequestCategoryCode, selection.Category, RepairRequestFields.RequestCategoryCode, "Category", errors);
        CheckLookup(query.PriorityCode, selection.Priority, RepairRequestFields.PriorityCode, "Priority", errors);

        if (query.RequestContactId is not null && !selection.ContactEligible)
        {
            errors[RepairRequestFields.RequestContactId] = ["The request contact must be a user assigned to the selected Site."];
        }

        return (selection.Category?.Code, selection.Priority?.Code);
    }

    private static void CheckLookup(string? requested, LookupSelection? found, string field, string label, IDictionary<string, string[]> errors)
    {
        if (requested is null)
        {
            return;
        }

        if (found is null)
        {
            errors[field] = [$"The {label} is not valid."];
        }
        else if (found.Status != MasterDataStatus.Active)
        {
            errors[field] = [$"The {label} is inactive."];
        }
    }
}
