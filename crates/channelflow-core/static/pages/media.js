const MEDIA_TABS = [
  { key: "movies", label: "Movies", kinds: ["movie"] },
  { key: "tvshows", label: "TV Shows", kinds: ["series"] },
  { key: "music", label: "Music", kinds: ["album", "artist"] },
  { key: "musicvideos", label: "Music Videos", kinds: ["musicvideo"] },
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
  const list = $(`media-list-${key}`);
  if (!list) return;
  list.textContent = "";
  const rows = mediaItems.filter((item) => spec.kinds.includes(item.kind));
  if (!rows.length) {
    list.appendChild(
      libraryCard(
        '<p class="hint">Nothing synced here yet — run a library scan on the Library tab and the items appear.</p>'
      )
    );
    return;
  }
  // One box per library, copying the Library page's group-per-connection look.
  const byLibrary = new Map();
  rows.forEach((item) => {
    const groupKey = `${item.connection_id}::${item.library || ""}`;
    if (!byLibrary.has(groupKey)) byLibrary.set(groupKey, { ...item, items: [] });
    byLibrary.get(groupKey).items.push(item);
  });
  for (const [groupKey, group] of byLibrary) {
    const box = document.createElement("div");
    box.className = "library-box";
    const host = connectionNameFor(group.connection_id);
    const libraryName = escapeHtml(group.library || "Library");
    const head = document.createElement("div");
    head.className = "library-box-head";
    head.innerHTML = `<h3>${escapeHtml(host)} · ${libraryName}</h3><span class="count">${group.items.length} item(s)</span>`;
    box.appendChild(head);
    group.items.forEach((item) => {
      const row = document.createElement("div");
      row.className = "media-row";
      const poster = mediaPosterUrl(item.poster_path);
      const thumb = poster
        ? `<img class="media-thumb" src="${escapeHtml(poster)}" alt="" loading="lazy">`
        : '<div class="media-thumb media-thumb-missing"></div>';
      const year = item.year ? ` <span class="media-year">(${escapeHtml(String(item.year))})</span>` : "";
      const sub = item.kind === "artist"
        ? '<span class="media-sub">Artist</span>'
        : item.kind === "album"
          ? '<span class="media-sub">Album</span>'
          : "";
      row.innerHTML = `${thumb}<div class="media-info"><div class="media-title">${escapeHtml(item.title)}${year}</div>${sub}</div>`;
      box.appendChild(row);
    });
    list.appendChild(box);
  }
}

// --- task progress popup ----------------------------------------------------
// A bottom-right popup tracks a running task. It stays until the work is done,
// holds briefly green (or red on failure), then fades away. It lives outside
// the tab panels, so switching pages never hides it.


CF.define("media", { onShow: loadMedia });
