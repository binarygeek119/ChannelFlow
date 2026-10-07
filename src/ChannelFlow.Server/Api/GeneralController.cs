using FinTv.Configuration;
using FinTv.Domain;
using FinTv.Next;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// General ChannelFlow-Server settings for the admin UI.
/// </summary>
[ApiController]
[Route("api/general")]
[Authorize(Policy = "admin")]
public class GeneralController : ControllerBase
{
    /// <summary>
    /// Gets general settings.
    /// </summary>
    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
    {
        try
        {
            var config = FinTvRuntime.Current?.Configuration ?? new PluginConfiguration();
            var scheduleTimeZone = ScheduleTimeZoneHelper.NormalizeTimeZoneId(config.ScheduleTimeZone);
            return Ok(new
            {
                debugLogging = config.DebugLogging,
                scheduleTimeZone,
                playoutDaysToBuild = config.PlayoutDaysToBuild,
                streamIdleTimeoutSeconds = PluginConfiguration.ClampStreamIdleTimeoutSeconds(config.StreamIdleTimeoutSeconds),
                publicBaseUrl = config.PublicBaseUrl
                    ?? ReverseProxyHosting.NormalizePublicBaseUrl(AppEnvironment.Get("PUBLIC_URL"))
                    ?? string.Empty,
                localBaseUrl = config.LocalBaseUrl ?? string.Empty,
                nextEnabled = config.NextTranscoding.Enabled,
                nextBaseUrl = config.NextTranscoding.BaseUrl ?? string.Empty,
                nextResolverBaseUrl = config.NextTranscoding.ResolverBaseUrl ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not load general settings: {ex.Message}" });
        }
    }

    /// <summary>
    /// Lists time zones available on this server for the schedule dropdown.
    /// </summary>
    [HttpGet("timezones")]
    public ActionResult<object> GetTimeZones()
    {
        try
        {
            var timeZones = ScheduleTimeZoneHelper.GetAvailableTimeZones()
                .Select(tz => new
                {
                    id = tz.Id,
                    label = tz.Label,
                    offset = tz.Offset
                });

            return Ok(timeZones);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not load time zones: {ex.Message}" });
        }
    }

    /// <summary>
    /// Updates general settings.
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] GeneralSettingsRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        try
        {
            if (request.DebugLogging.HasValue)
            {
                plugin.Configuration.DebugLogging = request.DebugLogging.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.ScheduleTimeZone))
            {
                var normalized = ScheduleTimeZoneHelper.NormalizeTimeZoneId(request.ScheduleTimeZone);
                if (!ScheduleTimeZoneHelper.TryResolveScheduleTimeZone(normalized, out _, out var resolvedId))
                {
                    return BadRequest(new { message = $"Time zone '{request.ScheduleTimeZone}' is not available on this server." });
                }

                plugin.Configuration.ScheduleTimeZone = resolvedId;
                ScheduleTimeZoneHelper.ApplyAsProcessTimeZone();
            }

            if (request.PlayoutDaysToBuild.HasValue)
            {
                plugin.Configuration.PlayoutDaysToBuild = Math.Clamp(request.PlayoutDaysToBuild.Value, 1, 14);
            }

            if (request.StreamIdleTimeoutSeconds.HasValue)
            {
                plugin.Configuration.StreamIdleTimeoutSeconds =
                    PluginConfiguration.ClampStreamIdleTimeoutSeconds(request.StreamIdleTimeoutSeconds.Value);
            }

            if (request.PublicBaseUrl is not null)
            {
                plugin.Configuration.PublicBaseUrl = ReverseProxyHosting.NormalizePublicBaseUrl(request.PublicBaseUrl);
            }

            if (request.LocalBaseUrl is not null)
            {
                plugin.Configuration.LocalBaseUrl = ReverseProxyHosting.NormalizeLocalBaseUrl(request.LocalBaseUrl);
            }

            if (request.NextEnabled.HasValue)
            {
                plugin.Configuration.NextTranscoding.Enabled = request.NextEnabled.Value;
            }

            if (request.NextBaseUrl is not null)
            {
                plugin.Configuration.NextTranscoding.BaseUrl = NormalizeNextUrl(request.NextBaseUrl);
            }

            if (request.NextResolverBaseUrl is not null)
            {
                plugin.Configuration.NextTranscoding.ResolverBaseUrl = NormalizeNextUrl(request.NextResolverBaseUrl);
            }

            plugin.SaveConfiguration();

            var coordinator = HttpContext.RequestServices.GetRequiredService<NextCoordinatorService>();
            if (plugin.Configuration.NextTranscoding.Enabled)
            {
                await coordinator.WriteConfigurationAsync();
            }
            else
            {
                coordinator.ClearConfiguration();
            }

            return Ok(new
            {
                saved = true,
                debugLogging = plugin.Configuration.DebugLogging,
                scheduleTimeZone = plugin.Configuration.ScheduleTimeZone,
                playoutDaysToBuild = plugin.Configuration.PlayoutDaysToBuild,
                streamIdleTimeoutSeconds = PluginConfiguration.ClampStreamIdleTimeoutSeconds(
                    plugin.Configuration.StreamIdleTimeoutSeconds),
                publicBaseUrl = plugin.Configuration.PublicBaseUrl ?? string.Empty,
                localBaseUrl = plugin.Configuration.LocalBaseUrl ?? string.Empty,
                nextEnabled = plugin.Configuration.NextTranscoding.Enabled,
                nextBaseUrl = plugin.Configuration.NextTranscoding.BaseUrl ?? string.Empty,
                nextResolverBaseUrl = plugin.Configuration.NextTranscoding.ResolverBaseUrl ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Could not save general settings: {ex.Message}" });
        }
    }

    private static string? NormalizeNextUrl(string? value)
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
}

/// <summary>
/// General settings payload.
/// </summary>
public class GeneralSettingsRequest
{
    /// <summary>
    /// Gets or sets whether verbose ChannelFlow debug logging is enabled.
    /// </summary>
    public bool? DebugLogging { get; set; }

    /// <summary>
    /// Gets or sets the schedule time zone IANA id.
    /// </summary>
    public string? ScheduleTimeZone { get; set; }

    /// <summary>
    /// Gets or sets how many days of playout to build (1-14).
    /// </summary>
    public int? PlayoutDaysToBuild { get; set; }

    /// <summary>
    /// Seconds to keep encoding after the last viewer disconnects (0–3600).
    /// </summary>
    public int? StreamIdleTimeoutSeconds { get; set; }

    /// <summary>
    /// Public origin for M3U/XMLTV when reverse-proxied (https://channelflow.example.com).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Same-network origin for Local URL copy and quick pairing (http://192.168.1.2:8097).
    /// </summary>
    public string? LocalBaseUrl { get; set; }

    /// <summary>
    /// Whether channels are served through the ErsatzTV next container.
    /// </summary>
    public bool? NextEnabled { get; set; }

    /// <summary>
    /// Base URL of the next container as seen by ChannelFlow (http://192.168.1.2:8409).
    /// </summary>
    public string? NextBaseUrl { get; set; }

    /// <summary>
    /// Base URL ChannelFlow is reachable at from inside the next container.
    /// </summary>
    public string? NextResolverBaseUrl { get; set; }
}
