// ChannelFlow web shell: the shared runtime every page depends on. The drawer,
// topbar, login, walkthrough and the page router live here; each tab's own
// behaviour ships as pages/<name>.{html,css,js} and is loaded on first visit.

const $ = (id) => document.getElementById(id);

// Element map, resolved lazily. Pages inject their markup when first shown, so
// `els.x` is read through a Proxy that maps camelCase keys to the element's id
// (tabLiveTv -> #tab-live-tv) at access time rather than capturing a snapshot
// at boot.
const els = new Proxy({}, {
  get(_target, key) {
    if (typeof key !== "string") return undefined;
    const id = key
      .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
      .toLowerCase();
    return document.getElementById(id);
  },
});

function escapeHtml(value) {
  return String(value ?? "").replace(
    /[&<>"']/g,
    (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]
  );
}

async function request(path, options = {}) {
  // Cache-bust every GET so a stale cached answer can never decide what the
  // page shows — not least `/api/auth/state`, which chooses the walkthrough
  // vs the login screen. Queries other code builds are left alone.
  const method = String(options.method || "GET").toUpperCase();
  if (method === "GET" && !String(path).includes("?")) {
    path = `${path}?_=${Date.now()}`;
  }
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  // A 401 means the session is gone. Rather than leaving whatever page was
  // open looking empty, go back through boot() to the login screen. The auth
  // endpoints themselves are public, so this cannot loop.
  if (response.status === 401) {
    location.reload();
    throw new Error("your session ended — log in again");
  }
  if (response.status === 204) return null;
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    throw new Error(body && body.error ? body.error : `Request failed (${response.status})`);
  }
  return body;
}

// --- Menu (drawer entries) --------------------------------------------------

const MENU = {
  livetv: ["Live TV", "Watch your channels."],
  guide: ["TV Guide", "What's on now, and what's coming up across every channel."],
  general: ["General Settings", "Server-wide defaults for how ChannelFlow runs."],
  quickpin: ["Quick Pin", "Pair a ChannelFlow app by the PIN it shows."],
  clients: ["Clients", "Players that have connected and what they're watching."],
  channels: ["Channels", "Manage Live TV channels"],
  lineups: ["Lineups", "Group channels into playlists you can hand to a player."],
  presets: ["Presets", "Reusable scheduling rules you can drop onto any channel."],
  list: ["Lists", "Named lists of items you can reuse across channels and presets."],
  special: ["Special Presentation", "One-off scheduled events that override the normal lineup."],
  media: ["Media", "The movies, shows, albums and videos synced into ChannelFlow's own catalog."],
  commercials: ["Commercials", "Breaks, avails, and where they're allowed to land."],
  youtube: ["YouTube", "Videos pulled in from YouTube for use in breaks or blocks."],
  tasks: ["Tasks", "Scheduled jobs like library scans and cache cleanup."],
  plugins: ["Plugins", "Loaded plugins and what each is allowed to do."],
  about: ["About", "Version, build, and where this install keeps its data."],
  credits: ["Credits", "Who built ChannelFlow, and what it's built on."],
};

// Which menu key has its own panel in the markup. Kept for the shell's own
// panel-placement decisions; a missing entry falls back to `tab-<key>`.
const PANEL_FOR = {
  channels: "tab-channels",
  about: "tab-about",
  credits: "tab-credits",
  media: "tab-media",
  livetv: "tab-live-tv",
  plugins: "tab-plugins",
  tasks: "tab-tasks",
};

// Plugin page components this shell knows how to render, mapped to the panel
// id that hosts them. A plugin declares a `page` contribution in its
// manifest; the tab appears only while that plugin is installed.
const PLUGIN_PANELS = {
  AiPage: "tab-ai",
  OffAirPage: "tab-ebs",
  CommercialBrainzPage: "tab-commercialbrainz",
  EmergencyPage: "tab-emergency",
  WeatherPage: "tab-weather",
  NewsPage: "tab-news",
};

// The ids of plugin-declared pages added to the drawer. Tracked so a rebuild
// (after install or remove) can drop exactly those entries and re-add the
// current set instead of duplicating them.
const pluginPageKeys = new Set();

// Which plugin provides each plugin-declared page key. The shell loads that
// page's html/css/js from the plugin's own web root (`/plugin/{id}/web`),
// never from <config>/webui.
const pluginPageOwner = {};

