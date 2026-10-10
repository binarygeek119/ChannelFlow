// Media page: what every media source synced, read from ChannelFlow's own
// catalog. The catalog is one entry per media, so a film that is in both
// Jellyfin and Plex is one card showing both sources. Each kind tab lists its
// items the way Jellyfin's web UI shows a library — a responsive poster grid.

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

let mediaItems = [];
let mediaCounts = { movies: 0, tvshows: 0, music: 0, musicvideos: 0 };
let mediaPageKey = "movies";

function mediaTabFromPath(path) {
  const match = path.match(/^\/webui\/media\/([^/]+)\/?$/);
  if (!match) return null;
  return MEDIA_TABS.some((tab) => tab.key === match[1]) ? match[1] : null;
}

async function loadMedia() {
  // Each kind tab is its own URL (/webui/media/movies, /webui/media/tvshows…).
  mediaPageKey = mediaTabFromPath(location.pathname) || "movies";
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

// The distinct sources providing an item, as human labels.
function mediaSourceLabels(item) {
  const kinds = [...new Set((item.sources || []).map((source) => source.source_kind))];
  return kinds.map((kind) => MEDIA_SOURCE_LABELS[kind] || kind);
}

// A Jellyfin-style poster card: a fixed-ratio poster with a hover dim + play
// badge, the title/year beneath, and a badge per source that provides it.
function mediaCard(item, shape) {
  const card = document.createElement("div");
  card.className = "card jf-card";
  card.title = item.title + (item.year ? ` (${item.year})` : "");
  const poster = mediaPosterUrl(item.poster_path);
  const image = poster
    ? `<div class="cardImage" style="background-image:url('${escapeHtml(poster)}')"></div>`
    : `<div class="cardImage cardImage-fallback"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><circle cx="10" cy="10" r="2"/><path d="M4 18l4.5-4.5 3 3L16 12l4 4"/></svg></div>`;
  const secondary = item.year ? String(item.year) : mediaKindLabel(item.kind);
  const sources = mediaSourceLabels(item);
  const badges = sources.length
    ? `<div class="media-sources">${sources
        .map((label) => `<span class="media-source-badge">${escapeHtml(label)}</span>`)
        .join("")}</div>`
    : "";
  card.innerHTML =
    `<div class="cardBox">` +
      `<div class="cardScalable">` +
        `<div class="cardPadder cardPadder-${shape}"></div>` +
        image +
        `<div class="cardOverlayContainer">` +
          `<div class="cardOverlayFab">` +
            `<svg viewBox="0 0 24 24" aria-hidden="true" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>` +
          `</div>` +
        `</div>` +
      `</div>` +
      `<div class="cardFooter">` +
        `<div class="cardText cardText-first">${escapeHtml(item.title)}</div>` +
        `<div class="cardText cardText-secondary">${escapeHtml(secondary)}</div>` +
        badges +
      `</div>` +
    `</div>`;
  return card;
}

function renderMediaPage(key) {
  mediaPageKey = key;
  renderMediaTabs();
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

CF.define("media", { onShow: loadMedia });
