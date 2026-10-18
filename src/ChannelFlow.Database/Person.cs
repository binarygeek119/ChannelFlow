namespace ChannelFlow.Database;

public class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? ImagePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PersonCredit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PersonId { get; set; }
    public string MediaMatchKey { get; set; } = "";
    public string Role { get; set; } = ""; // actor, director, writer, etc.
    public string? Character { get; set; }
}
