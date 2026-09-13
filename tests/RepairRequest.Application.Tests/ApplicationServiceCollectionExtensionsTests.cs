using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.DependencyInjection;

namespace RepairRequest.Application.Tests;

public class ApplicationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddApplication_ReturnsSameServiceCollection_ForFurtherChaining()
    {
        var services = new ServiceCollection();

        var result = services.AddApplication();

        Assert.Same(services, result);
    }
}
