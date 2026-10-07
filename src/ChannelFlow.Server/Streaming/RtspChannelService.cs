using System.Text;
using CliWrap;
using FinTv.Domain;

namespace FinTv.Streaming;

/// <summary>
/// Encodes a live RTSP feed (weather/news channels pointed at a camera) into the
/// channel's normalized MPEG-TS. Used by ChannelFlow's own tuner path; when the
/// ErsatzTV next engine is enabled the feed is handed to next directly instead.
/// </summary>
public sealed class RtspChannelService
{
    private readonly IFfmpegLocator _ffmpeg;
    private readonly FfmpegCommandBuilder _commands;
    private readonly ILogger<RtspChannelService> _logger;

    public RtspChannelService(
        IFfmpegLocator ffmpeg,
        FfmpegCommandBuilder commands,
        ILogger<RtspChannelService> logger)
    {
        _ffmpeg = ffmpeg;
        _commands = commands;
        _logger = logger;
    }

    public async Task StreamAsync(Channel channel, string rtspUrl, Stream output, CancellationToken cancellationToken)
    {
        var args = _commands.BuildRtspCommand(channel, rtspUrl);
        var stderr = new StringBuilder();

        try
        {
            var result = await Cli.Wrap(_ffmpeg.EncoderPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStream(output, autoFlush: true))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .ExecuteAsync(cancellationToken);

            if (result.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "RTSP ffmpeg exited {Exit} for {Channel} ({Url}): {Error}",
                    result.ExitCode,
                    channel.Name,
                    rtspUrl,
                    stderr.ToString().Trim());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Channel switch/tuner stop.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTSP stream failed for {Channel} ({Url})", channel.Name, rtspUrl);
        }
    }
}