// Plugin-declared pages add their own drawer entries. The catalog only lists
// installed plugins, so an AI plugin that is not installed leaves no AI tab —
// exactly the same rule that governs the connection types.
async function loadPluginPages() {
  document
    .querySelectorAll('.drawer-nav a[data-plugin-page]')
    .forEach((link) => link.remove());
  pluginPageKeys.forEach((key) => {
    delete MENU[key];
    delete PANEL_FOR[key];
    delete pluginPageOwner[key];
  });
  pluginPageKeys.clear();
  let plugins;
  try {
    plugins = (await request("/api/plugins")).plugins || [];
  } catch (error) {
    return;
  }
  const nav = document.getElementById("drawer-nav");
  if (!nav) return;
  const pageLinks = [];
  plugins.forEach((plugin) => {
    (plugin.ui_contributions || []).forEach((contribution) => {
      if (!contribution || contribution.type !== "page") return;
      const key = contribution.id;
      if (!key) return;
      if (MENU[key]) return; // a core page keeps precedence
      MENU[key] = [contribution.title || key, plugin.description || ""];
      PANEL_FOR[key] = PLUGIN_PANELS[contribution.component] || "tab-placeholder";
      pluginPageKeys.add(key);
      // This page's files live with the plugin that contributed it.
      pluginPageOwner[key] = plugin.id;
      const link = document.createElement("a");
      link.className = "nav-item";
      link.dataset.pluginPage = "1";
      link.dataset.tab = key;
      link.href = contribution.path || `/webui/${key}`;
      const icon =
        '<svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" ' +
        'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
        '<circle cx="12" cy="12" r="9"/><path d="M12 8v8M8 12h8"/></svg>';
      const label = document.createElement("span");
      label.className = "nav-label";
      label.textContent = contribution.title || key;
      link.innerHTML = icon;
      link.appendChild(label);
      link.addEventListener("click", (event) => {
        event.preventDefault();
        history.pushState(null, "", contribution.path || `/webui/${key}`);
        showTab(key);
      });
      pageLinks.push({
        link,
        title: label.textContent,
        order: Number(contribution.order) || 100,
        top: contribution.section === "top" || contribution.section === "main",
      });
    });
  });
  pageLinks.sort((a, b) => a.order - b.order || a.title.localeCompare(b.title));
  const firstGap = nav.querySelector(".drawer-nav-gap");
  for (const entry of pageLinks) {
    if (entry.top && firstGap) {
      nav.insertBefore(entry.link, firstGap);
    } else {
      const transcodeAnchor = nav.querySelector('[data-tab="transcode"]');
      if (transcodeAnchor) nav.insertBefore(entry.link, transcodeAnchor);
      else nav.appendChild(entry.link);
    }
  }
  const key = tabForPath(location.pathname);
  if (key) showTab(key);
}

// --- Page router -------------------------------------------------------------
// Each tab has its own URL (/webui/<tab>) and its own set of files under
// pages/: <name>.html is injected into the page area, <name>.css is linked
// once, and <name>.js is loaded once. Every page registers itself with
// `CF.define(key, { onShow })`; onShow runs on each visit so data is fresh.

// Which section id a routed tab lands in. Everything else is `tab-<key>`.
const PAGE_SECTION = {
  livetv: "tab-live-tv",
  jellyfin: "tab-library",
};

const UI_BUILD = "67";

// The page registry. Page scripts call `CF.define`.
const CF = {
  pages: {},
  define(key, definition) {
    CF.pages[key] = definition;
  },
};
window.CF = CF;

const loadedPageScripts = new Set();

