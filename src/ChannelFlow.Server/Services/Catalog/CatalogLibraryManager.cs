using System.Text.Json;
using FinTv;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinTv.Services;

/// <summary>
/// Postgres-backed stand-in for Jellyfin ILibraryManager + IChapterManager.
/// Reads TvShows/Episodes/Movies/Music/MusicVideos/PastTenseNews, with MediaItems as fallback.
/// </summary>
public sealed class CatalogLibraryManager : ILibraryManager, IChapterManager
{
    private readonly FinTvDbContext _db;
    private readonly PathRemapService _remap;

    public CatalogLibraryManager(FinTvDbContext db, PathRemapService remap)
    {
        _db = db;
        _remap = remap;
    }

    public BaseItem? GetItemById(Guid id)
    {
        var reported = FinTvRuntime.Current?.Configuration.JellyfinLibraries.Libraries
            .FirstOrDefault(library => library.Id == id);
        if (reported is not null)
        {
            return new CollectionFolder
            {
                Id = reported.Id,
                Name = reported.Name,
                CollectionType = reported.CollectionType,
                Kind = BaseItemKind.Folder,
                LibraryId = reported.Id,
                LibraryName = reported.Name
            };
        }

        var tv = _db.TvShows.AsNoTracking().FirstOrDefault(row => row.Id == id);
        if (tv is not null)
        {
            return Map(tv, BaseItemKind.Series);
        }

        var episodeRow = TryGetEpisode(id);
        if (episodeRow is not null)
        {
            var item = Map(episodeRow, BaseItemKind.Episode);
            if (item is Episode episode && episodeRow.SeriesId is Guid seriesId && seriesId != Guid.Empty)
            {
                var series = _db.TvShows.AsNoTracking().FirstOrDefault(row => row.Id == seriesId);
                if (series is not null)
                {
                    episode.Series = Map(series, BaseItemKind.Series) as Series;
                }
            }

            return item;
        }

        var movie = _db.Movies.AsNoTracking().FirstOrDefault(row => row.Id == id);
        if (movie is not null)
        {
            return Map(movie, BaseItemKind.Movie);
        }

        var music = _db.Music.AsNoTracking().FirstOrDefault(row => row.Id == id);
        if (music is not null)
        {
            return Map(music, BaseItemKind.Audio);
        }

        var video = _db.MusicVideos.AsNoTracking().FirstOrDefault(row => row.Id == id);
        if (video is not null)
        {
            return Map(video, BaseItemKind.MusicVideo);
        }

        var news = _db.PastTenseNews.AsNoTracking().FirstOrDefault(row => row.Id == id);
        if (news is not null)
        {
            return MapNews(news);
        }

        var row = _db.MediaItems.AsNoTracking().FirstOrDefault(item => item.Id == id);
        return row is null ? null : Map(row, includeSeries: true);
    }

