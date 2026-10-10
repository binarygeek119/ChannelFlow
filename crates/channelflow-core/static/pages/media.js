// Media page: what every media source synced, read from ChannelFlow's own
// catalog. The catalog is one entry per media, so a film that is in both
// Jellyfin and Plex is one card showing both sources. Each kind tab lists its
// items the way Jellyfin's web UI shows a library — a responsive poster grid.
//
// Clicking a card's play button jumps straight to that item on the media
// server that hosts it; clicking the card itself opens a Jellyfin-style item
// page (/webui/media/item/<match_key>).

const MEDIA_TABS = [
  { key: "movies", label: "Movies", kinds: ["movie"], shape: "portrait" },
  { key: "tvshows", label: "TV Shows", kinds: ["series"], shape: "portrait" },
  { key: "music", label: "Music", kinds: ["album", "artist"], shape: "portrait" },
  { key: "musicvideos", label: "Music Videos", kinds: ["musicvideo"], shape: "portrait" },
];
const MEDIA_SOURCE_LABELS = {
  jellyfin: "Jellyfin",
  plex: "Plex",
  emby: "Emby",
  local: "Local",
};
// Logo images served from /logos/<name>; these replace the source pills.
const MEDIA_SOURCE_LOGOS = {
  jellyfin: "/logos/Jellyfin.png",
  plex: "/logos/Plex.png",
  emby: "/logos/Emby.png",
  local: "/logos/LOCAL.png",
};
// The plugin that hosts rich metadata for a source kind (item detail pages).
const MEDIA_DETAIL_PLUGINS = {
  jellyfin: "com.channelflow.jellyfin",
};

let mediaItems = [];
let mediaCounts = { movies: 0, tvshows: 0, music: 0, musicvideos: 0 };
let mediaPageKey = "movies";

function mediaTabFromPath(path) {
  const match = path.match(/^\/webui\/media\/([^/]+)\/?$/);
  if (!match) return null;
  return MEDIA_TABS.some((tab) => tab.key === match[1]) ? match[1] : null;
}

function mediaItemKeyFromPath(path) {
  const match = path.match(/^\/webui\/media\/item\/(.+)$/);
  return match ? decodeURIComponent(match[1]) : null;
}

function mediaTabForKind(kind) {
  const tab = MEDIA_TABS.find((entry) => entry.kinds.includes(kind));
  return tab ? tab.key : "movies";
}

async function loadMedia() {
  const itemKey = mediaItemKeyFromPath(location.pathname);
  if (itemKey) {
    await renderMediaItem(itemKey);
    return;
  }
  mediaPageKey = mediaTabFromPath(location.pathname) || "movies";
  showMediaLibrary();
  renderMediaTabs();
  try {
    const data = await request("/api/media");
    mediaItems = data.items || [];
    mediaCounts = data.counts || mediaCounts;
  } catch (error) {
    mediaItems = [];
    mediaCounts = { movies: 0, tvshows: 0, music: 0, musicvideos: 0 };
  }
  renderMediaPage(mediaPageKey);
}

// The kind-tab grid is visible; the item-detail panel is not.
function showMediaLibrary() {
  const detail = $("media-detail");
  if (detail) detail.hidden = true;
  const tabs = $("media-inner-tabs");
  if (tabs && tabs.closest(".panel")) tabs.closest(".panel").hidden = false;
  document.querySelectorAll("#tab-media .library-page").forEach((page) => {
    page.hidden = page.id !== `media-page-${mediaPageKey}`;
  });
}

function renderMediaTabs() {
  const tabs = $("media-inner-tabs");
  if (!tabs) return;
  tabs.textContent = "";
  const add = (key, label) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "inner-tab";
    button.dataset.mediaPage = key;
    button.textContent = label;
    button.addEventListener("click", () => {
      history.pushState(null, "", `/webui/media/${key}`);
      mediaPageKey = key;
      showMediaLibrary();
      renderMediaTabs();
      renderMediaPage(key);
    });
    tabs.appendChild(button);
  };
  MEDIA_TABS.forEach((tab) => add(tab.key, tab.label));
  tabs.querySelectorAll(".inner-tab").forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.mediaPage === mediaPageKey);
  });
}