async function loadPageAsset(key) {
  // A plugin-declared page hosts its files in the plugin's own folder
  // (/plugin/{pluginId}/web); base pages come from <config>/webui (/pages).
  const owner = pluginPageOwner[key];
  const base = owner ? `/plugin/${owner}/web` : "/pages";
  // CSS is a stylesheet, safe to link before the markup arrives.
  const linkId = "page-stylesheet-" + key;
  if (!$(`page-stylesheet-${key}`)) {
    const link = document.createElement("link");
    link.id = linkId;
    link.rel = "stylesheet";
    link.href = `${base}/${key}.css?v=${UI_BUILD}`;
    document.head.appendChild(link);
  }
  const sectionId = PAGE_SECTION[key] || `tab-${key}`;
  let section = $(sectionId);
  if (!section) {
    const html = await (await fetch(`${base}/${key}.html?v=${UI_BUILD}`, { cache: "no-store" })).text();
    section = document.createElement("section");
    section.id = sectionId;
    section.className = "tab-panel";
    section.hidden = true;
    section.innerHTML = html;
    $("page").appendChild(section);
  }
  // A page's script binds listeners to its own markup, so it has to load after
  // the HTML is in the document. It runs exactly once.
  if (!loadedPageScripts.has(key)) {
    loadedPageScripts.add(key);
    await new Promise((resolve) => {
      const script = document.createElement("script");
      script.src = `${base}/${key}.js?v=${UI_BUILD}`;
      script.onload = resolve;
      script.onerror = () => {
        console.warn(`could not load ${base}/${key}.js`);
        resolve();
      };
      document.head.appendChild(script);
    });
  }
  return section;
}

async function showTab(key) {
  const entry = MENU[key];
  if (!entry) return;

  // The Media tab's default view is the Movies kind tab; a bare /webui/media
  // URL (typed in, or a stale bookmark) gets normalized to /webui/media/movies.
  if (key === "media" && (location.pathname === "/webui/media" || location.pathname === "/webui/media/")) {
    history.replaceState(null, "", "/webui/media/movies");
  }

  document.querySelectorAll(".drawer-nav a").forEach((link) => {
    const active = link.dataset.tab === key;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });

  els.pageTitle.textContent = entry[0];
  els.pageSubtitle.textContent = entry[1];

  const section = await loadPageAsset(key);
  document.querySelectorAll("#page > section.tab-panel").forEach((element) => {
    element.hidden = element.id !== section.id;
  });
  section.hidden = false;

  const page = CF.pages[key];
  if (page && page.onShow) page.onShow();
}

function pathForTab(key) {
  for (const link of document.querySelectorAll(".drawer-nav a[data-tab]")) {
    if (link.dataset.tab === key) return link.getAttribute("href") || `/webui/${key}`;
  }
  return `/webui/${key}`;
}

function tabForPath(path) {
  if (path === "/webui/library" || path.startsWith("/webui/library/")) return "jellyfin";
  if (path === "/webui/media" || path.startsWith("/webui/media/")) return "media";
  let found = null;
  document.querySelectorAll(".drawer-nav a[data-tab]").forEach((link) => {
    if (link.getAttribute("href") === path) found = link.dataset.tab;
  });
  return found;
}

// A plain wrapper the Library and Media pages both use for a section card.
function libraryCard(html) {
  const card = document.createElement("div");
  card.className = "card section-card";
  card.innerHTML = html;
  return card;
}

// Shared status line (the Channels page's #status) and error note. Null-safe:
// a page with no such element is simply unaffected.
function setStatus(text, kind) {
  const el = els.status;
  if (!el) return;
  el.textContent = text;
  el.className = "status" + (kind ? " " + kind : "");
}

function showError(message) {
  const el = els.error;
  if (!el) return;
  el.textContent = message || "";
  el.hidden = !message;
}

// --- task progress popup ----------------------------------------------------
// A bottom-right popup tracks a running task. It stays until the work is done,
// holds briefly green (or red on failure), then fades away. It lives outside
// the tab panels, so switching pages never hides it.

const taskPopup = (() => {
  const wrap = document.getElementById("task-popup");
  const head = wrap.querySelector(".task-popup-head");
  const bar = wrap.querySelector(".task-popup-bar");
  const status = wrap.querySelector(".task-popup-status");
  const card = wrap.querySelector(".task-popup");
  let holdTimer = null;

  function show(title) {
    clearTimeout(holdTimer);
    wrap.hidden = false;
    wrap.classList.remove("fading");
    card.classList.remove("status-done", "status-fail");
    bar.className = "task-popup-bar indeterminate";
    head.textContent = title;
    status.textContent = "Running…";
  }

  function finish(text, ok) {
    clearTimeout(holdTimer);
    bar.className = ok ? "task-popup-bar done" : "task-popup-bar fail";
    card.classList.toggle("status-done", ok);
    card.classList.toggle("status-fail", !ok);
    status.textContent = text;
    holdTimer = setTimeout(() => {
      wrap.classList.add("fading");
      setTimeout(() => { wrap.hidden = true; }, 650);
    }, 2600);
  }

  function progress(text) {
    clearTimeout(holdTimer);
    bar.className = "task-popup-bar indeterminate";
    status.textContent = text;
  }

  function run(title, work, format) {
    show(title);
    Promise.resolve()
      .then(work)
      .then((result) => finish(format ? format(result) : "Done.", true))
      .catch((error) => finish(error.message || "Failed.", false));
  }

  return { show, progress, finish, run };
})();