    private EpisodeRow? TryGetEpisode(Guid id)
    {
        try
        {
            return _db.Episodes.AsNoTracking().FirstOrDefault(row => row.Id == id);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    public QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query)
    {
        var kinds = query.IncludeItemTypes is { Length: > 0 }
            ? query.IncludeItemTypes.ToHashSet()
            : null;
        var items = QueryTyped(query, kinds);
        var skipMediaFallback = kinds is { Count: 1 }
            && kinds.Contains(BaseItemKind.Audio)
            && items.Count > 0;
        if (!skipMediaFallback)
        {
            var typedIds = items.Select(item => item.Id).ToHashSet();
            items.AddRange(QueryMediaItems(query, kinds).Where(item => !typedIds.Contains(item.Id)));
        }

        var librarySync = FinTvRuntime.Current?.Configuration.JellyfinLibraries;
        if (librarySync is not null)
        {
            var allowed = items
                .Where(item => librarySync.Allows(item.Kind, item.LibraryId, item.LibraryName))
                .ToList();
            if (allowed.Count > 0 || items.Count == 0)
            {
                items = allowed;
            }
        }

        if (query.Tags is { Length: > 0 })
        {
            items = items.Where(item => query.Tags.All(required =>
                item.Tags.Any(tag => tag.Equals(required, StringComparison.OrdinalIgnoreCase)))).ToList();
        }

        if (query.Genres is { Length: > 0 })
        {
            items = items.Where(item => query.Genres.Any(required =>
                item.Genres.Any(genre => genre.Equals(required, StringComparison.OrdinalIgnoreCase)))).ToList();
        }

        items = items
            .OrderBy(item => item.SortName ?? item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ParentIndexNumber)
            .ThenBy(item => item.IndexNumber)
            .ToList();

        if (query.Limit is > 0)
        {
            items = items.Take(query.Limit.Value).ToList();
        }

        return new QueryResult<BaseItem> { Items = items };
    }

    public IReadOnlyList<VirtualFolderInfo> GetVirtualFolders()
    {
        var reported = FinTvRuntime.Current?.Configuration.JellyfinLibraries.Libraries;
        if (reported is { Count: > 0 })
        {
            return reported
                .Where(library => library.Id != Guid.Empty)
                .Select(library => new VirtualFolderInfo
                {
                    ItemId = library.Id.ToString(),
                    Name = library.Name,
                    CollectionType = library.CollectionType
                })
                .ToList();
        }

        return DistinctLibraries()
            .Select(library => new VirtualFolderInfo
            {
                ItemId = library.Id.ToString(),
                Name = library.Name,
                CollectionType = library.CollectionType
            })
            .ToList();
    }

    public CollectionFolder GetUserRootFolder()
    {
        var children = GetVirtualFolders()
            .Select(folder =>
            {
                Guid.TryParse(folder.ItemId, out var id);
                return new CollectionFolder
                {
                    Id = id,
                    Name = folder.Name,
                    CollectionType = folder.CollectionType,
                    Kind = BaseItemKind.Folder,
                    LibraryId = id == Guid.Empty ? null : id,
                    LibraryName = folder.Name
                };
            })
            .Cast<BaseItem>()
            .ToList();

        return new CollectionFolder
        {
            Id = Guid.Empty,
            Name = "Media",
            Children = children
        };
    }

    public IReadOnlyList<ChapterInfo> GetChapters(Guid itemId)
    {
        var fromMedia = _db.MediaChapters.AsNoTracking()
            .Where(chapter => chapter.MediaItemId == itemId)
            .OrderBy(chapter => chapter.StartPositionTicks)
            .Select(chapter => new ChapterInfo { StartPositionTicks = chapter.StartPositionTicks, Name = chapter.Name })
            .ToList();
        if (fromMedia.Count > 0)
        {
            return fromMedia;
        }

        var json = _db.TvShows.AsNoTracking().Where(row => row.Id == itemId).Select(row => row.ChaptersJson).FirstOrDefault()
            ?? _db.Episodes.AsNoTracking().Where(row => row.Id == itemId).Select(row => row.ChaptersJson).FirstOrDefault()
            ?? _db.Movies.AsNoTracking().Where(row => row.Id == itemId).Select(row => row.ChaptersJson).FirstOrDefault()
            ?? _db.Music.AsNoTracking().Where(row => row.Id == itemId).Select(row => row.ChaptersJson).FirstOrDefault()
            ?? _db.MusicVideos.AsNoTracking().Where(row => row.Id == itemId).Select(row => row.ChaptersJson).FirstOrDefault()
            ?? _db.PastTenseNews.AsNoTracking().Where(row => row.Id == itemId).Select(row => row.ChaptersJson).FirstOrDefault();
        return ParseChapters(json);
    }

    private List<BaseItem> QueryTyped(InternalItemsQuery query, HashSet<BaseItemKind>? kinds)
    {
        _remap.LoadMappings();
        var items = new List<BaseItem>();
        var wantAll = kinds is null || kinds.Count == 0;
        var parentId = query.ParentId;

        if (wantAll || kinds!.Contains(BaseItemKind.Series))
        {
            IQueryable<TvShowRow> tv = _db.TvShows.AsNoTracking().Where(row => !row.IsMissing);
            if (parentId != Guid.Empty)
            {
                tv = tv.Where(row => row.Id == parentId || row.LibraryId == parentId);
            }

            tv = ApplyNameFilter(tv, query);
            items.AddRange(MapRows(tv, BaseItemKind.Series));
        }

        if (wantAll || kinds!.Contains(BaseItemKind.Episode))
        {
            try
            {
                IQueryable<EpisodeRow> episodes = _db.Episodes.AsNoTracking().Where(row => !row.IsMissing);
                if (parentId != Guid.Empty)
                {
                    episodes = episodes.Where(row => row.SeriesId == parentId || row.Id == parentId || row.LibraryId == parentId);
                }

                episodes = ApplyNameFilter(episodes, query);
                items.AddRange(MapRows(episodes, BaseItemKind.Episode));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
            }
        }

        if (wantAll
            || kinds!.Contains(BaseItemKind.Episode)
            || kinds.Contains(BaseItemKind.Movie)
            || kinds.Contains(BaseItemKind.Video))
        {
            IQueryable<PastTenseNewsRow> news = _db.PastTenseNews.AsNoTracking().Where(row => !row.IsMissing);
            if (parentId != Guid.Empty)
            {
                news = news.Where(row => row.SeriesId == parentId || row.Id == parentId || row.LibraryId == parentId);
            }

            news = ApplyNameFilter(news, query);
            items.AddRange(news.ToList().Select(MapNews));
        }

        if (wantAll || kinds!.Contains(BaseItemKind.Movie) || kinds.Contains(BaseItemKind.Video))
        {
            IQueryable<MovieRow> movies = _db.Movies.AsNoTracking().Where(row => !row.IsMissing);
            if (parentId != Guid.Empty)
            {
                movies = movies.Where(row => row.LibraryId == parentId || row.Id == parentId);
            }

            movies = ApplyNameFilter(movies, query);
            items.AddRange(MapRows(movies, BaseItemKind.Movie));
        }

        if (wantAll || kinds!.Contains(BaseItemKind.Audio))
        {
            IQueryable<MusicRow> music = _db.Music.AsNoTracking().Where(row => !row.IsMissing);
            if (parentId != Guid.Empty)
            {
                music = music.Where(row => row.LibraryId == parentId || row.Id == parentId);
            }

            music = ApplyNameFilter(music, query);
            items.AddRange(MapRows(music, BaseItemKind.Audio));
        }

        if (wantAll || kinds!.Contains(BaseItemKind.MusicVideo))
        {
            IQueryable<MusicVideoRow> videos = _db.MusicVideos.AsNoTracking().Where(row => !row.IsMissing);
            if (parentId != Guid.Empty)
            {
                videos = videos.Where(row => row.LibraryId == parentId || row.Id == parentId);
            }

            videos = ApplyNameFilter(videos, query);
            items.AddRange(MapRows(videos, BaseItemKind.MusicVideo));
        }

        return items;
    }

    private List<BaseItem> QueryMediaItems(InternalItemsQuery query, HashSet<BaseItemKind>? kinds)
    {
        IQueryable<MediaItem> items = _db.MediaItems.AsNoTracking().Where(item => !item.IsMissing);
        if (kinds is { Count: > 0 })
        {
            var kindList = kinds.ToArray();
            items = items.Where(item => kindList.Contains(item.Kind));
        }

        if (query.ParentId != Guid.Empty)
        {
            var parentId = query.ParentId;
            items = items.Where(item =>
                item.ParentId == parentId || item.SeriesId == parentId || item.LibraryId == parentId);
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name;
            items = items.Where(item =>
                item.Name == name
                || (item.CollectionNamesJson != null && item.CollectionNamesJson.Contains(name)));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm;
            items = items.Where(item => item.Name.Contains(term) || (item.Overview != null && item.Overview.Contains(term)));
        }

        return items.ToList().Select(row => Map(row, includeSeries: false)).ToList();
    }

    private static IQueryable<T> ApplyNameFilter<T>(IQueryable<T> query, InternalItemsQuery request)
        where T : CatalogMediaRow
    {
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = request.Name;
            query = query.Where(row => row.Name == name);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = request.SearchTerm;
            query = query.Where(row => row.Name.Contains(term) || (row.Plot != null && row.Plot.Contains(term)));
        }

        return query;
    }

