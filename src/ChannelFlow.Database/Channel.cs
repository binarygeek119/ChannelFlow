namespace ChannelFlow.Database;

public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Number { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string AiPrompt { get; set; } = "";
    public string TranscodeJson { get; set; } = "{}";
    public string WatermarkJson { get; set; } = "null";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
