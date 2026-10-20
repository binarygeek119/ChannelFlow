namespace ChannelFlow.PluginSdk;

public interface IMediaSource
{
    string TypeId { get; }
    string DisplayName { get; }
    List<FieldSpec> ConnectionFields { get; }
    List<MediaType> SupportedMedia { get; }

    Task<TestResult> TestConnectionAsync(Connection Connection, string ApiKey);
    Task<List<Library>> ListLibrariesAsync(Connection Connection, string ApiKey);
    Task<SyncReport> SyncLibraryAsync(SyncContext Context);
    Task<SyncReport> DownloadPostersAsync(SyncContext Context);
    Task<SyncReport> DownloadPeopleImagesAsync(SyncContext Context);
}

public record FieldSpec(string Kind, string Name, string Label, bool Required = false, string? Default = null)
{
    public static FieldSpec Text(string Name, string Label, bool Required = false) => new("text", Name, Label, Required);
}

public enum MediaType { Movie, Series, Episode, Track, MusicVideo }

public record Connection(string Id, string Kind, Dictionary<string, string> Config);

public record Library(string Id, string Name, MediaType MediaType);

public record SyncContext(string ConnectionId, string LibraryId, IPluginStorage Storage, IPluginLogger Logger, ICoreData Core, string ApiKey, Connection? Connection = null, string MediaType = "movie", IPluginTaskHost? Tasks = null, string? TaskKey = null);

public record SyncReport(int ItemsAdded, int ItemsUpdated, int ItemsRemoved, List<string> Errors = default!);

public record TestResult(bool Ok, string Detail = "");