function mediaPosterUrl(path) {
  if (!path) return null;
  return `/api/media/image?path=${encodeURIComponent(path)}`;
}

function mediaKindLabel(kind) {
  return (
    { movie: "Movie", series: "Series", album: "Album", artist: "Artist", musicvideo: "Music video" }[kind] ||
    kind
  );
}

// The distinct sources providing an item. Each is a tiny logo (with the
// human-readable name as tooltip/alt); an unknown source falls back to a pill.
function mediaSourceBadges(item) {
  const kinds = [...new Set((item.sources || []).map((source) => source.source_kind))];
  return kinds
    .map((kind) => {
      const label = MEDIA_SOURCE_LABELS[kind] || kind;
      const logo = MEDIA_SOURCE_LOGOS[kind];
      if (logo) {
        return `<img class="media-source-logo" src="${logo}" alt="${escapeHtml(label)}" title="${escapeHtml(label)}" loading="lazy">`;
      }
      return `<span class="media-source-badge">${escapeHtml(label)}</span>`;
    })
    .join("");
}

// A Jellyfin-style poster card: a fixed-ratio poster with a hover dim + play
// badge, the title/year beneath, and a badge per source that provides it.
// The play badge deep-links into the hosting media server; the rest of the
// card opens the item's detail page.
function mediaCard(item, shape) {
  const card = document.createElement("div");
  card.className = "card jf-card";
  card.title = item.title + (item.year ? ` (${item.year})` : "");
  card.tabIndex = 0;
  card.setAttribute("role", "button");
  const poster = mediaPosterUrl(item.poster_path);
  const image = poster
    ? `<div class="cardImage" style="background-image:url('${escapeHtml(poster)}')"></div>`
    : `<div class="cardImage cardImage-fallback"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><circle cx="10" cy="10" r="2"/><path d="M4 18l4.5-4.5 3 3L16 12l4 4"/></svg></div>`;
  const secondary = item.year ? String(item.year) : mediaKindLabel(item.kind);
  const badges = (item.sources || []).length
    ? `<div class="media-sources">${mediaSourceBadges(item)}</div>`
    : "";
  const playSource = (item.sources || []).find((source) => source.web_url);
  const fab = playSource
    ? `<a class="cardOverlayFab" href="${escapeHtml(playSource.web_url)}" target="_blank" rel="noopener" ` +
      `aria-label="Play on ${escapeHtml(playSource.source_kind)}" ` +
      `title="Play on ${escapeHtml(playSource.source_kind)}">` +
      `<svg viewBox="0 0 24 24" aria-hidden="true" fill="currentColor"><path d="M8 5v14l11-7z"/></svg></a>`
    : `<div class="cardOverlayFab" aria-hidden="true"><svg viewBox="0 0 24 24" aria-hidden="true" fill="currentColor"><path d="M8 5v14l11-7z"/></svg></div>`;
  card.innerHTML =
    `<div class="cardBox">` +
      `<div class="cardScalable">` +
        `<div class="cardPadder cardPadder-${shape}"></div>` +
        image +
        `<div class="cardOverlayContainer">` +
          fab +
        `</div>` +
      `</div>` +
      `<div class="cardFooter">` +
        `<div class="cardText cardText-first">${escapeHtml(item.title)}</div>` +
        `<div class="cardText cardText-secondary">${escapeHtml(secondary)}</div>` +
        badges +
      `</div>` +
    `</div>`;
  const openDetail = () => openMediaItem(item.match_key);
  card.addEventListener("click", (event) => {
    if (event.target.closest("a.cardOverlayFab")) return; // the play badge
    openDetail();
  });
  card.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      if (event.target.closest("a.cardOverlayFab")) return;
      openDetail();
    }
  });
  return card;
}

