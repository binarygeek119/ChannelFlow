namespace ChannelFlow.Database;

public class GuideProgramRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChannelId { get; set; } = "";
    public string ProgramId { get; set; } = "";
    public string Start { get; set; } = "";
    public string Finish { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? PosterUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