// --- Drawer + history navigation --------------------------------------------

document.querySelectorAll(".drawer-nav a").forEach((link) => {
  link.addEventListener("click", (event) => {
    if (!link.dataset.tab) return;
    event.preventDefault();
    history.pushState(null, "", link.getAttribute("href") || `/webui/${link.dataset.tab}`);
    showTab(link.dataset.tab);
    link.scrollIntoView({ block: "nearest", inline: "nearest" });
  });
});

window.addEventListener("popstate", () => {
  const key = tabForPath(location.pathname);
  if (key) showTab(key);
});

// --- M3U / XMLTV share menus ------------------------------------------------
// Top-right shortcuts, on every page, that copy the playlist or guide URL for
// the instance's local or public address (General Settings) to the clipboard.

const SHARE_PATHS = { m3u: "/live/channels.m3u", xmltv: "/live/xmltv.xml" };

function shareUrlFor(kind, source, settings) {
  const raw = source === "public" ? settings.public_url : settings.local_url;
  const base = String(raw || "").trim().replace(/\/+$/, "");
  if (!base) return null;
  return base + (SHARE_PATHS[kind] || "");
}

function closeShareMenus() {
  document.querySelectorAll(".share-dropdown").forEach((menu) => {
    menu.hidden = true;
  });
}

// The Clipboard API needs a secure context, which plain http on a LAN is not,
// so fall back to the old selection trick rather than leave the copy dead.
async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch (error) {
    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    let ok = false;
    try {
      ok = document.execCommand("copy");
    } catch (inner) {
      ok = false;
    }
    area.remove();
    return ok;
  }
}

let toastTimer = null;
function showToast(message) {
  const toast = document.getElementById("toast");
  if (!toast) return;
  clearTimeout(toastTimer);
  toast.textContent = message;
  toast.hidden = false;
  toastTimer = setTimeout(() => {
    toast.hidden = true;
  }, 1800);
}

document.querySelectorAll(".share-menu").forEach((menu) => {
  const button = menu.querySelector(".share-button");
  const dropdown = menu.querySelector(".share-dropdown");
  const kind = menu.dataset.share;
  button.addEventListener("click", async (event) => {
    event.stopPropagation();
    const wasOpen = !dropdown.hidden;
    closeShareMenus();
    if (wasOpen) return;
    let settings = {};
    try {
      settings = (await request("/api/settings/general")).settings || {};
    } catch (error) {
      /* options fall back to disabled */
    }
    dropdown.querySelectorAll("button[data-source]").forEach((option) => {
      const source = option.dataset.source;
      const url = shareUrlFor(kind, source, settings);
      option.dataset.url = url || "";
      option.disabled = !url;
      option.title =
        url ||
        (source === "public"
          ? "Set a public URL in General Settings"
          : "Set a local URL in General Settings");
    });
    dropdown.hidden = false;
  });
  dropdown.querySelectorAll("button[data-source]").forEach((option) => {
    option.addEventListener("click", async (event) => {
      event.stopPropagation();
      const url = option.dataset.url;
      if (!url) return;
      const ok = await copyText(url);
      closeShareMenus();
      showToast(
        ok
          ? `Copied ${kind.toUpperCase()} ${option.dataset.source} URL`
          : "Copy failed — could not reach the clipboard."
      );
    });
  });
});

document.addEventListener("click", closeShareMenus);

// --- First boot: the walkthrough (and after that, the app itself) -----------
// boot() runs once on load. No setup yet → the walkthrough at /first-time;
// once setup completes the server auto-authenticates so the app opens
// directly — there is no login screen.

function hideScreens() {
  document.getElementById("login-screen").hidden = true;
  document.getElementById("onboarding-screen").hidden = true;
  document.getElementById("app-shell").hidden = true;
}

