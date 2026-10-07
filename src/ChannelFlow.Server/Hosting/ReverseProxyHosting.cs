using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace FinTv;

internal static class ReverseProxyHosting
{
    public static void AddReverseProxySupport(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost
                | ForwardedHeaders.XForwardedPrefix;
            options.RequireHeaderSymmetry = false;
            options.ForwardLimit = 4;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromHours(8);
        });

        var configDir = AppEnvironment.FromConfiguration(builder.Configuration, "CONFIG")
            ?? Path.Combine(builder.Environment.ContentRootPath, "config");
        var keyDir = Path.Combine(configDir, "dataprotection");
        Directory.CreateDirectory(keyDir);
        builder.Services.AddDataProtection()
            .SetApplicationName("ChannelFlow")
            .PersistKeysToFileSystem(new DirectoryInfo(keyDir));
    }

    public static void UseReverseProxy(this WebApplication app)
    {
        app.UseForwardedHeaders();

        var pathBase = AppEnvironment.FromConfiguration(app.Configuration, "PATH_BASE")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE");
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            var trimmed = pathBase.Trim();
            if (!trimmed.StartsWith('/'))
            {
                trimmed = "/" + trimmed;
            }

            app.UsePathBase(trimmed.TrimEnd('/'));
        }
    }

    public static void MapSpaFallback(this WebApplication app)
    {
        async Task WriteIndex(HttpContext context)
        {
            var file = Path.Combine(app.Environment.WebRootPath, "index.html");
            var html = await File.ReadAllTextAsync(file);
            var prefix = context.Request.PathBase.HasValue
                ? context.Request.PathBase.Value!.TrimEnd('/')
                : "";
            var href = string.IsNullOrEmpty(prefix) ? "/" : prefix + "/";
            html = html.Replace("<base href=\"/\">", "<base href=\"" + href + "\">");
            html = html.Replace("window.__CF_BASE__=\"\"", "window.__CF_BASE__=\"" + prefix + "\"");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(html);
        }

        app.MapGet("/", WriteIndex);
        app.MapGet("/index.html", WriteIndex);
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"error\":\"Not found\"}");
                return;
            }

            await WriteIndex(context);
        });
    }

    public static string PublicOrigin(HttpRequest request)
    {
        var prefix = request.PathBase.HasValue ? request.PathBase.Value!.TrimEnd('/') : "";
        return $"{request.Scheme}://{request.Host}{prefix}";
    }

    public static string? NormalizeLocalBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var url = value.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "http://" + url;
        }

        return url;
    }

    /// <summary>
    /// True when the host is a loopback or private-network IP literal, meaning the client is
    /// reaching ChannelFlow directly on the local network rather than through the public URL.
    /// </summary>
    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var value = host.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1];
        }

        if (!IPAddress.TryParse(value, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC
                || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }

        return false;
    }

    public static bool IsLocalOrigin(string? origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLocalHost(uri.Host);

    /// <summary>
    /// Same-network base URL: the configured local URL when set, otherwise the origin the
    /// browser connected with. A bridge-networked container cannot discover the host's LAN
    /// address on its own, so the configured value wins whenever the admin browses through
    /// the public URL.
    /// </summary>
    public static string LocalBaseUrl(HttpRequest request)
        => NormalizeLocalBaseUrl(FinTvRuntime.Current?.Configuration.LocalBaseUrl)
            ?? PublicOrigin(request);

    public static string? NormalizePublicBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var url = value.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        return url;
    }
}
