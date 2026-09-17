using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace RepairRequest.ApiTests;

/// <summary>
/// Runs the API's configured JWT bearer handler directly against a token, so validation rules
/// can be verified without adding a protected business endpoint in S1-002.
/// </summary>
internal static class BearerAuthentication
{
    public static async Task<AuthenticateResult> AuthenticateAsync(IServiceProvider services, string? accessToken)
    {
        await using var scope = services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        if (accessToken is not null)
        {
            httpContext.Request.Headers.Authorization = $"Bearer {accessToken}";
        }

        var authentication = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        return await authentication.AuthenticateAsync(httpContext, JwtBearerDefaults.AuthenticationScheme);
    }
}
