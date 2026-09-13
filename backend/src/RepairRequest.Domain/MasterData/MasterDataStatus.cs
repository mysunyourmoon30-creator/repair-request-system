namespace RepairRequest.Domain.MasterData;

/// <summary>
/// Lifecycle of Customer / Site / Equipment master data (DEC-PS1-001/002/003).
/// Persisted as the codes ACTIVE / INACTIVE. No hard delete (DEC-PS1-013).
/// </summary>
public enum MasterDataStatus
{
    Active,
    Inactive
}
