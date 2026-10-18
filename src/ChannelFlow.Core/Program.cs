using ChannelFlow.PluginSdk;
using ChannelFlow.Core;
using ChannelFlow.Database;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database provider selection via connection string. Defaults to SQLite.
var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=channelflow.db";
var provider = Environment.GetEnvironmentVariable("DATABASE_PROVIDER")
    ?? builder.Configuration["Database:Provider"]
    ?? "sqlite";

builder.Services.AddDbContext<ChannelFlowDbContext>(options =>
{
    switch (provider.ToLowerInvariant())
    {
        case "postgres":
        case "postgresql":
            options.UseNpgsql(connectionString);
            break;
        case "mariadb":
        case "mysql":
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
            break;
        default:
            options.UseSqlite(connectionString);
            break;
    }
});

builder.Services.AddControllers();
var baseUrl = Environment.GetEnvironmentVariable("BASE_URL")
    ?? builder.Configuration["BaseUrl"]
    ?? "http://localhost:5108/";
builder.Services.AddHttpClient("default", client =>
{
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("default"));
builder.Services.AddResponseCaching();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<EfCoreData>(sp => new EfCoreData(sp, Path.Combine(builder.Environment.ContentRootPath, "data")));
builder.Services.AddSingleton<ICoreData>(sp => sp.GetRequiredService<EfCoreData>());
builder.Services.AddScoped<MigrationService>();
builder.Services.AddHostedService<BuildQueue>();
builder.Services.AddSingleton<PluginManager>(sp => new PluginManager(sp));

var app = builder.Build();

// Auto-create schema on startup (dev; use Migrate in prod).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
    db.Database.EnsureCreated();
}

// Auto-discover and load plugins
var pluginsDir = "/config/plugins";
if (!Directory.Exists(pluginsDir)) pluginsDir = Path.Combine(app.Environment.ContentRootPath, "plugins");
if (!Directory.Exists(pluginsDir)) pluginsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins");
if (!Directory.Exists(pluginsDir)) pluginsDir = Path.Combine(Directory.GetCurrentDirectory(), "plugins");
var pm = app.Services.GetRequiredService<PluginManager>();
await PluginDiscovery.LoadAllAsync(pm, pluginsDir, app.Services);

// Map plugin routes
foreach (var plugin in pm.Plugins)
{
    var group = app.MapGroup($"/api/plugins/{plugin.Metadata.Id}");
    plugin.MapRoutes(group);
}

app.UseResponseCaching();
app.UseStaticFiles();
app.MapControllers();

app.MapGet("/api/health", () => new { name = "ChannelFlow", status = "ok", version = "3.0.0" });

app.UseAntiforgery();
// Serve uploaded images
var imagesDir = Path.Combine(builder.Environment.ContentRootPath, "data", "images");
Directory.CreateDirectory(imagesDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(imagesDir),
    RequestPath = "/images"
});

app.MapRazorComponents<ChannelFlow.Core.Components.App>().AddInteractiveServerRenderMode();

app.MapGet("/api/ui/version", () => new { version = "3.0.0", name = "ChannelFlow" });

app.Run();
