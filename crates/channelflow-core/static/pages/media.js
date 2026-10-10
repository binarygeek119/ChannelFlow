// Media page: what every media source synced, read from ChannelFlow's own
// catalog. Each kind tab lists its source libraries the way Jellyfin's web UI
// shows a library — a header naming the library, then a responsive grid of
// poster cards — rather than a flat list.

const MEDIA_TABS = [
  { key: "movies", label: "Movies", kinds: ["movie"], shape: "portrait" },
  { key: "tvshows", label: "TV Shows", kinds: ["series"], shape: "portrait" },
  { key: "music", label: "Music", kinds: ["album", "artist"], shape: "square" },
  { key: "musicvideos", label: "Music Videos", kinds: ["musicvideo"], shape: "portrait" },
];
let mediaItems = [];
let mediaCounts = { movies: 0, tvshows: 0, music: 0, musicvideos: 0 };
let mediaConnections = [];
let mediaPageKey = "movies";

async function loadMedia() {
  renderMediaTabs();
  try {
    const data = await request("/api/media");
    mediaItems = data.items || [];
    mediaCounts = data.counts || mediaCounts;
    mediaConnections = (await request("/api/connections")).connections || [];
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
    button.addEventListener("click", () => renderMediaPage(key));
    tabs.appendChild(button);
  };
  MEDIA_TABS.forEach((tab) => add(tab.key, tab.label));
  tabs.querySelectorAll(".inner-tab").forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.mediaPage === mediaPageKey);
  });
}

function connectionNameFor(id) {
  const row = mediaConnections.find((connection) => connection.id === id);
  return (row && row.config && row.config.name) || `connection ${id}`;
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

// A Jellyfin-style poster card: a fixed-ratio poster with a hover dim + play
// badge, and the title/year beneath.
function mediaCard(item, shape) {
  const card = document.createElement("div");
  card.className = "card jf-card";
  card.title = item.title + (item.year ? ` (${item.year})` : "");
  const poster = mediaPosterUrl(item.poster_path);
  const image = poster
    ? `<div class="cardImage" style="background-image:url('${escapeHtml(poster)}')"></div>`
    : `<div class="cardImage cardImage-fallback"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><circle cx="10" cy="10" r="2"/><path d="M4 18l4.5-4.5 3 3L16 12l4 4"/></svg></div>`;
  const secondary = item.year ? String(item.year) : mediaKindLabel(item.kind);
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
  // One Jellyfin-style library section per source library.
  const byLibrary = new Map();
  items.forEach((item) => {
    const groupKey = `${item.connection_id}::${item.library || ""}`;
    if (!byLibrary.has(groupKey)) byLibrary.set(groupKey, { items: [], library: item.library, connection_id: item.connection_id });
    byLibrary.get(groupKey).items.push(item);
  });
  for (const group of byLibrary.values()) {
    const section = document.createElement("section");
    section.className = "jf-library";
    const source = connectionNameFor(group.connection_id);
    const heading = group.library ? escapeHtml(group.library) : "Library";
    section.innerHTML =
      `<div class="jf-library-head">` +
        `<h3>${heading}</h3>` +
        `<span class="jf-library-meta">${escapeHtml(source)} · ${group.items.length} item(s)</span>` +
      `</div>`;
    const grid = document.createElement("div");
    grid.className = "itemsContainer vertical-wrap";
    group.items
      .sort((a, b) => String(a.title).localeCompare(String(b.title)))
      .forEach((item) => grid.appendChild(mediaCard(item, spec.shape)));
    section.appendChild(grid);
    host.appendChild(section);
  }
}

CF.define("media", { onShow: loadMedia });