    private List<BaseItem> MapRows<T>(IQueryable<T> query, BaseItemKind kind)
        where T : CatalogMediaRow
        => query.ToList().Select(row => Map(row, kind)).ToList();

    private IEnumerable<(Guid Id, string Name, string? CollectionType)> DistinctLibraries()
    {
        var fromConfig = FinTvRuntime.Current?.Configuration.JellyfinLibraries.Libraries ?? [];
        if (fromConfig.Count > 0)
        {
            return fromConfig.Select(library => (library.Id, library.Name, library.CollectionType));
        }

        return _db.MediaItems.AsNoTracking()
            .Where(item => item.Kind == BaseItemKind.Folder && item.LibraryId == null)
            .Select(item => new { item.Id, item.Name, item.CollectionType })
            .ToList()
            .Select(item => (item.Id, item.Name, item.CollectionType));
    }

    private BaseItem MapNews(PastTenseNewsRow row)
        => Map(row, BaseItemKind.Movie);

    private BaseItem Map(CatalogMediaRow row, BaseItemKind kind)
    {
        BaseItem item = kind switch
        {
            BaseItemKind.Episode => new Episode(),
            BaseItemKind.Movie => new Movie(),
            BaseItemKind.Series => new Series(),
            BaseItemKind.MusicVideo => new MusicVideo(),
            BaseItemKind.Audio => new Audio(),
            _ => new BaseItem()
        };

        item.Id = row.Id;
        item.Name = row.Name;
        item.SortName = row.SortName;
        item.Overview = row.Plot;
        item.Path = _remap.ResolveExistingPath(row.Path, row.SourceConnectionId) ?? row.Path;
        item.OfficialRating = row.OfficialRating;
        item.ProductionYear = row.ProductionYear;
        item.PremiereDate = row.PremiereDate;
        item.RunTimeTicks = row.RuntimeTicks;
        item.LibraryId = row.LibraryId;
        item.LibraryName = row.LibraryName;
        item.PrimaryImagePath = _remap.ResolveExistingPath(row.PrimaryImagePath, row.SourceConnectionId) ?? row.PrimaryImagePath;
        item.Width = row.Width;
        item.Height = row.Height;
        item.VideoCodec = row.VideoCodec;
        item.TrueAspectRatio = row.TrueAspectRatio;
        item.AspectRatio = row.AspectRatio;
        item.Tags = ReadStringArray(row.TagsJson);
        item.Genres = ReadStringArray(row.GenresJson);
        item.Studios = ReadStringArray(row.StudiosJson);
        item.Chapters = ParseChapters(row.ChaptersJson);
        item.Kind = kind;
        item.ParentId = row.LibraryId ?? Guid.Empty;

        if (row is EpisodeRow episode)
        {
            item.SeriesId = episode.SeriesId ?? Guid.Empty;
            item.SeriesName = episode.SeriesName;
            item.IndexNumber = episode.EpisodeNumber;
            item.ParentIndexNumber = episode.SeasonNumber;
            item.ParentId = episode.SeriesId ?? episode.LibraryId ?? Guid.Empty;
        }

        if (row is MusicRow track)
        {
            item.IndexNumber = track.TrackNumber;
            item.ParentIndexNumber = track.DiscNumber;
        }

        if (row is MusicVideoRow video && item is MusicVideo musicVideo)
        {
            musicVideo.Artists = ReadStringArray(video.ArtistsJson);
        }

        return item;
    }

