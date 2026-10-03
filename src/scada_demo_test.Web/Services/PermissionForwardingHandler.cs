using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace scada_demo_test.Web.Services;

// Attached to the ScadaDemoTestApiClient's HttpClient pipeline (see Program.cs).
// Reads the signed-in user's role and JWT access token off the current circuit's
// AuthenticationStateProvider or HttpContext (with fallback to the latest active
// session token for iframe environments) and forwards them to scada_demo_test.API.
public class PermissionForwardingHandler : DelegatingHandler
{
    public static string? FallbackAccessToken { get; set; }
    public static string? FallbackRole { get; set; }
    public static ClaimsPrincipal? FallbackPrincipal { get; set; }

    private readonly IHttpContextAccessor _accessor;
    private readonly IServiceProvider _serviceProvider;

    public PermissionForwardingHandler(IHttpContextAccessor accessor, IServiceProvider serviceProvider)
    {
        _accessor = accessor;
        _serviceProvider = serviceProvider;
        InnerHandler = new HttpClientHandler { AllowAutoRedirect = false };
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
                // Resolve directly from the circuit's IServiceProvider (never CreateScope,
                // which would create an uninitialized AuthenticationStateProvider).
                var authStateProvider = _serviceProvider.GetService<AuthenticationStateProvider>();
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
            catch
            {
                // Circuit not yet initialized; fall back below.
            }
        }

        role ??= FallbackRole;
        token ??= FallbackAccessToken;

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
