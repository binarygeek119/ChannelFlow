namespace ChannelFlow.Database;

public class PluginRepo
{
    public int Id { get; set; }
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class InstalledPlugin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string RepoUrl { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