function renderMediaPage(key) {
  mediaPageKey = key;
  document.querySelectorAll("#tab-media .library-page").forEach((page) => {
    page.hidden = page.id !== `media-page-${key}`;
  });
  const spec = MEDIA_TABS.find((tab) => tab.key === key);
  if (!spec) return;
  const count = $(`media-count-${key}`);
  if (count) count.textContent = `${mediaCounts[key] || 0} item(s)`;
  const host = $(`media-list-${key}`);
  if (!host) return;
  host.textContent = "";
  const items = mediaItems.filter((item) => spec.kinds.includes(item.kind));
  if (!items.length) {
    host.appendChild(
      libraryCard(
        '<p class="hint">Nothing synced here yet — run a library scan on the Library tab and the items appear.</p>'
      )
    );
    return;
  }
  const grid = document.createElement("div");
  grid.className = "itemsContainer vertical-wrap";
  items
    .sort((a, b) => String(a.title).localeCompare(String(b.title)))
    .forEach((item) => grid.appendChild(mediaCard(item, spec.shape)));
  host.appendChild(grid);
}

// ── Item detail (Jellyfin-style) ──────────────────────────────────────────

function openMediaItem(matchKey) {
  history.pushState(null, "", `/webui/media/item/${encodeURIComponent(matchKey)}`);
  renderMediaItem(matchKey);
}

async function renderMediaItem(matchKey) {
  const detail = $("media-detail");
  if (detail) {
    detail.hidden = false;
    detail.textContent = "";
    detail.innerHTML = '<p class="hint">Loading…</p>';
  }
  // The item page replaces the tab's library grid, not just the shared shell:
  // hide every kind-tab page so the detail isn't stranded below a populated
  // grid (which made a click look like the page never opened until refresh).
  document.querySelectorAll("#tab-media .library-page").forEach((page) => {
    page.hidden = true;
  });
  const tabs = $("media-inner-tabs");
  if (tabs && tabs.closest(".panel")) tabs.closest(".panel").hidden = true;
  // Directly-opened item URLs may lack the catalog the tabs loaded.
  if (!mediaItems.length) {
    try {
      const data = await request("/api/media");
      mediaItems = data.items || [];
    } catch (error) {
      mediaItems = [];
    }
  }
  let item = null;
  try {
    const data = await request(`/api/media/${encodeURIComponent(matchKey)}`);
    item = data.item || null;
  } catch (error) {
    item = null;
  }
  if (!item) {
    if (detail) detail.innerHTML = "";
    if (detail) detail.appendChild(mediaDetailBackBar(""));
    if (detail) detail.appendChild(
      libraryCard('<p class="hint bad">That item isn\u2019t in the catalog any more.</p>')
    );
    return;
  }
  const enriched = await fetchItemDetail(item);
  renderMediaDetail(detail, item, enriched);
  // Music gets its own lower sections: an artist lists their albums, an
  // album lists its tracks.
  await renderMusicDetail(detail, item);
  mediaPageKey = mediaTabForKind(item.kind);
}

// Ask the hosting plugin for the rich row (genres, runtime, ratings, cast)
// when it offers an item-detail endpoint.
async function fetchItemDetail(item) {
  const source = (item.sources || []).find(
    (entry) => entry.source_kind && MEDIA_DETAIL_PLUGINS[entry.source_kind] && entry.remote_id
  );
  if (!source) return null;
  const pluginId = MEDIA_DETAIL_PLUGINS[source.source_kind];
  try {
    const path = `/api/plugins/${pluginId}/item/${encodeURIComponent(source.remote_id)}/detail`;
    const data = await request(path);
    return data.item || null;
  } catch (error) {
    return null;
  }
}

