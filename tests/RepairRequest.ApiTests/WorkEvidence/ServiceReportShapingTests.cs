using System.Reflection;
using System.Text.Json;
using RepairRequest.Api.Contracts.WorkEvidence;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.ApiTests.WorkEvidence;

/// <summary>
/// Actor-specific response shaping of the Service Report (`docs/16` OD5, §4.2), proven without a database: even when the application result
/// carries a cost section (which the store never produces for these audiences), the response type chosen for the Technician and the
/// Acceptance Contact has no cost member, so no cost name or value can reach the serialized JSON.
/// </summary>
public class ServiceReportShapingTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static ServiceReportResult Result(ReportAudience audience, ServiceReportCostDto? cost) =>
        new(
            audience,
            new ServiceReportDto(
                new ServiceReportHeaderDto(Id, "WO-1", WorkOrderStatus.InProgress, Now, null, Guid.NewGuid(), "RR-2026-000001", "CUST", "SITE", null),
                [],
                [],
                []),
            cost);

    private static readonly ServiceReportCostDto Cost = new(4821.37m, "THB", "COST-NOTE-SENTINEL", Guid.NewGuid(), Now, Guid.NewGuid(), Now);

    [Theory]
    [InlineData(ReportAudience.Technician)]
    [InlineData(ReportAudience.AcceptanceContact)]
    public void NonCostAudiences_AreMappedToATypeWithNoCostMember_EvenIfTheResultCarriesCost(ReportAudience audience)
    {
        var response = ServiceReportResponses.ToResponse(Result(audience, Cost));

        Assert.IsType<ServiceReportResponse>(response);
        var json = JsonSerializer.Serialize(response, response.GetType());
        Assert.DoesNotContain("cost", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4821.37", json, StringComparison.Ordinal);
        Assert.DoesNotContain("THB", json, StringComparison.Ordinal);
        Assert.DoesNotContain("COST-NOTE-SENTINEL", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Cost.PreparedBy.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TeamLeadSupervisor_IsMappedToTheFullType_WithTheCostSectionOrNullWhenNoneExists()
    {
        var withCost = Assert.IsType<ServiceReportFullResponse>(ServiceReportResponses.ToResponse(Result(ReportAudience.TeamLeadSupervisor, Cost)));
        Assert.Equal(4821.37m, withCost.CostSummary!.TotalAmount);

        var without = Assert.IsType<ServiceReportFullResponse>(ServiceReportResponses.ToResponse(Result(ReportAudience.TeamLeadSupervisor, null)));
        Assert.Null(without.CostSummary);
    }

    [Fact]
    public void TheCostFreeResponseTypeAndItsNestedTypes_DeclareNoCostRelatedMemberAtAnyDepth()
    {
        var visited = new HashSet<Type>();
        var members = new List<string>();
        Collect(typeof(ServiceReportResponse), visited, members);

        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, name =>
            name.Contains("cost", StringComparison.OrdinalIgnoreCase)
            || name.Contains("amount", StringComparison.OrdinalIgnoreCase)
            || name.Contains("currency", StringComparison.OrdinalIgnoreCase)
            || name.Contains("price", StringComparison.OrdinalIgnoreCase)
            || name.Contains("prepared", StringComparison.OrdinalIgnoreCase)
            || name.Contains("reviewed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(ServiceReportCostResponse), visited);
        Assert.DoesNotContain(typeof(ServiceReportFullResponse), visited);
    }

    [Fact]
    public void TheCostFreeResponseType_IsNotRelatedToTheFullType()
    {
        Assert.False(typeof(ServiceReportFullResponse).IsAssignableFrom(typeof(ServiceReportResponse)));
        Assert.False(typeof(ServiceReportResponse).IsAssignableFrom(typeof(ServiceReportFullResponse)));
        Assert.Equal(typeof(object), typeof(ServiceReportResponse).BaseType);
    }

    private static void Collect(Type type, HashSet<Type> visited, List<string> members)
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.DeclaringType != type)
            {
                continue;
            }

            members.Add(property.Name);
            var propertyType = property.PropertyType;
            var element = propertyType.IsGenericType ? propertyType.GetGenericArguments()[0] : propertyType;
            if (element.Namespace == typeof(ServiceReportResponse).Namespace)
            {
                Collect(element, visited, members);
            }
        }
    }
}
