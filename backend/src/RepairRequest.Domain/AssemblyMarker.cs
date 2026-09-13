namespace RepairRequest.Domain;

/// <summary>
/// Non-functional anchor type used for assembly reflection/scanning (tests, DI
/// registration) so callers don't need to reference an arbitrary business type
/// that doesn't exist yet in Sprint 0.
/// </summary>
public sealed class AssemblyMarker
{
    private AssemblyMarker()
    {
    }
}
