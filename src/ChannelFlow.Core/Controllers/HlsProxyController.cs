using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("hls")]
public class HlsProxyController : ControllerBase
{
    private readonly HttpClient _http;
    private readonly string _sidecarUrl;
    public HlsProxyController(IHttpClientFactory factory)
    {
        _http = factory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _sidecarUrl = Environment.GetEnvironmentVariable("ERSATZTV_URL") ?? "http://127.0.0.1:8409";
    }

    [HttpGet("{**path}")]
    public async Task<IActionResult> Proxy(string path, CancellationToken ct)
    {
        var target = _sidecarUrl.TrimEnd('/') + "/" + (path ?? "");
        if (Request.QueryString.HasValue) target += Request.QueryString.Value;

        HttpResponseMessage res;
        try { res = await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception e) { return StatusCode(502, new { error = "sidecar unreachable", detail = e.Message }); }

        if (!res.IsSuccessStatusCode) return StatusCode((int)res.StatusCode);

        var contentType = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var isPlaylist = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

        if (isPlaylist)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            var baseUri = new Uri(_sidecarUrl);
            var proxyBase = "/hls/";
            var sb = new System.Text.StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                { sb.AppendLine(line); continue; }
                if (trimmed.StartsWith("#"))
                {
                    if (trimmed.Contains("URI=\""))
                    {
                        var rewritten = System.Text.RegularExpressions.Regex.Replace(trimmed, @"URI=""([^""]+)""", m =>
                            $"URI=\"{RewriteUrl(m.Groups[1].Value, baseUri, proxyBase)}\"");
                        sb.AppendLine(rewritten);
                    }
                    else sb.AppendLine(line);
                    continue;
                }
                sb.AppendLine(RewriteUrl(trimmed, baseUri, proxyBase));
            }
            return Content(sb.ToString(), "application/vnd.apple.mpegurl");
        }

        var stream = await res.Content.ReadAsStreamAsync(ct);
        Response.ContentType = contentType;
        if (res.Content.Headers.ContentLength.HasValue)
            Response.ContentLength = res.Content.Headers.ContentLength.Value;
        await stream.CopyToAsync(Response.Body, ct);
        return new EmptyResult();
    }

    private static string RewriteUrl(string url, Uri baseUri, string proxyBase)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var u = new Uri(url);
                if (u.Host == baseUri.Host && u.Port == baseUri.Port)
                    return proxyBase + u.PathAndQuery.TrimStart('/');
            }
            catch { }
            return url;
        }
        return proxyBase + url.TrimStart('/');
    }
}
