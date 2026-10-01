using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using scada_demo_test.Domain.Constants;
using scada_demo_test.Domain.Services;
using scada_demo_test.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddHttpContextAccessor();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options =>
{
    foreach (var tab in AppTabs.All)
    {
        options.AddPolicy($"Tab:{tab}", policy => policy.RequireAssertion(ctx =>
            ctx.User.HasClaim("IsSuperAdmin", "true") ||
            ctx.User.IsInRole("SuperAdmin") ||
            ctx.User.HasClaim("perm", tab)));
    }

    foreach (var action in AppPermissions.All)
    {
        options.AddPolicy($"Action:{action}", policy => policy.RequireAssertion(ctx =>
            ctx.User.HasClaim("IsSuperAdmin", "true") ||
            ctx.User.IsInRole("SuperAdmin") ||
            ctx.User.HasClaim("perm", action)));
    }
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<FirebaseScadaConfig>();
builder.Services.AddSingleton<FirebaseScadaService>();

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5080";
builder.Services.AddTransient<PermissionForwardingHandler>();
builder.Services.AddHttpClient<ScadaDemoTestApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
}).AddHttpMessageHandler<PermissionForwardingHandler>();

builder.Services.AddSingleton<ITelemetryBroadcastBus, TelemetryBroadcastBus>();
builder.Services.AddScoped<LiveTelemetryState>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// ---- Local Login Endpoint with JWT & RememberMe Support ----
app.MapPost("/account/login", async (HttpContext http, IFormCollection form, ScadaDemoTestApiClient api) =>
{
    var email = form["email"].ToString();
    var password = form["password"].ToString();
    var rememberMe = form["rememberMe"] == "on" || form["rememberMe"] == "true";

    var (success, data, error) = await api.LoginAsync(email, password, rememberMe);
    if (!success || data == null)
    {
        return Results.Redirect("/login?error=" + Uri.EscapeDataString(error ?? "Invalid email or password."));
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, data.UserId.ToString()),
        new(ClaimTypes.Name, $"{data.FirstName} {data.LastName}".Trim()),
        new(ClaimTypes.Email, data.Email),
        new(ClaimTypes.Role, data.RoleName),
        new("IsSuperAdmin", data.IsSuperAdmin ? "true" : "false"),
        new("access_token", data.AccessToken),
        new("refresh_token", data.RefreshToken)
    };

    claims.AddRange(data.Permissions.Select(p => new Claim("perm", p)));

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = rememberMe,
        ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(30) : null
    });

    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapGet("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