function formatRuntime(ticks) {
  if (!ticks) return null;
  const minutes = Math.round(ticks / 10_000_000 / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? `${hours}h ${rest}m` : `${hours}h`;
}

function mediaDetailBackBar(kind) {
  const tab = mediaTabForKind(kind);
  const label = (MEDIA_TABS.find((entry) => entry.key === tab) || {}).label || "Media";
  const button = document.createElement("button");
  button.type = "button";
  button.className = "media-back";
  button.textContent = `← ${label}`;
  button.addEventListener("click", () => {
    history.pushState(null, "", `/webui/media/${tab}`);
    loadMedia();
  });
  return button;
}

function renderMediaDetail(detail, item, enriched) {
  if (!detail) return;
  detail.textContent = "";
  detail.appendChild(mediaDetailBackBar(item.kind));

  const poster = mediaPosterUrl(item.poster_path);
  // A music video's stored image is a frame of the actual video, so it is
  // shown as a wide "screenshot" instead of a portrait poster.
  const isVideo = item.kind === "musicvideo";
  const videoPreview =
    isVideo && poster
      ? `<div class="media-detail-video" style="background-image:url('${escapeHtml(poster)}')"></div>`
      : "";
  const posterEl =
    !isVideo && poster
      ? `<div class="media-detail-poster" style="background-image:url('${escapeHtml(poster)}')"></div>`
      : `<div class="media-detail-poster media-detail-poster-fallback"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><circle cx="10" cy="10" r="2"/><path d="M4 18l4.5-4.5 3 3L16 12l4 4"/></svg></div>`;

  // Metadata chips: year • runtime • rating • genres • studios • official rating.
  const meta = [];
  if (item.year) meta.push(String(item.year));
  const runtime = formatRuntime(enriched && enriched.runtime_ticks);
  if (runtime) meta.push(runtime);
  if (enriched) {
    if (enriched.genres && enriched.genres.length) meta.push(enriched.genres.join(" · "));
    if (typeof enriched.community_rating === "number" && enriched.community_rating > 0) {
      meta.push(`★ ${Math.round(enriched.community_rating * 10)}%`);
    }
    if (enriched.official_rating) meta.push(enriched.official_rating);
    if (enriched.studios && enriched.studios.length) meta.push(enriched.studios.join(" · "));
  }

  const sources = item.sources || [];
  const playable = sources.filter((source) => source.web_url);
  const actions = playable.length
    ? `<div class="media-detail-actions">` +
      playable
        .map((source) => {
          const label = MEDIA_SOURCE_LABELS[source.source_kind] || source.source_kind;
          return `<a class="primary media-play" href="${escapeHtml(source.web_url)}" target="_blank" rel="noopener">` +
                 `▶ Play${playable.length > 1 ? ` on ${escapeHtml(label)}` : ""}</a>`;
        })
        .join("") +
      `</div>`
    : "";

  const overview = item.overview || (enriched && enriched.overview) || "";
  const overviewEl = overview
    ? `<p class="media-detail-overview">${escapeHtml(overview)}</p>`
    : "";

  detail.innerHTML =
    videoPreview +
    `<div class="media-detail-main">` +
      posterEl +
      `<div class="media-detail-info">` +
        `<h1>${escapeHtml(item.title)}</h1>` +
        `<div class="media-detail-sub">${escapeHtml(mediaKindLabel(item.kind))}</div>` +
        (meta.length ? `<div class="media-detail-meta">${meta.map((chip) => `<span class="media-detail-chip">${escapeHtml(chip)}</span>`).join("")}</div>` : "") +
        actions +
        overviewEl +
        (sources.length ? `<div class="media-sources">${mediaSourceBadges(item)}</div>` : "") +
      `</div>` +
    `</div>`;

  const people = (enriched && enriched.people) || [];
  if (people.length) {
    const heading = document.createElement("h2");
    heading.className = "media-detail-section";
    heading.textContent = "Cast";
    detail.appendChild(heading);
    const cast = document.createElement("div");
    cast.className = "media-cast";
    people.forEach((person) => cast.appendChild(mediaCastCard(person)));
    detail.appendChild(cast);
  }
}

function mediaCastCard(person) {
  const card = document.createElement("div");
  card.className = "media-cast-card";
  const name = person.name || "";
  const initials = name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((word) => (word[0] || "").toUpperCase())
    .join("");
  const url = person.image_path
    ? `/api/media/image?path=${encodeURIComponent(person.image_path)}`
    : null;
  const photo = url
    ? `<img class="media-cast-photo" src="${escapeHtml(url)}" alt="${escapeHtml(name)}" loading="lazy" onerror="this.remove(); this.closest('.media-cast-photo-frame').querySelector('.media-cast-initial').style.display='flex';">`
    : "";
  card.innerHTML =
    `<div class="media-cast-photo-frame">` +
      `<span class="media-cast-initial">${escapeHtml(initials)}</span>` +
      photo +
    `</div>` +
    `<div class="media-cast-name" title="${escapeHtml(name)}">${escapeHtml(name)}</div>` +
    (person.character ? `<div class="media-cast-role">${escapeHtml(person.character)}</div>` : "");
  // Clicking a cast member opens their page (the People tab) listing every
  // title they appear in.
  if (name) {
    card.tabIndex = 0;
    card.setAttribute("role", "button");
    card.title = `See everything with ${name}`;
    const open = () => {
      history.pushState(null, "", `/webui/people/${encodeURIComponent(name)}`);
      showTab("people");
    };
    card.addEventListener("click", open);
    card.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        open();
      }
    });
  }
  return card;
}