function showApp() {
  hideScreens();
  document.getElementById("app-shell").hidden = false;
  loadPluginPages();
  showTab(tabForPath(location.pathname) || "guide");
  // A task can outlive the page that started it; bring its popup back.
  watchRunningTask();
}

// If a background task is still running (a scan started before this page load),
// show the popup and keep it current until the task ends. No running task means
// no popup at all.
async function watchRunningTask() {
  let task = null;
  try {
    task = (await request("/api/tasks/running")).task;
  } catch (error) {
    return;
  }
  if (!task) return;
  taskPopup.show(task.title || "Task running");
  const progressUrl = task.progress_url;
  // Poll until the server says nothing is running any more.
  for (;;) {
    await new Promise((resolve) => setTimeout(resolve, 900));
    let current = null;
    try {
      current = (await request("/api/tasks/running")).task;
    } catch (error) {
      current = null;
    }
    if (!current) {
      taskPopup.finish("Finished.", true);
      return;
    }
    if (progressUrl) {
      try {
        const snapshot = (await request(progressUrl)).progress || {};
        taskPopup.progress(
          `${snapshot.label || current.title} · ` +
            `${Number(snapshot.current || 0).toLocaleString()} of ` +
            `${Number(snapshot.total || 0).toLocaleString()}`
        );
      } catch (error) {
        /* a transient poll failure must not hide a running task */
      }
    }
  }
}

function showLogin(note) {
  hideScreens();
  document.getElementById("login-screen").hidden = false;
  if (note) setLoginNote(note, true);
}

function setLoginNote(message, bad) {
  const note = document.getElementById("login-note");
  note.hidden = !message;
  note.textContent = message || "";
  note.classList.toggle("bad", !!bad);
}

const OB_STEPS = [
  "Welcome",
  "Plugins",
  "Database",
  "Transcoding",
  "Media source",
  "Addresses",
  "Account",
  "Done",
];
let obIndex = 0;
let obCompleted = {};

function setObNote(message, bad) {
  const note = document.getElementById("ob-note");
  note.hidden = !message;
  note.textContent = message || "";
  note.classList.toggle("bad", !!bad);
}

function renderObSteps() {
  document.getElementById("ob-steps").innerHTML = OB_STEPS.map((title, i) => {
    const cls = i < obIndex ? "done" : i === obIndex ? "current" : "";
    return `<span class="${cls}" title="${title}"></span>`;
  }).join("");
}

function refreshObNext() {
  const $next = document.getElementById("ob-next");
  if (obIndex === OB.length - 1) {
    $next.disabled = false;
    return;
  }
  $next.disabled = !(OB[obIndex].next ? OB[obIndex].next() : true);
}

