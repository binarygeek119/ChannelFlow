using System.Text.Json;

namespace ChannelFlow.PluginSdk;

public interface IPluginStorage
{
    Task<JsonElement?> GetAsync(string Key);
    Task SetAsync(string Key, JsonElement Value);
    Task DeleteAsync(string Key);
}

public interface IPluginLogger
{
    void Info(string Message);
    void Warn(string Message);
    void Error(string Message);
}

public interface IPluginDatabase
{
    // Plugin-specific database access; default no-op
}

public interface IPluginTaskHost
{
    void Report(string Key, string Title, string? Label, long? Current, long? Total, string Status = "running", string? Detail = null);
}

public interface ICoreData
{
    Task<List<CoreChannel>> ChannelsAsync();
    Task<List<CatalogRow>> CatalogAsync(CatalogFilter Filter);
    Task UpsertMediaItemsAsync(List<MediaItemDto> Items);
    Task RemoveMediaItemsAsync(string ConnectionId, string LibraryId);
    Task<List<PersonCreditDto>> GetCreditsForMediaAsync(string MediaMatchKey);
    Task<PersonCreditDto> AddCreditAsync(PersonCreditDto Credit);
    Task RemoveCreditAsync(Guid CreditId);
    Task<ChatResponse> ChatAsync(ChatRequest Request);
    Task FileForAsync(string MatchKey);
    Task SetChannelGuideAsync(string ChannelId, List<GuideProgram> Programs);
    Task SetChannelLineupAsync(string ChannelId, List<LineupSlot> Slots);
    Task GeneratePlayoutAsync(string ChannelId);
}

public record CoreChannel(string Id, double Number, string Name, bool Enabled);

public record CatalogRow(string MatchKey, string Kind, string Title, int? Year, long? RuntimeMs, List<string> Genres, string? OfficialRating, List<string> Tags, string? PosterUrl = null, string? WebUrl = null, string? SourceKind = null);

public record MediaItemDto(string MatchKey, string Kind, string Title, int? Year, long? RuntimeMs, List<string> Genres, string? OfficialRating, List<string> Tags, string? ConnectionId, string? LibraryId, string? PosterUrl = null, string? WebUrl = null, string? SourceKind = null, string? Overview = null);

public record PersonCreditDto(Guid Id, Guid PersonId, string MediaMatchKey, string Role, string? Character, string? ImagePath);

public record CatalogFilter(List<string> Kinds, List<string> Genres, List<string> Tags, int? Limit);

public record ChatRequest(List<ChatMessage> Messages, double? Temperature, bool Json, string? Model);
public record ChatMessage(string Role, string Content);
public record ChatResponse(string Content, string ProviderId, List<string> Attempts);

public record GuideProgram(string Id, string Start, string Finish, string Title, string? Description, string? SubTitle, string? Episode, List<string>? Categories, int? Year, string? Rating, string? PosterUrl);

public record LineupSlot(int SlotIndex, int SpanSlots, bool IsRerun, List<SlotCandidate> Candidates);
public record SlotCandidate(string Kind, string MatchKey, string Title, int Weight);