// ── Music: artist → albums, album → tracks ───────────────────────────────

const MUSIC_GRAPH = "/api/plugins/com.channelflow.jellyfin/music";
let musicArtists = null;
let musicAlbums = null;
let musicVideos = null;

// "a", "the" and punctuation are ignored for matching so the base catalog's
// titles line up with the plugin's hierarchy rows.
function normalizeMusicName(name) {
  return String(name)
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]/gu, "")
    .replace(/^(a|an|the)/, "");
}

async function loadMusicGraph() {
  if (musicArtists && musicAlbums && musicVideos) return;
  try {
    const [artists, albums, videos] = await Promise.all([
      request(`${MUSIC_GRAPH}/artists`),
      request(`${MUSIC_GRAPH}/albums`),
      request(`${MUSIC_GRAPH}/videos`),
    ]);
    musicArtists = artists.artists || [];
    musicAlbums = albums.albums || [];
    musicVideos = videos.videos || [];
  } catch (error) {
    musicArtists = [];
    musicAlbums = [];
    musicVideos = [];
  }
}

// The plugin albums belonging to the artist named `name`.
function albumsForArtist(name) {
  const wanted = normalizeMusicName(name);
  const artist =
    (musicArtists || []).find((entry) => normalizeMusicName(entry.name) === wanted) ||
    (musicArtists || []).find((entry) => {
      const candidate = normalizeMusicName(entry.name);
      return candidate && (candidate.includes(wanted) || wanted.includes(candidate));
    });
  if (!artist) return [];
  return (musicAlbums || []).filter((album) => album.artist_id === artist.id);
}

// The plugin album row matching a title (+year), for its track list.
function findGraphAlbum(title, year) {
  const wanted = normalizeMusicName(title);
  return (musicAlbums || []).find(
    (album) =>
      normalizeMusicName(album.title) === wanted &&
      (year == null || !album.year || String(album.year) === String(year))
  );
}

// The base-catalog album matching a title (+year), for its poster and link.
function findBaseAlbum(title, year) {
  const wanted = normalizeMusicName(title);
  return mediaItems.find(
    (item) =>
      item.kind === "album" &&
      normalizeMusicName(item.title) === wanted &&
      (year == null || !item.year || String(item.year) === String(year))
  );
}