    private BaseItem Map(MediaItem row, bool includeSeries)
    {
        BaseItem item = row.Kind switch
        {
            BaseItemKind.Episode => new Episode(),
            BaseItemKind.Movie => new Movie(),
            BaseItemKind.Series => new Series(),
            BaseItemKind.MusicVideo => new MusicVideo(),
            BaseItemKind.Audio => new Audio(),
            BaseItemKind.Playlist => new Playlist(),
            BaseItemKind.Folder => new CollectionFolder { CollectionType = row.CollectionType },
            BaseItemKind.Season => new Season(),
            _ => new BaseItem()
        };

        item.Id = row.Id;
        item.Name = row.Name;
        item.SortName = row.SortName;
        item.Overview = row.Overview;
        item.Path = _remap.ResolveExistingPath(row.Path, row.SourceConnectionId) ?? row.Path;
        item.OfficialRating = row.OfficialRating;
        item.ProductionYear = row.ProductionYear;
        item.PremiereDate = row.PremiereDate;
        item.RunTimeTicks = row.RuntimeTicks;
        item.IndexNumber = row.IndexNumber;
        item.ParentIndexNumber = row.ParentIndexNumber;
        item.ParentId = row.ParentId ?? Guid.Empty;
        item.SeriesId = row.SeriesId ?? Guid.Empty;
        item.SeriesName = row.SeriesName;
        item.LibraryId = row.LibraryId;
        item.LibraryName = row.LibraryName;
        item.CollectionType = row.CollectionType;
        item.PrimaryImagePath = _remap.ResolveExistingPath(row.PrimaryImagePath, row.SourceConnectionId) ?? row.PrimaryImagePath;
        item.Width = row.Width;
        item.Height = row.Height;
        item.TrueAspectRatio = row.TrueAspectRatio;
        item.AspectRatio = row.AspectRatio;
        item.Tags = ReadStringArray(row.TagsJson);
        item.Genres = ReadStringArray(row.GenresJson);
        item.Studios = ReadStringArray(row.StudiosJson);
        item.CollectionNames = ReadStringArray(row.CollectionNamesJson);
        item.Kind = row.Kind;

        if (item is MusicVideo mappedVideo)
        {
            mappedVideo.Artists = ReadStringArray(row.ArtistsJson);
        }

        if (includeSeries && item is Episode episode && row.SeriesId is Guid seriesId && seriesId != Guid.Empty)
        {
            var seriesRow = _db.TvShows.AsNoTracking().FirstOrDefault(tv => tv.Id == seriesId)
                ?? (CatalogMediaRow?)null;
            if (seriesRow is TvShowRow series)
            {
                episode.Series = Map(series, BaseItemKind.Series) as Series;
            }
            else
            {
                var mediaSeries = _db.MediaItems.AsNoTracking().FirstOrDefault(media => media.Id == seriesId);
                if (mediaSeries is not null)
                {
                    episode.Series = Map(mediaSeries, includeSeries: false) as Series;
                }
            }
        }

        return item;
    }

    private static List<ChapterInfo> ParseChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChapterDto>>(json)?
                .Select(chapter => new ChapterInfo
                {
                    StartPositionTicks = chapter.StartPositionTicks,
                    Name = chapter.Name
                })
                .ToList()
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string[] ReadStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed class ChapterDto
    {
        public long StartPositionTicks { get; set; }

        public string? Name { get; set; }
    }
}
