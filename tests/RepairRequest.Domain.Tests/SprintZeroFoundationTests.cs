namespace RepairRequest.Domain.Tests;

/// <summary>
/// Sprint 0 placeholder: proves the Domain test project is wired to the Domain
/// assembly and runs under CI. Replace/extend with real invariant tests once
/// baseline entities are approved (RR-DBD-001 / RR-DD-001) and implemented.
/// </summary>
public class SprintZeroFoundationTests
{
    [Fact]
    public void DomainAssembly_IsLoadable()
    {
        var assembly = typeof(RepairRequest.Domain.AssemblyMarker).Assembly;

        Assert.NotNull(assembly);
    }
}