const OB = [
  {
    next: () => true,
    body: () =>
      `<p>Welcome to <strong>ChannelFlow</strong> — a simulated live-TV server: real channels, a guide, and scheduled playout, driven by ErsatzTV next underneath. This short walkthrough installs the two plugins it needs and creates the account you will log in with.</p>`,
  },
  {
    next: () => true,
    body: () =>
      `<p>ChannelFlow is built from <strong>plugins</strong>. Each one brings a capability — the transcoding engine, a media source, AI and speech. Some are needed for the app to do anything useful, and this walkthrough installs them for you. After setup you can add and remove plugins from the <strong>Plugins</strong> page.</p>`,
  },
  {
    next: () => !!obCompleted["database"],
    body: () =>
      `<p>ChannelFlow keeps everything in <strong>Postgres</strong> when you give it one. Enter the server's connection string, and once it connects — the schema is created and any data in the config directory is carried over — you can continue.</p>
       <label class="field-label" for="ob-db-url">Postgres connection string</label>
       <input id="ob-db-url" type="text" autocomplete="off" spellcheck="false" placeholder="postgres://user:password@host:5432/channelflow">
       <div class="ob-action"><button type="button" class="primary" id="ob-do">Connect database</button></div>`,
    after: () => attachObDatabase(),
  },
  {
    next: () => !!obCompleted["com.channelflow.ersatztv"],
    body: () =>
      `<p>Your channels will be encoded and streamed by the <strong>ErsatzTV Transcoding Engine</strong>, which turns a channel into the HLS stream a player watches. Install it now:</p>
       <div class="ob-action"><button type="button" class="primary" id="ob-do">Install latest ErsatzTV Transcoding Engine</button></div>`,
    after: () =>
      attachObInstall("com.channelflow.ersatztv", "Install latest ErsatzTV Transcoding Engine"),
  },
  {
    next: () => !!obCompleted["com.channelflow.jellyfin"],
    body: () =>
      `<p>ChannelFlow pulls movies and shows from a <strong>media source</strong>. Install the <strong>Jellyfin media source</strong> now — it connects your Jellyfin library to your channels.</p>
       <div class="ob-action"><button type="button" class="primary" id="ob-do">Install the Jellyfin media source</button></div>`,
    after: () => attachObInstall("com.channelflow.jellyfin", "Install the Jellyfin media source"),
  },
  {
    next: () => true,
    body: () =>
      `<p>How should people reach ChannelFlow? We detected the <strong>local URL</strong> from the address you are using now — correct it if it is wrong. Set a <strong>public URL</strong> only if ChannelFlow is exposed beyond your network.</p>
       <label class="field-label" for="ob-local-url">Local URL</label>
       <input id="ob-local-url" type="text" autocomplete="off" spellcheck="false" placeholder="http://192.168.1.2:8097">
       <label class="field-label" for="ob-public-url">Public URL (optional)</label>
       <input id="ob-public-url" type="text" autocomplete="off" spellcheck="false" placeholder="https://channelflow.example.com">
       <p class="hint" id="ob-network-note"></p>`,
    after: () => attachObNetwork(),
    beforeNext: () => saveObNetwork(),
  },
  {
    next: () => !!obCompleted["account"],
    body: () =>
      `<form onsubmit="return false">
        <label class="field-label" for="ob-user">Username</label>
        <input id="ob-user" type="text" autocomplete="username" spellcheck="false">
        <label class="field-label" for="ob-pass">Password</label>
        <input id="ob-pass" type="password" autocomplete="new-password">
        <label class="field-label" for="ob-pass2">Confirm password</label>
        <input id="ob-pass2" type="password" autocomplete="new-password">
        <div class="ob-action"><button type="button" class="primary" id="ob-do">Create account</button></div>
      </form>`,
    after: () => attachObAccount(),
  },
  {
    next: () => true,
    body: () =>
      `<p>That's it — the ErsatzTV engine and the Jellyfin media source are installed, and your account is ready. Open ChannelFlow to get started — no login needed.</p>`,
  },
];

function renderOnboarding() {
  renderObSteps();
  const step = OB[obIndex];
  document.getElementById("ob-body").innerHTML = step.body();
  document.getElementById("ob-back").hidden = obIndex === 0;
  const $next = document.getElementById("ob-next");
  $next.textContent = obIndex === OB.length - 1 ? "Open ChannelFlow" : "Next";
  $next.hidden = false;
  refreshObNext();
  if (step.after) step.after();
  // The address bar follows the walkthrough, step by step.
  const path = `/first-time/${OB_STEP_PATHS[obIndex]}`;
  if (location.pathname !== path) history.replaceState(null, "", path);
}

function attachObDatabase() {
  const btn = document.getElementById("ob-do");
  if (!btn) return;
  btn.addEventListener("click", async () => {
    const url = document.getElementById("ob-db-url").value.trim();
    if (!url.startsWith("postgres://") && !url.startsWith("postgresql://")) {
      return setObNote("The connection string must start with postgres://", true);
    }
    btn.disabled = true;
    btn.textContent = "Connecting…";
    setObNote("");
    try {
      await request("/api/setup/database", {
        method: "POST",
        body: JSON.stringify({ url }),
      });
      obCompleted["database"] = true;
      btn.textContent = "Connected";
      setObNote("Connected — ChannelFlow is using Postgres.", false);
      refreshObNext();
    } catch (error) {
      btn.disabled = false;
      btn.textContent = "Connect database";
      setObNote(error.message || "Could not connect to Postgres.", true);
    }
  });
}