function formatTrackTime(ticks) {
  if (!ticks) return null;
  const seconds = Math.round(ticks / 10_000_000);
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, "0")}`;
}

function mediaAlbumCard(album, baseAlbum) {
  const card = document.createElement("div");
  const clickable = Boolean(baseAlbum && baseAlbum.match_key);
  card.className = "media-album-card" + (clickable ? "" : " unlinked");
  const poster = baseAlbum && baseAlbum.poster_path
    ? mediaPosterUrl(baseAlbum.poster_path)
    : null;
  const image = poster
    ? `<div class="media-album-poster" style="background-image:url('${escapeHtml(poster)}')"></div>`
    : `<div class="media-album-poster media-album-poster-fallback"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M9 9h6M9 13h6M9 17h4"/></svg></div>`;
  card.innerHTML =
    image +
    `<div class="media-album-title" title="${escapeHtml(album.title || "")}">${escapeHtml(album.title || "")}</div>` +
    (album.year ? `<div class="media-album-sub">${escapeHtml(String(album.year))}</div>` : "");
  if (clickable) {
    card.tabIndex = 0;
    card.setAttribute("role", "button");
    const go = () => openMediaItem(baseAlbum.match_key);
    card.addEventListener("click", go);
    card.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        go();
      }
    });
  }
  return card;
}

function mediaTrackRow(track, index) {
  const row = document.createElement("div");
  row.className = "media-track-row";
  const number =
    track.disc_number && track.disc_number > 1
      ? `${track.disc_number}.${track.track_number || ""}`
      : String(track.track_number || index + 1);
  const time = formatTrackTime(track.runtime_ticks);
  row.innerHTML =
    `<div class="media-track-num">${escapeHtml(number)}</div>` +
    `<div class="media-track-title" title="${escapeHtml(track.title || "")}">${escapeHtml(track.title || "")}</div>` +
    (time ? `<div class="media-track-time">${escapeHtml(time)}</div>` : "");
  return row;
}

// The plugin video row matching a title (+year).
function findGraphVideo(title, year) {
  const wanted = normalizeMusicName(title);
  return (musicVideos || []).find(
    (video) =>
      normalizeMusicName(video.title) === wanted &&
      (year == null || !video.year || String(video.year) === String(year))
  );
}

// The base-catalog music video matching a title (+year), for its poster/link.
function findBaseVideo(title, year) {
  const wanted = normalizeMusicName(title);
  return mediaItems.find(
    (item) =>
      item.kind === "musicvideo" &&
      normalizeMusicName(item.title) === wanted &&
      (year == null || !item.year || String(item.year) === String(year))
  );
}

// The plugin videos belonging to an artist (by the plugin artist id).
function videosForArtistId(artistId, excludeId) {
  return (musicVideos || []).filter(
    (video) => video.artist_id === artistId && video.id !== excludeId
  );
}

function musicVideoCard(video, baseVideo) {
  const card = document.createElement("div");
  const clickable = Boolean(baseVideo && baseVideo.match_key);
  card.className = "media-video-card" + (clickable ? "" : " unlinked");
  const poster = baseVideo && baseVideo.poster_path
    ? mediaPosterUrl(baseVideo.poster_path)
    : null;
  const image = poster
    ? `<div class="media-video-poster" style="background-image:url('${escapeHtml(poster)}')"></div>`
    : `<div class="media-video-poster media-video-poster-fallback"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><circle cx="10" cy="10" r="2"/><path d="M4 18l4.5-4.5 3 3L16 12l4 4"/></svg></div>`;
  card.innerHTML =
    image +
    `<div class="media-video-title" title="${escapeHtml(video.title || "")}">${escapeHtml(video.title || "")}</div>` +
    (video.year ? `<div class="media-video-sub">${escapeHtml(String(video.year))}</div>` : "");
  if (clickable) {
    card.tabIndex = 0;
    card.setAttribute("role", "button");
    const go = () => openMediaItem(baseVideo.match_key);
    card.addEventListener("click", go);
    card.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        go();
      }
    });
  }
  return card;
}

