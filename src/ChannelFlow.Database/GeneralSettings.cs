namespace ChannelFlow.Database;

public class GeneralSettings
{
    public string Key { get; set; } = "general";
    public string PublicUrl { get; set; } = "";
    public string LocalUrl { get; set; } = "";
    public string Timezone { get; set; } = "";
    public int PlayoutDays { get; set; } = 14;
    public int StreamIdleSeconds { get; set; } = 30;
}