async function attachObInstall(pluginId, label) {
  const btn = document.getElementById("ob-do");
  if (!btn) return;
  btn.addEventListener("click", async () => {
    btn.disabled = true;
    btn.textContent = "Installing…";
    setObNote("");
    try {
      const catalog = await request("/api/plugins/catalog");
      const plugin = catalog.plugins.find((entry) => entry.id === pluginId);
      if (!plugin) throw new Error(`The store does not list ${pluginId}`);
      await request("/api/plugins/install", {
        method: "POST",
        body: JSON.stringify({ id: pluginId, url: plugin.repository }),
      });
      obCompleted[pluginId] = true;
      btn.textContent = "Installed";
      setObNote("Installed.", false);
      refreshObNext();
    } catch (error) {
      btn.disabled = false;
      btn.textContent = label;
      setObNote(error.message || "Could not install.", true);
    }
  });
}

// The Addresses step shows the detected local URL (the server fills it on this
// first request) and lets the operator correct it, plus set a public URL.
async function attachObNetwork() {
  const local = document.getElementById("ob-local-url");
  const pub = document.getElementById("ob-public-url");
  const note = document.getElementById("ob-network-note");
  if (!local || !pub) return;
  try {
    const data = await request("/api/settings/general");
    const settings = data.settings || {};
    if (!local.value) local.value = settings.local_url || "";
    if (!pub.value) pub.value = settings.public_url || "";
  } catch (error) {
    if (note) note.textContent = "Could not detect the local URL — enter it if you know it.";
  }
}

async function saveObNetwork() {
  const local = document.getElementById("ob-local-url");
  const pub = document.getElementById("ob-public-url");
  if (!local && !pub) return true;
  try {
    await request("/api/settings/general", {
      method: "PUT",
      body: JSON.stringify({
        public_url: (pub && pub.value.trim()) || "",
        local_url: (local && local.value.trim()) || "",
      }),
    });
  } catch (error) {
    // Don't block setup on a URL the server rejected; it can be fixed later in
    // General Settings.
  }
  return true;
}

function attachObAccount() {
  const btn = document.getElementById("ob-do");
  if (!btn) return;
  btn.addEventListener("click", async () => {
    const username = document.getElementById("ob-user").value.trim();
    const password = document.getElementById("ob-pass").value;
    const confirm = document.getElementById("ob-pass2").value;
    if (!username) return setObNote("Choose a username.", true);
    if (password.length < 4) return setObNote("Password must be at least 4 characters.", true);
    if (password !== confirm) return setObNote("Passwords do not match.", true);
    btn.disabled = true;
    btn.textContent = "Creating…";
    setObNote("");
    try {
      await request("/api/auth/setup", {
        method: "POST",
        body: JSON.stringify({ username, password }),
      });
      obCompleted["account"] = true;
      btn.textContent = "Account created";
      refreshObNext();
    } catch (error) {
      btn.disabled = false;
      btn.textContent = "Create account";
      setObNote(error.message || "Could not create the account.", true);
    }
  });
}

const OB_STEP_PATHS = [
  "welcome",
  "plugins",
  "database",
  "transcoding",
  "media-source",
  "addresses",
  "account",
  "done",
];

function obStepFromPath() {
  const match = location.pathname.match(/^\/first-time\/([^/]+)\/?$/);
  if (!match) return null;
  const index = OB_STEP_PATHS.indexOf(match[1]);
  return index >= 0 ? index : null;
}

function startOnboarding() {
  hideScreens();
  obIndex = obStepFromPath() ?? 0;
  obCompleted = {};
  document.getElementById("onboarding-screen").hidden = false;
  renderOnboarding();
}

document.getElementById("ob-back").addEventListener("click", () => {
  if (obIndex > 0) {
    obIndex--;
    renderOnboarding();
  }
});

document.getElementById("ob-next").addEventListener("click", async () => {
  const step = OB[obIndex];
  if (step && step.beforeNext && !(await step.beforeNext())) return;
  if (obIndex < OB.length - 1) {
    obIndex++;
    renderOnboarding();
  } else {
    location.assign("/webui/guide");
  }
});

// ── login and forgotten password ────────────────────────────────────────────
document.getElementById("login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const username = document.getElementById("login-username").value.trim();
  const password = document.getElementById("login-password").value;
  setLoginNote("");
  try {
    await request("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({
        username,
        password,
        remember: document.getElementById("login-remember").checked,
      }),
    });
    location.assign("/webui/guide");
  } catch (error) {
    setLoginNote(error.message || "Wrong username or password.", true);
  }
});

function setResetNote(message, bad) {
  const note = document.getElementById("reset-note");
  note.hidden = !message;
  note.textContent = message || "";
  note.classList.toggle("bad", !!bad);
}