async function renderMusicDetail(detail, item) {
  if (!detail || (item.kind !== "artist" && item.kind !== "album" && item.kind !== "musicvideo")) return;
  await loadMusicGraph();

  if (item.kind === "artist") {
    const albums = albumsForArtist(item.title);
    if (albums.length) {
      const heading = document.createElement("h2");
      heading.className = "media-detail-section";
      heading.textContent = "Albums";
      detail.appendChild(heading);
      const grid = document.createElement("div");
      grid.className = "media-albums";
      albums.forEach((album) => grid.appendChild(mediaAlbumCard(album, findBaseAlbum(album.title, album.year))));
      detail.appendChild(grid);
    }
    // The artist's music videos (geared for video files, like the albums).
    let artist = (musicArtists || []).find((entry) => normalizeMusicName(entry.name) === normalizeMusicName(item.title));
    if (!artist) {
      artist = (musicArtists || []).find((entry) => {
        const candidate = normalizeMusicName(entry.name);
        return candidate && (candidate.includes(normalizeMusicName(item.title)) || normalizeMusicName(item.title).includes(candidate));
      });
    }
    if (artist) {
      const videos = videosForArtistId(artist.id);
      if (videos.length) {
        const heading = document.createElement("h2");
        heading.className = "media-detail-section";
        heading.textContent = "Videos";
        detail.appendChild(heading);
        const grid = document.createElement("div");
        grid.className = "media-videos";
        videos.forEach((video) => grid.appendChild(musicVideoCard(video, findBaseVideo(video.title, video.year))));
        detail.appendChild(grid);
      }
    }
    return;
  }

  if (item.kind === "album") {
    const album = findGraphAlbum(item.title, item.year);
    if (!album) return;
    let tracks = [];
    try {
      const data = await request(`${MUSIC_GRAPH}/albums/${album.id}/tracks`);
      tracks = data.tracks || [];
    } catch (error) {
      tracks = [];
    }
    if (!tracks.length) return;
    const heading = document.createElement("h2");
    heading.className = "media-detail-section";
    heading.textContent = `Tracks (${tracks.length})`;
    detail.appendChild(heading);
    const list = document.createElement("div");
    list.className = "media-tracks";
    tracks.forEach((track, index) => list.appendChild(mediaTrackRow(track, index)));
    detail.appendChild(list);
    return;
  }

  if (item.kind === "musicvideo") {
    const video = findGraphVideo(item.title, item.year);
    if (!video) return;
    // The video's artist (link to their page) and the artist's other videos.
    let artist = (musicArtists || []).find((entry) => entry.id === video.artist_id);
    if (!artist) {
      artist = (musicArtists || []).find((entry) => {
        const candidate = normalizeMusicName(entry.name);
        const wanted = normalizeMusicName(video.artist_name || "");
        return candidate && wanted && (candidate === wanted || candidate.includes(wanted) || wanted.includes(candidate));
      });
    }
    const baseArtist = artist ? findBaseArtist(artist.name) : null;
    if (baseArtist) {
      const link = document.createElement("div");
      link.className = "media-artist-link";
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = artist.name;
      button.addEventListener("click", () => openMediaItem(baseArtist.match_key));
      link.appendChild(document.createTextNode("Artist: "));
      link.appendChild(button);
      detail.appendChild(link);
    }
    const more = artist ? videosForArtistId(artist.id, video.id) : [];
    if (more.length) {
      const heading = document.createElement("h2");
      heading.className = "media-detail-section";
      heading.textContent = `More from ${escapeHtml(artist.name)}`;
      detail.appendChild(heading);
      const grid = document.createElement("div");
      grid.className = "media-videos";
      more.forEach((other) => grid.appendChild(musicVideoCard(other, findBaseVideo(other.title, other.year))));
      detail.appendChild(grid);
    }
  }
}

// The base-catalog artist matching a plugin artist name, for page links.
function findBaseArtist(name) {
  const wanted = normalizeMusicName(name);
  return mediaItems.find((item) => item.kind === "artist" && normalizeMusicName(item.title) === wanted);
}

CF.define("media", { onShow: loadMedia });