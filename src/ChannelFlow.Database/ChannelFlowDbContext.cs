using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Database;

public class ChannelFlowDbContext : DbContext
{
    public ChannelFlowDbContext(DbContextOptions<ChannelFlowDbContext> options) : base(options) { }

    public DbSet<Channel> Channels { get; set; } = null!;
    public DbSet<MediaItem> MediaItems { get; set; } = null!;
    public DbSet<GuideProgramRow> GuidePrograms { get; set; } = null!;
    public DbSet<AppSetting> Settings { get; set; } = null!;
    public DbSet<GeneralSettings> GeneralSettings { get; set; } = null!;
    public DbSet<Connection> Connections { get; set; } = null!;
    public DbSet<PluginRepo> PluginRepos { get; set; } = null!;
    public DbSet<InstalledPlugin> InstalledPlugins { get; set; } = null!;
    public DbSet<Person> People { get; set; } = null!;
    public DbSet<PersonCredit> PersonCredits { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Channel>().HasKey(c => c.Id);
        modelBuilder.Entity<Channel>().Property(c => c.Number).IsRequired();
        modelBuilder.Entity<Channel>().Property(c => c.Name).IsRequired().HasMaxLength(200);

        modelBuilder.Entity<MediaItem>().HasKey(m => m.Id);
        modelBuilder.Entity<MediaItem>().Property(m => m.MatchKey).IsRequired();
        modelBuilder.Entity<MediaItem>().Property(m => m.Kind).IsRequired();
        modelBuilder.Entity<MediaItem>().Property(m => m.Title).IsRequired();

        modelBuilder.Entity<GuideProgramRow>().HasKey(g => g.Id);
        modelBuilder.Entity<GuideProgramRow>().Property(g => g.ChannelId).IsRequired();
        modelBuilder.Entity<GuideProgramRow>().Property(g => g.Title).IsRequired();
        modelBuilder.Entity<GuideProgramRow>().HasIndex(g => new { g.ChannelId, g.Start });
        modelBuilder.Entity<MediaItem>().HasIndex(m => m.MatchKey).IsUnique();
        modelBuilder.Entity<MediaItem>().HasIndex(m => m.Kind);
        modelBuilder.Entity<Person>().HasIndex(p => p.Name);
        modelBuilder.Entity<PersonCredit>().HasIndex(c => c.MediaMatchKey);
        modelBuilder.Entity<Channel>().HasIndex(c => c.Number).IsUnique();
        modelBuilder.Entity<AppSetting>().HasKey(s => s.Key);
        modelBuilder.Entity<GeneralSettings>().HasKey(s => s.Key);
        modelBuilder.Entity<Connection>().HasKey(c => c.Id);
        modelBuilder.Entity<PluginRepo>().HasKey(r => r.Id);
        modelBuilder.Entity<InstalledPlugin>().HasKey(i => i.Id);
        modelBuilder.Entity<Person>().HasKey(p => p.Id);
        modelBuilder.Entity<PersonCredit>().HasKey(c => c.Id);
    }
}