function showForgot() {
  document.getElementById("login-view").hidden = true;
  document.getElementById("reset-view").hidden = false;
  setResetNote("");
  document.getElementById("forgot-info").textContent = "";
}

function showLoginView() {
  document.getElementById("reset-view").hidden = true;
  document.getElementById("login-view").hidden = false;
  document.getElementById("reset-pin").value = "";
  document.getElementById("reset-pass").value = "";
  document.getElementById("reset-pass2").value = "";
}

document.getElementById("forgot-link").addEventListener("click", showForgot);
document.getElementById("reset-back").addEventListener("click", showLoginView);

document.getElementById("forgot-generate").addEventListener("click", async () => {
  document.getElementById("forgot-generate").disabled = true;
  document.getElementById("forgot-info").textContent = "Writing a reset pin…";
  setResetNote("");
  try {
    const data = await request("/api/auth/forgot", { method: "POST" });
    document.getElementById("forgot-info").textContent =
      `Reset pin written to config/${data.file}. Open that file to read it.`;
  } catch (error) {
    document.getElementById("forgot-info").textContent = "";
    setResetNote(error.message || "Could not generate a reset pin.", true);
  } finally {
    document.getElementById("forgot-generate").disabled = false;
  }
});

document.getElementById("reset-submit").addEventListener("click", async () => {
  const pin = document.getElementById("reset-pin").value.trim();
  const password = document.getElementById("reset-pass").value;
  const confirm = document.getElementById("reset-pass2").value;
  if (!pin) return setResetNote("Enter the pin from the reset file.", true);
  if (password.length < 4) return setResetNote("Password must be at least 4 characters.", true);
  if (password !== confirm) return setResetNote("Passwords do not match.", true);
  setResetNote("");
  try {
    await request("/api/auth/reset", {
      method: "POST",
      body: JSON.stringify({ pin, password }),
    });
    showLoginView();
    setLoginNote("Password changed — log in with the new one.", false);
  } catch (error) {
    setResetNote(error.message || "Could not reset the password.", true);
  }
});

// ── log out ─────────────────────────────────────────────────────────────────
document.getElementById("logout").addEventListener("click", async () => {
  try {
    await request("/api/auth/logout", { method: "POST" });
  } catch (error) {
    // best-effort; a reload shows login either way
  }
  location.replace("/");
});

// --- Boot -------------------------------------------------------------------

function showSetupUnreachable(error) {
  hideScreens();
  document.getElementById("onboarding-screen").hidden = false;
  obIndex = obStepFromPath() ?? 0;
  obCompleted = {};
  renderOnboarding();
  setObNote(
    `Could not reach the ChannelFlow server — is it running? (${error.message || "network error"})`,
    true
  );
}

async function fetchStateWithRetry() {
  let lastError;
  for (let attempt = 0; attempt < 5; attempt++) {
    try {
      return await request("/api/auth/state");
    } catch (error) {
      lastError = error;
      await new Promise((r) => setTimeout(r, 700));
    }
  }
  throw lastError;
}

const APP_HOME = "/webui/guide";

async function boot() {
  console.info(`[channelflow] ui build v${UI_BUILD}`);
  const path = location.pathname;
  const onFirstTime = path === "/first-time" || path.startsWith("/first-time/");
  let state = null;
  try {
    state = await fetchStateWithRetry();
  } catch (error) {
    state = null;
  }
  if (state === null) {
    if (onFirstTime) {
      showSetupUnreachable(new Error("the server is not responding"));
    } else {
      showApp();
    }
    return;
  }
  if (!state.setup_done) {
    if (!onFirstTime) {
      location.replace("/first-time");
      return;
    }
    startOnboarding();
    return;
  }
  if (onFirstTime) {
    location.replace(APP_HOME);
    return;
  }
  if (!state.authenticated) {
    showLogin();
    return;
  }
  showApp();
}

// Shared names used by page scripts (loaded later as classic scripts share
// this global scope).
Object.assign(globalThis, {
  $,
  els,
  escapeHtml,
  request,
  MENU,
  PANEL_FOR,
  PLUGIN_PANELS,
  showTab,
  pathForTab,
  tabForPath,
  taskPopup,
  libraryCard,
  setStatus,
  showError,
  copyText,
  showToast,
  CF,
  UI_BUILD,
});

boot();