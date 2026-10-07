using FinTv.Configuration;
using FinTv.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/normalization")]
[Authorize(Policy = "admin")]
public class NormalizationController : ControllerBase
{
    private readonly StreamNormalizationService _normalization;
    private readonly FfmpegEncodingService _encoding;
    private readonly GpuCapabilityService _gpu;
    private readonly FfmpegCommandBuilder _commands;

    public NormalizationController(
        StreamNormalizationService normalization,
        FfmpegEncodingService encoding,
        GpuCapabilityService gpu,
        FfmpegCommandBuilder commands)
    {
        _normalization = normalization;
        _encoding = encoding;
        _gpu = gpu;
        _commands = commands;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<object>> GetSettings(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await BuildPayloadAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not load normalization settings: {ex.Message}" });
        }
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] NormalizationSettingsRequest? request,
        CancellationToken cancellationToken)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        try
        {
            await _gpu.GetAsync(cancellationToken);
            var accel = _encoding.Describe().HardwareAcceleration;
            plugin.Configuration.Normalization = request.ResetToDefaults == true
                ? _gpu.ClampNormalization(NormalizationSettings.CreateDefault(), accel)
                : _gpu.ClampNormalization(
                    NormalizationTarget.FromSettings(new NormalizationSettings
                    {
                        Resolution = request.Resolution ?? NormalizationSettings.DefaultResolution,
                        FrameRate = request.FrameRate ?? NormalizationSettings.DefaultFrameRate,
                        VideoCodec = request.VideoCodec ?? NormalizationSettings.DefaultVideoCodec,
                        VideoProfile = request.VideoProfile ?? NormalizationSettings.DefaultVideoProfile,
                        VideoBitrate = request.VideoBitrate ?? NormalizationSettings.DefaultVideoBitrate,
                        AudioCodec = request.AudioCodec ?? NormalizationSettings.DefaultAudioCodec,
                        AudioChannels = request.AudioChannels ?? NormalizationSettings.DefaultAudioChannels,
                        AudioSampleRate = request.AudioSampleRate ?? NormalizationSettings.DefaultAudioSampleRate,
                        AudioBitrate = request.AudioBitrate ?? NormalizationSettings.DefaultAudioBitrate,
                        NormalizationMode = request.NormalizationMode ?? NormalizationSettings.DefaultNormalizationMode
                    }).ToSettings(),
                    accel);
            plugin.SaveConfiguration();
            _normalization.ApplyFromSaved(plugin.Configuration.Normalization);
            return Ok(await BuildPayloadAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Could not save normalization settings: {ex.Message}" });
        }
    }

    private async Task<object> BuildPayloadAsync(CancellationToken cancellationToken)
    {
        var caps = await _gpu.GetAsync(cancellationToken);
        _encoding.ApplyFromSaved(FinTvRuntime.Current?.Configuration.Transcode);
        var accel = _encoding.Describe().HardwareAcceleration;
        var clamped = _gpu.ClampNormalization(_normalization.Current.ToSettings(), accel);
        _normalization.ApplyFromSaved(clamped);
        var target = _normalization.Current;
        var format = _gpu.FormatFor(accel);
        return new
        {
            resolution = target.Resolution,
            frameRate = target.FrameRate,
            videoCodec = target.VideoCodec,
            videoProfile = target.VideoProfile,
            videoBitrate = target.VideoBitrate,
            audioCodec = target.AudioCodec,
            audioChannels = target.AudioChannels,
            audioSampleRate = target.AudioSampleRate.ToString(),
            audioBitrate = target.AudioBitrate,
            normalizationMode = target.NormalizationMode,
            summary = target.Summary,
            pipeline = _commands.DescribePipeline(),
            capabilities = new
            {
                summary = caps.Summary,
                driver = caps.Driver,
                acceleration = accel,
                videoCodecs = format.VideoCodecs,
                h264Profiles = format.H264Profiles,
                resolutions = format.Resolutions,
                frameRates = format.FrameRates
            }
        };
    }
}

public class NormalizationSettingsRequest
{
    public string? Resolution { get; set; }

    public string? FrameRate { get; set; }

    public string? VideoCodec { get; set; }

    public string? VideoProfile { get; set; }

    public string? VideoBitrate { get; set; }

    public string? AudioCodec { get; set; }

    public string? AudioChannels { get; set; }

    public string? AudioSampleRate { get; set; }

    public string? AudioBitrate { get; set; }

    /// <summary><c>transcode</c> or <c>copy</c>. Apply the new next-normalization mode.</summary>
    public string? NormalizationMode { get; set; }

    public bool? ResetToDefaults { get; set; }
}
