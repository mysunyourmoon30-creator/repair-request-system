using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>
/// Field validation for master-data commands (BR-04 server-side validation; 422 VALIDATION_FAILED).
/// Codes are stored as varchar(30), so only printable ASCII is accepted; the baseline defines no
/// stricter code format. Reasons follow the DEC-PS1-014 / BR-04 reason convention (nvarchar(1000)).
/// </summary>
public static class MasterDataValidation
{
    public static string? Code(string? value, string field, IDictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors[field] = ["The code is required."];
            return null;
        }

        if (trimmed.Length > MasterDataEntity.CodeMaxLength)
        {
            errors[field] = [$"The code must not exceed {MasterDataEntity.CodeMaxLength} characters."];
            return null;
        }

        if (trimmed.Any(character => character is < ' ' or > '~'))
        {
            errors[field] = ["The code may contain printable ASCII characters only."];
            return null;
        }

        return trimmed;
    }

    public static string? Reason(string? value, IDictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors[MasterDataFields.Reason] = ["A deactivation reason is required."];
            return null;
        }

        if (trimmed.Length > MasterDataEntity.DeactivateReasonMaxLength)
        {
            errors[MasterDataFields.Reason] = [$"The reason must not exceed {MasterDataEntity.DeactivateReasonMaxLength} characters."];
            return null;
        }

        return trimmed;
    }
}
