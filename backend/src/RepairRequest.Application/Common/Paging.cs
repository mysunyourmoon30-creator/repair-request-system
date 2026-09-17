namespace RepairRequest.Application.Common;

/// <summary>A bounded page request. Values are validated and clamped by the API before reaching the Application layer.</summary>
public sealed record PageRequest(int Page, int PageSize);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
