using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed record NewsArticle(string Title, string Summary, string? FeedName, string? ImageUrl = null);

public sealed class NewsHeadlineService
{
    private static readonly Regex ImgSrc = new(
        @"<img\b[^>]*?\bsrc\s*=\s*(?:""(?<url>[^""]+)""|'(?<url>[^']+)'|(?<url>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NewsHeadlineService> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<NewsArticle> _articles = [];
    private DateTime _fetchedAt = DateTime.MinValue;

    public NewsHeadlineService(
        IServiceScopeFactory scopes,
        IHttpClientFactory http,
        ILogger<NewsHeadlineService> logger)
    {
        _scopes = scopes;
        _http = http;
        _logger = logger;
    }

    public IReadOnlyList<NewsArticle> Cached => _articles;

    public DateTime FetchedAt => _fetchedAt;

    public async Task<IReadOnlyList<NewsArticle>> GetAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force)
        {
            lock (_gate)
            {
                if (_articles.Count > 0 && DateTime.UtcNow - _fetchedAt < TimeSpan.FromMinutes(2))
                {
                    return _articles;
                }
            }
        }

        return await RefreshAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NewsArticle>> RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var settings = await db.NewsSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken) ?? new NewsSettings();
        var feeds = await db.NewsFeeds.AsNoTracking()
            .Where(f => f.Enabled)
            .OrderBy(f => f.SortOrder)
            .ToListAsync(cancellationToken);

        var fetchLimit = settings.AiRewrite
            ? Math.Clamp(Math.Max(settings.ArticleCount * 3, 12), 8, 40)
            : Math.Max(1, settings.ArticleCount);
        var articles = await FetchAsync(feeds, fetchLimit, cancellationToken);
        lock (_gate)
        {
            _articles = articles;
            _fetchedAt = DateTime.UtcNow;
        }

        return articles;
    }

    private async Task<List<NewsArticle>> FetchAsync(
        IReadOnlyList<NewsFeed> feeds,
        int limit,
        CancellationToken cancellationToken)
    {
        var articles = new List<NewsArticle>();
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/0.0.3 (news)");
        }

        foreach (var feed in feeds)
        {
            try
            {
                using var stream = await client.GetStreamAsync(feed.Url, cancellationToken);
                var doc = XDocument.Load(stream);
                var items = doc.Descendants("item").Concat(doc.Descendants().Where(e => e.Name.LocalName == "entry"));
                foreach (var item in items)
                {
                    var title = Clean(item.Element("title")?.Value
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var rawSummary = item.Element("description")?.Value
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName is "summary" or "content" or "encoded")?.Value;
                    var summary = Clean(rawSummary);
                    var html = string.Join(
                        " ",
                        item.Elements()
                            .Where(e => e.Name.LocalName is "description" or "summary" or "content" or "encoded")
                            .Select(e => e.Value)
                            .Where(v => !string.IsNullOrWhiteSpace(v)));
                    var imageUrl = ReadImageUrl(item, feed.Url, html);
                    articles.Add(new NewsArticle(title, summary, feed.Name, imageUrl));
                    if (articles.Count >= limit)
                    {
                        return articles;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load RSS feed {Url}", feed.Url);
            }
        }

        return articles;
    }

    internal static string? ReadImageUrl(XElement item, string feedUrl, string? html)
    {
        foreach (var enclosure in item.Elements().Where(e => e.Name.LocalName == "enclosure"))
        {
            var url = ResolveUrl(Attr(enclosure, "url"), feedUrl);
            if (LooksLikeImage(url, Attr(enclosure, "type")))
            {
                return url;
            }
        }

        foreach (var media in item.Descendants().Where(e => e.Name.LocalName is "content" or "thumbnail" or "image"))
        {
            if (media.Name.LocalName == "content"
                && string.IsNullOrWhiteSpace(Attr(media, "url"))
                && string.IsNullOrWhiteSpace(Attr(media, "href"))
                && !string.IsNullOrWhiteSpace(media.Value)
                && media.Value.Contains('<'))
            {
                continue;
            }

            var medium = Attr(media, "medium");
            if (medium is "video" or "audio")
            {
                continue;
            }

            var url = ResolveUrl(Attr(media, "url") ?? Attr(media, "href"), feedUrl);
            if (media.Name.LocalName is "thumbnail" or "image" && !string.IsNullOrWhiteSpace(url)
                && (string.IsNullOrWhiteSpace(medium) || medium == "image"))
            {
                return url;
            }

            if (medium == "image" && !string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (LooksLikeImage(url, Attr(media, "type")))
            {
                return url;
            }

            var nested = media.Elements().FirstOrDefault(e => e.Name.LocalName == "url")?.Value;
            url = ResolveUrl(nested, feedUrl);
            if (LooksLikeImage(url, null))
            {
                return url;
            }
        }

        foreach (var link in item.Elements().Where(e => e.Name.LocalName == "link"))
        {
            var rel = Attr(link, "rel");
            var href = ResolveUrl(Attr(link, "href") ?? link.Value, feedUrl);
            if (rel is "enclosure" or "preview" or "image" && LooksLikeImage(href, Attr(link, "type")))
            {
                return href;
            }
        }

        var img = ImgSrc.Match(html ?? "");
        return img.Success ? ResolveUrl(img.Groups["url"].Value, feedUrl) : null;
    }

    private static string? Attr(XElement el, string name)
        => el.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? ResolveUrl(string? raw, string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = WebUtility.HtmlDecode(raw.Trim());
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs.ToString();
        }

        if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var feed)
            && Uri.TryCreate(feed, value, out var combined)
            && (combined.Scheme == Uri.UriSchemeHttp || combined.Scheme == Uri.UriSchemeHttps))
        {
            return combined.ToString();
        }

        return null;
    }

    private static bool LooksLikeImage(string? url, string? type)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !type.Contains("svg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var path = url.Split('?', 2)[0];
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(StripTags(value)).Trim();
        return decoded.Replace("..", ".").Replace('\u00a0', ' ').Replace('\u2019', '\'');
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<')
            {
                inTag = true;
                continue;
            }

            if (ch == '>')
            {
                inTag = false;
                continue;
            }

            if (!inTag)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
