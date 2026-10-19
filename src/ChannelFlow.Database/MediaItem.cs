namespace ChannelFlow.Database;

public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MatchKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public long? RuntimeMs { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? OfficialRating { get; set; }
    public string? PosterUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? SourceKind { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? ConnectionId { get; set; }
    public string? LibraryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
