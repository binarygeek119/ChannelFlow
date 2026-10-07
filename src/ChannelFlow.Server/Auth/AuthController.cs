using System.Security.Claims;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinTv.Auth;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly FinTvDbContext _db;
    private readonly PostgresConnectionStore _postgres;
    private readonly DatabaseInitializer _database;
    private readonly IServiceScopeFactory _scopes;

    public AuthController(
        FinTvDbContext db,
        PostgresConnectionStore postgres,
        DatabaseInitializer database,
        IServiceScopeFactory scopes)
    {
        _db = db;
        _postgres = postgres;
        _database = database;
        _scopes = scopes;
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> Status(CancellationToken cancellationToken)
    {
        if (!_postgres.IsConfigured)
        {
            return Ok(new
            {
                needsDatabase = true,
                needsSetup = true,
                authenticated = false,
                database = (object?)null
            });
        }

        try
        {
            var hasUser = await _db.AdminUsers.AnyAsync(cancellationToken);
            return Ok(new
            {
                needsDatabase = false,
                needsSetup = !hasUser,
                authenticated = User.Identity?.IsAuthenticated == true,
                userName = User.Identity?.Name,
                database = _postgres.GetPublicSettings(),
                databaseFromEnvironment = _postgres.FromEnvironment
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                needsDatabase = true,
                needsSetup = true,
                authenticated = false,
                database = _postgres.GetPublicSettings(),
                databaseError = "Could not connect to PostgreSQL. " + ex.Message
            });
        }
    }

    [HttpPost("database")]
    [AllowAnonymous]
    public async Task<IActionResult> SaveDatabase(
        [FromBody] PostgresSettings? request,
        CancellationToken cancellationToken)
    {
        if (_database.IsReady && await HasAdminAsync(cancellationToken) && User.Identity?.IsAuthenticated != true)
        {
            return Conflict(new { message = "PostgreSQL is already configured." });
        }

        try
        {
            await _postgres.SaveAndVerifyAsync(request ?? new PostgresSettings(), cancellationToken);
            if (!await _database.TryInitializeAsync(cancellationToken))
            {
                return BadRequest(new { message = "Saved the connection, but ChannelFlow could not initialize the database." });
            }

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
            var hasUser = await db.AdminUsers.AnyAsync(cancellationToken);
            return Ok(new
            {
                needsDatabase = false,
                needsSetup = !hasUser,
                database = _postgres.GetPublicSettings()
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (!_database.IsReady)
        {
            return BadRequest(new { message = "Configure PostgreSQL first." });
        }

        if (await _db.AdminUsers.AnyAsync(cancellationToken))
        {
            return Conflict(new { message = "Admin user already exists." });
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Username and password are required." });
        }

        if (request.Password.Length < 8)
        {
            return BadRequest(new { message = "Password must be at least 8 characters." });
        }

        var userName = request.UserName.Trim();
        _db.AdminUsers.Add(new AdminUser
        {
            UserName = userName,
            PasswordHash = Services.PasswordHasher.Hash(request.Password)
        });
        await _db.SaveChangesAsync(cancellationToken);
        await SignInAsync(userName, request.RememberMe);
        return Ok(new { authenticated = true, userName });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.AdminUsers.FirstOrDefaultAsync(
            u => u.UserName == request.UserName,
            cancellationToken);
        if (user is null || !Services.PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }

        await SignInAsync(user.UserName, request.RememberMe);
        return Ok(new { authenticated = true, userName = user.UserName });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok();
    }

    [HttpPost("password")]
    [Authorize(Policy = "admin")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.AdminUsers.FirstOrDefaultAsync(u => u.UserName == User.Identity!.Name, cancellationToken);
        if (user is null || !Services.PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            return BadRequest(new { message = "Current password is incorrect." });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { message = "New password must be at least 8 characters." });
        }

        user.PasswordHash = Services.PasswordHasher.Hash(request.NewPassword);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    private Task SignInAsync(string userName, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Role, "admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var props = new AuthenticationProperties
        {
            IsPersistent = rememberMe
        };
        if (rememberMe)
        {
            props.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30);
        }

        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            props);
    }

    private async Task<bool> HasAdminAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
            return await db.AdminUsers.AnyAsync(cancellationToken);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public class LoginRequest
{
    public string? UserName { get; set; }

    public string? Password { get; set; }

    public bool RememberMe { get; set; }
}

public class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }

    public string? NewPassword { get; set; }
}

public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        // Programme posters are <img> URLs in XMLTV; the browser cannot send the IPTV API key.
        var isIptvPoster = path.StartsWith("/iptv/poster/", StringComparison.OrdinalIgnoreCase);
        // ErsatzTV next bridge: the engine fetches resolver/source URLs with its internal
        // token (checked in the controller), and players fetch proxied HLS session URLs that
        // carry no API key (relative segment resolution drops query strings).
        var isNextBridge = path.StartsWith("/iptv/next/", StringComparison.OrdinalIgnoreCase);
        var isClientLogIngest = HttpMethods.IsPost(context.Request.Method)
            && path.StartsWith("/api/client-logs", StringComparison.OrdinalIgnoreCase);
        var isClientSession = HttpMethods.IsPost(context.Request.Method)
            && path.Equals("/api/clients/session", StringComparison.OrdinalIgnoreCase);
        var isClientForget = HttpMethods.IsDelete(context.Request.Method)
            && path.Equals("/api/clients/me", StringComparison.OrdinalIgnoreCase);
        var needsApiKey = !isIptvPoster
            && !isNextBridge
            && (path.StartsWith("/iptv", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/plugin", StringComparison.OrdinalIgnoreCase)
                || isClientLogIngest
                || isClientSession
                || isClientForget);

        if (needsApiKey)
        {
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                ?? context.Request.Query["apiKey"].FirstOrDefault();
            var clients = context.RequestServices.GetRequiredService<PairedTvClientStore>();
            var pluginOk = PluginApiKey.Matches(provided);
            var paired = pluginOk ? null : clients.FindByApiKey(provided);
            if (!pluginOk && paired is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Invalid API key.",
                    code = ChannelFlowApiAuth.RevokedCode
                });
                return;
            }

            context.Items[ChannelFlowApiAuth.ApiKeyItem] = provided?.Trim();
            if (paired is not null)
            {
                context.Items[ChannelFlowApiAuth.ClientItem] = paired;
                if (!path.StartsWith("/iptv/stream", StringComparison.OrdinalIgnoreCase))
                {
                    clients.Touch(provided);
                }
            }
        }

        await _next(context);
    }
}
