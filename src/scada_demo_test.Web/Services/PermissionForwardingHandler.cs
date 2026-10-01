using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace scada_demo_test.Web.Services;

// Attached to the ScadaDemoTestApiClient's HttpClient pipeline (see Program.cs).
// Reads the signed-in user's role and JWT access token off the current HttpContext
// or AuthenticationState and forwards them so scada_demo_test.API can authorize all requests.
public class PermissionForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IServiceProvider _serviceProvider;

    public PermissionForwardingHandler(IHttpContextAccessor accessor, IServiceProvider serviceProvider)
    {
        _accessor = accessor;
        _serviceProvider = serviceProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? role = null;
        string? token = null;

        var user = _accessor.HttpContext?.User;
        if (user != null && user.Identity?.IsAuthenticated == true)
        {
            role = user.FindFirstValue(ClaimTypes.Role);
            token = user.FindFirstValue("access_token");
        }

        if (string.IsNullOrEmpty(token))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var authStateProvider = scope.ServiceProvider.GetService<AuthenticationStateProvider>();
                if (authStateProvider != null)
                {
                    var authState = await authStateProvider.GetAuthenticationStateAsync();
                    if (authState.User.Identity?.IsAuthenticated == true)
                    {
                        role ??= authState.User.FindFirstValue(ClaimTypes.Role);
                        token ??= authState.User.FindFirstValue("access_token");
                    }
                }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(role))
        {
            request.Headers.Remove("X-Requesting-Role");
            request.Headers.Add("X-Requesting-Role", role);
        }

        if (!string.IsNullOrEmpty(token) && request.Headers.Authorization == null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
