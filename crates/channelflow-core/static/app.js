const $ = (id) => document.getElementById(id);

const els = {
  rows: $("rows"),
  empty: $("empty"),
  count: $("count"),
  form: $("form"),
  formTitle: $("form-title"),
  id: $("id"),
  number: $("number"),
  name: $("name"),
  description: $("description"),
  enabled: $("enabled"),
  save: $("save"),
  cancel: $("cancel"),
  error: $("error"),
  pageTitle: $("page-title"),
  pageSubtitle: $("page-subtitle"),
  tabChannels: $("tab-channels"),
  tabAbout: $("tab-about"),
  tabCredits: $("tab-credits"),
  tabAi: $("tab-ai"),
  tabTranscode: $("tab-transcode"),
  tabLiveTv: $("tab-live-tv"),
  tabPlugins: $("tab-plugins"),
  tabLibrary: $("tab-library"),
  pluginsRows: $("plugins-rows"),
  pluginsEmpty: $("plugins-empty"),
  pluginsInstalled: $("plugins-installed"),
  pluginsStore: $("plugins-store"),
  pluginsStoreNote: $("plugins-store-note"),
  pluginsStoreGrid: $("plugins-store-grid"),
  pluginsStoreEmpty: $("plugins-store-empty"),
  pluginRemove: $("plugin-remove"),
  pluginRemoveName: $("plugin-remove-name"),
  pluginRemoveDrop: $("plugin-remove-drop"),
  pluginRemoveKeep: $("plugin-remove-keep"),
  pluginRemoveCancel: $("plugin-remove-cancel"),
  pluginRemoveNote: $("plugin-remove-note"),
  tabPlaceholder: $("tab-placeholder"),
  placeholderTitle: $("placeholder-title"),
  aboutApp: $("about-app"),
  aboutSystem: $("about-system"),
  aboutStream: $("about-stream"),
  aiVoice: $("ai-voice"),
  aiName: $("ai-name"),
  aiPriority: $("ai-priority"),
  aiTabs: $("ai-tabs"),
  aiSave: $("ai-save"),
  aiTest: $("ai-test"),
  aiDelete: $("ai-delete"),
  aiTestAll: $("ai-test-all"),
  aiFailover: $("ai-failover"),
  aiFailoverResult: $("ai-failover-result"),
  aiTestResult: $("ai-test-result"),
  aiNote: $("ai-note"),
  aiBaseUrl: $("ai-base-url"),
  aiApiKey: $("ai-api-key"),
  aiKeyReveal: $("ai-key-reveal"),
  aiKeyClear: $("ai-key-clear"),
  aiKeyHint: $("ai-key-hint"),
  aiTtsModel: $("ai-tts-model"),
  aiChatModel: $("ai-chat-model"),
  transcodeGroups: $("transcode-groups"),
  transcodeSave: $("transcode-save"),
  transcodeReload: $("transcode-reload"),
  transcodeNote: $("transcode-note"),
  channelTranscode: $("channel-transcode"),
  channelTranscodeTitle: $("channel-transcode-title"),
  channelTranscodeGroups: $("channel-transcode-groups"),
  channelTranscodeSave: $("channel-transcode-save"),
  channelTranscodeClear: $("channel-transcode-clear"),
  channelTranscodeClose: $("channel-transcode-close"),
  channelTranscodeNote: $("channel-transcode-note"),
  channelTranscodeError: $("channel-transcode-error"),
  liveGrid: $("live-grid"),
  liveEmpty: $("live-empty"),
  liveCount: $("live-count"),
  liveLinks: $("live-links"),
};

let channels = [];

function setStatus(text, kind) {
  if (!els.status) return;
  els.status.textContent = text;
  els.status.className = "status" + (kind ? " " + kind : "");
}

function showError(message) {
  els.error.textContent = message || "";
  els.error.hidden = !message;
}

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

function render() {
  els.count.textContent = channels.length
    ? `${channels.length} channel${channels.length === 1 ? "" : "s"}`
    : "";
  els.empty.hidden = channels.length > 0;

  els.rows.innerHTML = channels
    .map((c) => {
      const state = c.enabled
        ? '<span class="pill on">ON AIR</span>'
        : '<span class="pill off">OFF</span>';
      return `
        <tr data-id="${c.id}">
          <td class="num">${c.number}</td>
          <td><span class="channel-name">${escapeHtml(c.name)}</span></td>
          <td><span class="channel-desc">${escapeHtml(c.description)}</span></td>
          <td class="state">${state}</td>
          <td class="actions">
            <span class="row-actions">
              <button class="link" data-action="edit">Edit</button>
              <button class="link" data-action="transcode">Transcode</button>
              <button class="link" data-action="toggle">${c.enabled ? "Disable" : "Enable"}</button>
              <button class="link danger" data-action="delete">Delete</button>
            </span>
          </td>
        </tr>`;
    })
    .join("");
}

async function load() {
  try {
    const [health, list] = await Promise.all([
      request("/api/health"),
      request("/api/channels"),
    ]);
    channels = list;
    setStatus(`v${health.version} · ok`, "ok");
    render();
  } catch (error) {
    setStatus("unreachable", "bad");
    showError(error.message);
  }
}

function resetForm() {
  els.form.reset();
  els.id.value = "";
  els.enabled.checked = true;
  els.formTitle.textContent = "Add channel";
  els.save.textContent = "Add channel";
  els.cancel.hidden = true;
  showError("");
}

function editChannel(channel) {
  els.id.value = channel.id;
  els.number.value = channel.number;
  els.name.value = channel.name;
  els.description.value = channel.description;
  els.enabled.checked = channel.enabled;
  els.formTitle.textContent = `Edit ${channel.name}`;
  els.save.textContent = "Save changes";
  els.cancel.hidden = false;
  showError("");
  els.name.focus();
}

els.form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const id = els.id.value;
  const payload = {
    number: Number(els.number.value),
    name: els.name.value,
    description: els.description.value,
    enabled: els.enabled.checked,
  };

  try {
    if (id) {
      await request(`/api/channels/${id}`, { method: "PUT", body: JSON.stringify(payload) });
    } else {
      await request("/api/channels", { method: "POST", body: JSON.stringify(payload) });
    }
    resetForm();
    await load();
  } catch (error) {
    showError(error.message);
  }
});

els.cancel.addEventListener("click", resetForm);

els.rows.addEventListener("click", async (event) => {
  const button = event.target.closest("button[data-action]");
  if (!button) return;
  const id = button.closest("tr").dataset.id;
  const channel = channels.find((c) => c.id === id);
  if (!channel) return;

  try {
    if (button.dataset.action === "edit") {
      editChannel(channel);
      return;
    }
    if (button.dataset.action === "transcode") {
      await openChannelTranscode(id);
      return;
    }
    if (button.dataset.action === "toggle") {
      await request(`/api/channels/${id}`, {
        method: "PUT",
        body: JSON.stringify({ enabled: !channel.enabled }),
      });
    }
    if (button.dataset.action === "delete") {
      if (!confirm(`Delete channel ${channel.number} — ${channel.name}?`)) return;
      await request(`/api/channels/${id}`, { method: "DELETE" });
      if (els.id.value === id) resetForm();
    }
    await load();
  } catch (error) {
    showError(error.message);
  }
});

// --- Menu navigation -------------------------------------------------------
// Layout carry-over from ChannelFlow 1.0.0: the drawer swaps the topbar
// heading and shows either the wired Channels panel or a placeholder. No
// backend sits behind the other menus yet, so the hrefs are intercepted here
// rather than served -- a real route per menu comes with the wiring pass.

const MENU = {
  livetv: ["Live TV", "Watch your channels."],
  guide: ["TV Guide", "What's on now, and what's coming up across every channel."],
  general: ["General Settings", "Server-wide defaults for how ChannelFlow runs."],
  quickpin: ["Quick Pin", "Pin something to the top of a channel without building a full preset."],
  clients: ["Clients", "Players that have connected and what they're watching."],
  channels: ["Channels", "Manage Live TV channels"],
  lineups: ["Lineups", "Group channels into playlists you can hand to a player."],
  presets: ["Presets", "Reusable scheduling rules you can drop onto any channel."],
  list: ["Lists", "Named lists of items you can reuse across channels and presets."],
  special: ["Special Presentation", "One-off scheduled events that override the normal lineup."],
  jellyfin: ["Library", "What we've picked up from your Jellyfin or Emby server."],
  commercials: ["Commercials", "Breaks, avails, and where they're allowed to land."],
  commercialbrainz: ["CommercialBrainz", "Ad avails synced from CommercialBrainz."],
  youtube: ["YouTube", "Videos pulled in from YouTube for use in breaks or blocks."],
  ebs: ["Off Air", "What plays when a channel has nothing scheduled."],
  weather: ["Weather", "Forecasts, alerts, and the crawl that runs over programming."],
  news: ["News", "News bumps, tickers, and insert clips."],
  emergency: ["Emergency Broadcast System", "The EBS slate, header, and attention tones."],
  transcode: ["Transcode", "How ChannelFlow asks ErsatzTV next to encode each channel."],
  tasks: ["Tasks", "Scheduled jobs like library scans and cache cleanup."],
  plugins: ["Plugins", "Loaded plugins and what each is allowed to do."],
  about: ["About", "Version, build, and where this install keeps its data."],
  credits: ["Credits", "Who built ChannelFlow, and what it's built on."],
};

// Which menu key has its own panel in the markup.
const PANEL_FOR = {
  channels: "tab-channels",
  about: "tab-about",
  credits: "tab-credits",
  jellyfin: "tab-library",
  transcode: "tab-transcode",
  livetv: "tab-live-tv",
  plugins: "tab-plugins",
};

// Plugin page components this shell knows how to render, mapped to the panel
// id that hosts them. A plugin declares a `page` contribution in its
// manifest; the tab appears only while that plugin is installed, and lands on
// the panel for its component or on the generic placeholder.
const PLUGIN_PANELS = {
  AiPage: "tab-ai",
};

// The ids of plugin-declared pages added to the drawer. Tracked so a rebuild
// (after install or remove) can drop exactly those entries and re-add the
// current set instead of duplicating them.
const pluginPageKeys = new Set();

// Plugin-declared pages add their own drawer entries. The catalog only lists
// installed plugins, so an AI plugin that is not installed leaves no AI tab —
// exactly the same rule that governs the connection types.
async function loadPluginPages() {
  // Rebuild from scratch: out goes whatever a previous run added, then the
  // current plugin list decides the drawer again.
  document
    .querySelectorAll('.drawer-nav a[data-plugin-page]')
    .forEach((link) => link.remove());
  pluginPageKeys.forEach((key) => {
    delete MENU[key];
    delete PANEL_FOR[key];
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
  // Collect plugin pages with the placement a plugin may declare. A page
  // contribution can carry `order` (a number; lower sorts first, default 100)
  // and `section` ("top"/"main" pins it into the top nav group; anything else
  // lands just above the Transcode utility page).
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
  // Stable sort: declared order first, then the label.
  pageLinks.sort((a, b) => a.order - b.order || a.title.localeCompare(b.title));
  // Pinned-to-top pages go into the first nav group, in order; the rest sit
  // just above the Transcode utility page, also in order.
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
  // Plugin pages load asynchronously; a deep link to one can only be resolved
  // once its drawer entry exists.
  const key = tabForPath(location.pathname);
  if (key) showTab(key);
}

// Render rows as `<div class="about-row">` pairs. Rows with no value are
// dropped rather than shown blank, so fields the server cannot answer on this
// platform simply do not appear.
function renderAboutDl(element, rows) {
  if (!element) return;
  element.innerHTML =
    rows
      .filter((row) => row[1] != null && row[1] !== "")
      .map(([label, value, href]) => {
        const text = escapeHtml(String(value));
        const link = href
          ? `<a href="${escapeHtml(String(href))}" target="_blank" rel="noopener">${text}</a>`
          : text;
        return `<div class="about-row"><dt>${escapeHtml(label)}</dt><dd>${link}</dd></div>`;
      })
      .join("") ||
    '<div class="about-row"><dt>Status</dt><dd>Unavailable</dd></div>';
}

// Fetched on each visit so Uptime stays current.
async function loadAbout() {
  try {
    const info = await request("/api/about");
    const app = info.app || {};
    const system = info.system || {};

    renderAboutDl(els.aboutApp, [
      ["Author", app.author, app.authorUrl],
      ["Version", app.version],
      ["Build", app.revision],
      ["Packaging", app.packagingLabel],
      ["Runtime", app.runtime],
      ["Homepage", app.homepage, app.homepage],
    ]);
    renderAboutDl(els.aboutSystem, [
      ["Operating system", system.os],
      ["Architecture", system.architecture],
      ["Host", system.machineName],
      ["CPU cores", system.processorCount],
      ["Memory (working set)", system.workingSet],
      ["Uptime", system.uptime],
      ["Time zone", system.timeZone],
      ["Listen port", system.listenPort],
      ["Config folder", system.configFolder],
    ]);
    // No encoder exists yet, so say that instead of reporting nothing.
    renderAboutDl(els.aboutStream, [
      ["Status", "Encoder arrives with the playout milestone — 2.0.0 hands channel definitions to next and does not encode yet."],
    ]);
  } catch (error) {
    renderAboutDl(els.aboutApp, [["Status", error.message || "Could not load About information."]]);
  }
}

// --- AI --------------------------------------------------------------------
// A list of OpenAI-compatible providers, stored in `ai.json`. The first tab is
// always "New provider"; every saved provider gets its own tab and its own
// form. The API never returns a saved key, only whether one is set, so the
// field starts blank and its contract is "leave blank to keep": a save that
// leaves it blank sends no key at all, and the form's Remove button is what
// clears one. Providers are tried by ascending priority, so the first tab
// that works is the one the app uses.

let aiProviders = [];
// The AI feature is a plugin now; the shell talks to it at its own route
// namespace instead of a core `/api/ai`.
const AI_API = "/api/plugins/com.channelflow.ai";
let aiNextPriority = 1;
let aiDefaults = { base_url: "", chat_model: "", tts_model: "", voice: "" };
let aiActiveId = null; // null is the "New provider" tab
let aiKeySet = false;

function setAiNote(message, bad) {
  els.aiNote.textContent = message || "";
  els.aiNote.className = bad ? "hint bad" : "hint";
}

function renderAiKeyState() {
  els.aiKeyClear.hidden = !aiKeySet;
  els.aiKeyHint.textContent = aiKeySet
    ? "A key is saved. Leave this blank to keep it, or type a new one to replace it."
    : "No key saved. Leave it blank for a server that needs none.";
  els.aiApiKey.placeholder = aiKeySet ? "•••••••• saved" : "sk-…";
}

// The tab strip: "New provider" first, then the saved providers in the order
// the app will try them. A provider is addressed by its id, so renaming one
// does not move it to a different tab.
function renderAiTabs() {
  els.aiTabs.replaceChildren();
  const tabs = [{ id: null, label: "New provider" }].concat(
    aiProviders.map((provider) => ({ id: provider.id, label: provider.name }))
  );
  for (const tab of tabs) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "inner-tab";
    button.textContent = tab.label;
    button.setAttribute("role", "tab");
    const active = tab.id === aiActiveId;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", active ? "true" : "false");
    button.addEventListener("click", () => selectAiTab(tab.id));
    els.aiTabs.appendChild(button);
  }
}

// The running order shown under the form. It is the same list the tabs use, so
// what the page shows is what the app does.
function renderAiFailover() {
  els.aiFailover.replaceChildren();
  if (!aiProviders.length) {
    const item = document.createElement("li");
    item.className = "empty";
    item.textContent = "No providers yet — add one above.";
    els.aiFailover.appendChild(item);
    return;
  }
  for (const provider of aiProviders) {
    const item = document.createElement("li");
    item.textContent = `${provider.priority} · ${provider.name} — ${provider.base_url}`;
    els.aiFailover.appendChild(item);
  }
}

// Point the one form at a provider, or at a blank new one.
function fillAiForm(provider) {
  if (provider) {
    els.aiName.value = provider.name;
    els.aiPriority.value = String(provider.priority);
    els.aiBaseUrl.value = provider.base_url;
    els.aiChatModel.value = provider.chat_model;
    els.aiTtsModel.value = provider.tts_model;
    els.aiVoice.value = provider.voice;
    aiKeySet = provider.api_key_set;
    els.aiSave.textContent = "Save provider";
    els.aiDelete.hidden = false;
  } else {
    els.aiName.value = "";
    els.aiPriority.value = String(aiNextPriority);
    els.aiBaseUrl.value = aiDefaults.base_url;
    els.aiChatModel.value = aiDefaults.chat_model;
    els.aiTtsModel.value = aiDefaults.tts_model;
    els.aiVoice.value = aiDefaults.voice;
    aiKeySet = false;
    els.aiSave.textContent = "Add provider";
    els.aiDelete.hidden = true;
  }
  els.aiApiKey.value = "";
  els.aiApiKey.type = "password";
  els.aiKeyReveal.textContent = "Show";
  renderAiKeyState();
  els.aiTestResult.hidden = true;
}

function selectAiTab(id) {
  if (id !== null && !aiProviders.some((provider) => provider.id === id)) {
    id = null;
  }
  aiActiveId = id;
  fillAiForm(id === null ? null : aiProviders.find((provider) => provider.id === id));
  renderAiTabs();
  els.aiFailoverResult.hidden = true;
  setAiNote("");
}

async function loadAi(selectId) {
  try {
    const data = await request(AI_API);
    aiProviders = data.providers;
    aiNextPriority = data.next_priority;
    aiDefaults = data.defaults;
    const wanted = selectId !== undefined ? selectId : aiActiveId;
    renderAiFailover();
    selectAiTab(wanted);
  } catch (error) {
    setAiNote(error.message || "Could not load AI settings.", true);
  }
}

// The name, URL, models and a new key are always sent — clearing one is a
// validation error worth showing, not a silent fall-back. Priority is left out
// when the field is blank, which keeps the stored number on an edit; creating
// a provider needs it, so `saveAi` checks first. The key is only sent when the
// field holds something, so a blank field keeps the saved key.
function aiBody() {
  const body = {
    name: els.aiName.value,
    base_url: els.aiBaseUrl.value,
    chat_model: els.aiChatModel.value,
    tts_model: els.aiTtsModel.value,
    voice: els.aiVoice.value,
  };
  const priority = els.aiPriority.value.trim();
  if (priority !== "") body.priority = Number.parseInt(priority, 10);
  const key = els.aiApiKey.value.trim();
  if (key !== "") body.api_key = key;
  return body;
}

function newPriorityMissing() {
  return aiActiveId === null && els.aiPriority.value.trim() === "";
}

async function saveAi() {
  if (newPriorityMissing()) {
    setAiNote("Give the provider a priority — a whole number, lowest is tried first.", true);
    return;
  }
  try {
    let saved;
    let message;
    if (aiActiveId === null) {
      saved = await request(`${AI_API}/providers`, {
        method: "POST",
        body: JSON.stringify(aiBody()),
      });
      message = `Added ${saved.name}.`;
    } else {
      saved = await request(`${AI_API}/providers/${encodeURIComponent(aiActiveId)}`, {
        method: "PUT",
        body: JSON.stringify(aiBody()),
      });
      message = `Saved ${saved.name}.`;
    }
    await loadAi(saved.id);
    setAiNote(message);
  } catch (error) {
    setAiNote(error.message || "Could not save the provider.", true);
  }
}

async function deleteAi() {
  const provider = aiProviders.find((entry) => entry.id === aiActiveId);
  if (!provider) return;
  if (!window.confirm(`Delete the "${provider.name}" provider?`)) return;
  try {
    await request(`${AI_API}/providers/${encodeURIComponent(provider.id)}`, { method: "DELETE" });
    await loadAi(null);
    setAiNote(`Deleted ${provider.name}.`);
  } catch (error) {
    setAiNote(error.message || "Could not delete the provider.", true);
  }
}

// Tests what is on the page rather than what is saved, so a new key, model or
// priority can be checked before it is written. It makes the real requests — a
// model listing, a chat reply and one spoken phrase — and stores nothing.
async function testAi() {
  if (newPriorityMissing()) {
    setAiNote("Give the provider a priority before testing it.", true);
    return;
  }
  els.aiTest.disabled = true;
  els.aiTest.textContent = "Testing…";
  els.aiTestResult.hidden = true;
  setAiNote("");
  try {
    const path =
      aiActiveId === null
        ? `${AI_API}/test`
        : `${AI_API}/providers/${encodeURIComponent(aiActiveId)}/test`;
    renderAiTest(await request(path, { method: "POST", body: JSON.stringify(aiBody()) }));
  } catch (error) {
    setAiNote(error.message || "Could not run the test.", true);
  } finally {
    els.aiTest.disabled = false;
    els.aiTest.textContent = "Test AI";
  }
}

function renderAiTest(report) {
  const head = report.ok
    ? `Connected. Spoke ${formatBytes(report.bytes)} in ${report.elapsed_ms} ms.`
    : "The test did not pass.";
  els.aiTestResult.className = "test-result " + (report.ok ? "ok" : "bad");
  els.aiTestResult.innerHTML =
    `<p class="test-head">${escapeHtml(head)}</p>` +
    report.probes
      .map(
        (probe) =>
          `<div class="test-probe"><span class="test-name">${escapeHtml(probe.name)}</span>` +
          `<span class="test-detail ${probe.ok ? "ok" : "bad"}">${escapeHtml(probe.detail)}</span></div>`
      )
      .join("");
  els.aiTestResult.hidden = false;
}

// Walk the providers in priority order and stop at the first that answers, so
// the page shows which endpoint the app would actually use — and which ones
// are never reached because a higher-priority one works.
async function testAiAll() {
  els.aiTestAll.disabled = true;
  els.aiTestAll.textContent = "Testing…";
  els.aiFailoverResult.hidden = true;
  try {
    renderFailover(await request(`${AI_API}/test-all`, { method: "POST" }));
  } catch (error) {
    els.aiFailoverResult.className = "test-result bad";
    els.aiFailoverResult.innerHTML = `<p class="test-head">${escapeHtml(
      error.message || "Could not run the failover test."
    )}</p>`;
    els.aiFailoverResult.hidden = false;
  } finally {
    els.aiTestAll.disabled = false;
    els.aiTestAll.textContent = "Test failover";
  }
}

function renderFailover(report) {
  const head = report.ok
    ? `Connected — "${report.chosen}" answers first.`
    : "No provider answered.";
  els.aiFailoverResult.className = "test-result " + (report.ok ? "ok" : "bad");
  els.aiFailoverResult.innerHTML =
    `<p class="test-head">${escapeHtml(head)}</p>` +
    report.attempts
      .map((attempt) => {
        const suffix = report.chosen === attempt.name && attempt.ok ? " (used first)" : "";
        return (
          `<div class="test-probe"><span class="test-name">${attempt.priority}</span>` +
          `<span class="test-detail ${attempt.ok ? "ok" : "bad"}">${escapeHtml(
            attempt.name
          )} — ${escapeHtml(attempt.detail)}${suffix}</span></div>`
        );
      })
      .join("");
  els.aiFailoverResult.hidden = false;
}

function formatBytes(bytes) {
  if (bytes >= 1048576) return `${(bytes / 1048576).toFixed(1)} MiB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${bytes} B`;
}

els.aiSave.addEventListener("click", saveAi);
els.aiTest.addEventListener("click", testAi);
els.aiDelete.addEventListener("click", deleteAi);
els.aiTestAll.addEventListener("click", testAiAll);
els.aiKeyClear.addEventListener("click", async () => {
  if (aiActiveId === null) return;
  try {
    await request(`${AI_API}/providers/${encodeURIComponent(aiActiveId)}`, {
      method: "PUT",
      body: JSON.stringify({ api_key: "" }),
    });
    aiKeySet = false;
    renderAiKeyState();
    setAiNote("Saved key removed.");
  } catch (error) {
    setAiNote(error.message || "Could not remove the key.", true);
  }
});
els.aiKeyReveal.addEventListener("click", () => {
  const hidden = els.aiApiKey.type === "password";
  els.aiApiKey.type = hidden ? "text" : "password";
  els.aiKeyReveal.textContent = hidden ? "Hide" : "Show";
});

// --- Transcode -------------------------------------------------------------
// The Transcode feature is the ErsatzTV plugin; the page calls its routes.
const ERSATZTV_API = "/api/plugins/com.channelflow.ersatztv";

// The field list is served, not hard-coded here: the plugin builds it from
// next's `channel_config.json`, and a test in the plugin asserts it names
// exactly the settings next accepts. That is why the form cannot offer a
// setting next would reject, or quietly miss one it would take — a field
// added upstream shows up here after a refresh.
//
// next keeps these settings per channel. ChannelFlow keeps instance defaults
// on the Transcode page and stores only a channel's *differences* from them,
// so editing a default still reaches every channel that has not overridden
// that one field. The dialog diffs the edited values against the defaults to
// work out which fields to store, which is also what marks a field as
// overridden — no separate toggle to keep in sync with the value.

// Distinguishes "unchanged" from "changed to null" while diffing.
const NO_CHANGE = Symbol("no-change");

let transcodeSpec = null;
let transcodeDraft = null;
let channelTranscode = null;
let channelTranscodeDraft = null;

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function getPath(object, path) {
  return path
    .split(".")
    .reduce((value, key) => (value == null ? undefined : value[key]), object);
}

function setPath(object, path, value) {
  const keys = path.split(".");
  let cursor = object;
  for (const key of keys.slice(0, -1)) {
    if (!isPlainObject(cursor[key])) cursor[key] = {};
    cursor = cursor[key];
  }
  cursor[keys[keys.length - 1]] = value;
}

// next's schema releases a nullable field when the value is null; the note
// under each field in the dialog reads back what inheriting would give.
function describeField(field, value) {
  if (value === null || value === undefined) return field.nullLabel || "Not set";
  if (field.kind === "bool") return value ? "On" : "Off";
  if (field.kind === "enum") {
    const match = (field.options || []).find((option) => option.value === value);
    return match ? match.label : String(value);
  }
  if (field.kind === "enum_list") {
    const labels = (field.options || [])
      .filter((option) => (value || []).includes(option.value))
      .map((option) => option.label);
    return labels.length ? labels.join(", ") : "None";
  }
  if (field.kind === "text_list") {
    return value && value.length ? value.join(", ") : "None";
  }
  return String(value);
}

function fieldId(field) {
  return `f-${field.path.replace(/\./g, "-")}`;
}

// Build the control for one field, reporting every edit through `onChange`.
// The control is seeded from `value`, which is the object being edited.
function buildControl(field, value, onChange) {
  const control = document.createElement("input");
  const extras = [];

  if (field.kind === "bool") {
    control.type = "checkbox";
    control.checked = value === true;
    control.addEventListener("change", () => onChange(control.checked));
    return { control, extras };
  }

  if (field.kind === "enum") {
    const select = document.createElement("select");
    if (field.nullable) {
      const blank = document.createElement("option");
      blank.value = "";
      blank.textContent = field.nullLabel || "Not set";
      blank.selected = value == null;
      select.appendChild(blank);
    }
    for (const option of field.options || []) {
      const entry = document.createElement("option");
      entry.value = option.value;
      entry.textContent = option.label;
      entry.selected = value === option.value;
      select.appendChild(entry);
    }
    select.addEventListener("change", () => {
      if (field.nullable && select.selectedIndex === 0) {
        onChange(null);
        return;
      }
      const offset = field.nullable ? 1 : 0;
      const option = (field.options || [])[select.selectedIndex - offset];
      // Emit the option's own value, not the select's string, so a numeric
      // choice like bit depth stores 8 and not "8".
      onChange(option ? option.value : null);
    });
    return { control: select, extras };
  }

  if (field.kind === "enum_list") {
    const group = document.createElement("div");
    group.className = "checks";
    const selected = new Set(Array.isArray(value) ? value : []);
    for (const option of field.options || []) {
      const wrapper = document.createElement("label");
      wrapper.className = "check inline";
      const box = document.createElement("input");
      box.type = "checkbox";
      box.value = option.value;
      box.checked = selected.has(option.value);
      box.addEventListener("change", () => {
        if (box.checked) selected.add(option.value);
        else selected.delete(option.value);
        onChange(
          (field.options || [])
            .filter((entry) => selected.has(entry.value))
            .map((entry) => entry.value)
        );
      });
      wrapper.append(box, document.createTextNode(option.label));
      group.appendChild(wrapper);
    }
    return { control: group, extras };
  }

  if (field.kind === "text_list") {
    const textarea = document.createElement("textarea");
    textarea.rows = 3;
    textarea.value = Array.isArray(value) ? value.join("\n") : "";
    textarea.addEventListener("input", () =>
      onChange(
        textarea.value
          .split("\n")
          .map((line) => line.trim())
          .filter(Boolean)
      )
    );
    return { control: textarea, extras };
  }

  if (field.kind === "int" || field.kind === "float") {
    control.type = "number";
    if (field.min != null) control.min = field.min;
    if (field.max != null) control.max = field.max;
    control.step = field.kind === "int" ? field.step || 1 : field.step || "any";
    control.value = value == null ? "" : String(value);
    control.addEventListener("input", () => {
      if (control.value === "") {
        onChange(field.nullable ? null : 0);
        return;
      }
      const parsed =
        field.kind === "int"
          ? Number.parseInt(control.value, 10)
          : Number.parseFloat(control.value);
      onChange(Number.isFinite(parsed) ? parsed : null);
    });
    return { control, extras };
  }

  control.type = "text";
  control.value = value == null ? "" : String(value);
  if (field.suggestions && field.suggestions.length) {
    const list = document.createElement("datalist");
    list.id = `${fieldId(field)}-list`;
    for (const suggestion of field.suggestions) {
      const option = document.createElement("option");
      option.value = suggestion;
      list.appendChild(option);
    }
    control.setAttribute("list", list.id);
    extras.push(list);
  }
  control.addEventListener("input", () => {
    const text = control.value.trim();
    onChange(text === "" ? (field.nullable ? null : "") : text);
  });
  return { control, extras };
}

// `defaults` is only passed for the per-channel dialog; with it each field
// grows a note reading back what inheriting would give, and lights up once the
// value differs from the default.
function renderField(field, value, defaults) {
  const wrapper = document.createElement("div");
  wrapper.className = "field";
  wrapper.dataset.path = field.path;

  const label = document.createElement("label");
  label.className = "field-label";
  label.textContent = field.label;
  label.htmlFor = fieldId(field);
  wrapper.appendChild(label);

  const note = defaults ? document.createElement("small") : null;
  if (note) note.className = "field-note";

  const updateNote = () => {
    const base = getPath(defaults, field.path);
    const current = getPath(value, field.path);
    const overridden = JSON.stringify(current ?? null) !== JSON.stringify(base ?? null);
    wrapper.classList.toggle("overridden", overridden);
    note.textContent = `${overridden ? "Overridden — default" : "Default"}: ${describeField(
      field,
      base
    )}`;
  };

  const { control, extras } = buildControl(field, getPath(value, field.path), (next) => {
    setPath(value, field.path, next);
    if (note) updateNote();
  });
  control.id = fieldId(field);
  wrapper.appendChild(control);
  for (const extra of extras) wrapper.appendChild(extra);

  if (field.hint) {
    const hint = document.createElement("small");
    hint.className = "field-hint";
    hint.textContent = field.hint;
    wrapper.appendChild(hint);
  }
  if (note) {
    wrapper.appendChild(note);
    updateNote();
  }

  return wrapper;
}

function renderSettings(container, groups, value, defaults) {
  container.innerHTML = "";
  for (const group of groups) {
    const card = document.createElement("div");
    card.className = "card section-card settings-group";
    const heading = document.createElement("h3");
    heading.textContent = group.title;
    card.appendChild(heading);
    if (group.hint) {
      const hint = document.createElement("p");
      hint.className = "hint";
      hint.textContent = group.hint;
      card.appendChild(hint);
    }
    for (const field of group.fields) {
      card.appendChild(renderField(field, value, defaults));
    }
    container.appendChild(card);
  }
}

// What a channel needs to store: the keys of `value` that differ from `base`,
// recursing through objects. A null is a real value here, not "inherit".
function diffAgainst(base, value) {
  if (isPlainObject(base) && isPlainObject(value)) {
    const patch = {};
    let changed = false;
    for (const key of Object.keys(value)) {
      const child = diffAgainst(base[key], value[key]);
      if (child !== NO_CHANGE) {
        patch[key] = child;
        changed = true;
      }
    }
    return changed ? patch : NO_CHANGE;
  }
  return JSON.stringify(base ?? null) === JSON.stringify(value ?? null) ? NO_CHANGE : value;
}

function setTranscodeNote(message, bad) {
  els.transcodeNote.textContent = message || "";
  els.transcodeNote.className = bad ? "hint bad" : "hint";
}

async function loadTranscode() {
  try {
    const data = await request(ERSATZTV_API);
    transcodeSpec = data.spec;
    transcodeDraft = data.defaults;
    renderSettings(els.transcodeGroups, data.spec.groups, transcodeDraft);
    setTranscodeNote("");
  } catch (error) {
    setTranscodeNote(error.message || "Could not load transcode settings.", true);
  }
}

async function saveTranscode() {
  try {
    const data = await request(ERSATZTV_API, {
      method: "PUT",
      body: JSON.stringify(transcodeDraft),
    });
    transcodeDraft = data.defaults;
    renderSettings(els.transcodeGroups, transcodeSpec.groups, transcodeDraft);
    setTranscodeNote("Saved. Channels with no override for a field pick up the new value.");
  } catch (error) {
    setTranscodeNote(error.message || "Could not save transcode settings.", true);
  }
}

function setChannelTranscodeNote(message, bad) {
  if (els.channelTranscodeNote) {
    els.channelTranscodeNote.textContent = message || "";
    els.channelTranscodeNote.className = bad ? "hint bad" : "hint";
  }
  showChannelTranscodeError(bad ? message : "");
}

function showChannelTranscodeError(message) {
  els.channelTranscodeError.textContent = message || "";
  els.channelTranscodeError.hidden = !message;
}

async function openChannelTranscode(id) {
  try {
    const data = await request(`${ERSATZTV_API}/channels/${id}`);
    channelTranscode = data;
    // Edit a copy of the effective settings; the diff against the defaults is
    // what gets stored.
    channelTranscodeDraft = data.effective;
    els.channelTranscodeTitle.textContent = `Transcode — ${data.channel.number} ${data.channel.name}`;
    renderSettings(
      els.channelTranscodeGroups,
      data.spec.groups,
      channelTranscodeDraft,
      data.defaults
    );
    setChannelTranscodeNote("");
    els.channelTranscode.showModal();
  } catch (error) {
    setStatus("transcode failed", "bad");
    showError(error.message);
  }
}

async function saveChannelTranscode() {
  if (!channelTranscode) return;
  const diff = diffAgainst(channelTranscode.defaults, channelTranscodeDraft);
  const patch = diff === NO_CHANGE ? {} : diff;
  try {
    const data = await request(`${ERSATZTV_API}/channels/${channelTranscode.channel.id}`, {
      method: "PUT",
      body: JSON.stringify(patch),
    });
    channelTranscode.overrides = data.overrides;
    channelTranscode.effective = data.effective;
    channelTranscodeDraft = data.effective;
    renderSettings(
      els.channelTranscodeGroups,
      channelTranscode.spec.groups,
      channelTranscodeDraft,
      channelTranscode.defaults
    );
    const count = countLeaves(data.overrides);
    setChannelTranscodeNote(
      count
        ? `Saved ${count} override${count === 1 ? "" : "s"} on this channel.`
        : "Saved. This channel follows the Transcode page exactly."
    );
  } catch (error) {
    setChannelTranscodeNote(error.message || "Could not save overrides.", true);
  }
}

async function clearChannelTranscode() {
  if (!channelTranscode) return;
  try {
    const data = await request(`${ERSATZTV_API}/channels/${channelTranscode.channel.id}`, {
      method: "DELETE",
    });
    channelTranscode.overrides = data.overrides;
    channelTranscode.effective = data.effective;
    channelTranscodeDraft = data.effective;
    renderSettings(
      els.channelTranscodeGroups,
      channelTranscode.spec.groups,
      channelTranscodeDraft,
      channelTranscode.defaults
    );
    setChannelTranscodeNote("This channel follows the Transcode page again.");
  } catch (error) {
    setChannelTranscodeNote(error.message || "Could not clear overrides.", true);
  }
}

function countLeaves(value) {
  if (!isPlainObject(value)) return 1;
  return Object.values(value).reduce((total, child) => total + countLeaves(child), 0);
}

els.transcodeSave.addEventListener("click", saveTranscode);
els.transcodeReload.addEventListener("click", loadTranscode);
els.channelTranscodeSave.addEventListener("click", saveChannelTranscode);
els.channelTranscodeClear.addEventListener("click", clearChannelTranscode);
els.channelTranscodeClose.addEventListener("click", () => els.channelTranscode.close());

// --- Live TV ---------------------------------------------------------------
// The URLs a player will use. Only port 8097 is published and the encoder
// listens inside the container on another port, so everything here is written
// against ChannelFlow's own origin and served — until playout lands — by the
// /live/* routes, which answer 503 with a sentence rather than 404.

function livePlaylistUrl(path) {
  return `${location.origin}${path}`;
}

async function loadLiveTv() {
  try {
    renderLive(await request("/api/channels"));
  } catch (error) {
    els.liveGrid.innerHTML = "";
    els.liveCount.textContent = "";
    els.liveEmpty.hidden = false;
    els.liveEmpty.textContent = error.message || "Could not load channels.";
  }
}

// --- Plugins ---------------------------------------------------------------
// Two tabs. Installed lists what the registry says this instance runs, with
// update/remove actions; Store lists the ChannelFlow-Plugins repository and
// lets you install what this build contains.

function showPluginsTab(name) {
  document.querySelectorAll("#plugins-tabs .inner-tab").forEach((tab) => {
    const active = tab.dataset.pluginsTab === name;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", active ? "true" : "false");
  });
  els.pluginsInstalled.hidden = name !== "installed";
  els.pluginsStore.hidden = name !== "store";
  if (name === "installed") loadInstalledPlugins();
  else loadPluginStore();
}

async function loadPlugins() {
  showPluginsTab("installed");
}

async function loadInstalledPlugins() {
  try {
    renderInstalled((await request("/api/plugins")).plugins);
  } catch (error) {
    els.pluginsRows.innerHTML = "";
    els.pluginsEmpty.hidden = false;
    els.pluginsEmpty.textContent = error.message || "Could not load installed plugins.";
  }
}

function renderInstalled(plugins) {
  els.pluginsEmpty.hidden = plugins.length > 0;
  els.pluginsRows.innerHTML = plugins
    .map((plugin) => {
      const status = plugin.enabled
        ? '<span class="pill on">ENABLED</span>'
        : '<span class="pill off">DISABLED</span>';
      const health = plugin.health
        ? `${plugin.health.ok ? "ok" : "down"} — ${escapeHtml(plugin.health.detail)}`
        : "not part of this build";
      const update = plugin.update_available
        ? `<button type="button" class="ghost" data-installed="${escapeHtml(plugin.id)}" data-act="update">Update</button>`
        : "";
      const perms = (plugin.permissions || []).length
        ? plugin.permissions.map((p) => `<code>${escapeHtml(p)}</code>`).join(" ")
        : "none";
      return `<tr>
        <td><span class="channel-name">${escapeHtml(plugin.name)}</span>
            <div class="channel-desc">${escapeHtml(plugin.id)} · ${status}</div></td>
        <td>${escapeHtml(plugin.category || "—")}</td>
        <td>${escapeHtml(plugin.version)}</td>
        <td class="perms">${perms}</td>
        <td class="${plugin.health && plugin.health.ok ? "ok" : ""}">${escapeHtml(health)}</td>
        <td class="actions">${update}
          <button type="button" class="ghost danger" data-installed="${escapeHtml(plugin.id)}" data-act="remove">Remove</button>
        </td>
      </tr>`;
    })
    .join("");
}

async function loadPluginStore() {
  els.pluginsStoreNote.textContent = "Loading the plugin store…";
  try {
    const [catalog, installed] = await Promise.all([
      request("/api/plugins/catalog"),
      request("/api/plugins"),
    ]);
    renderStore(catalog, installed.plugins);
  } catch (error) {
    els.pluginsStoreGrid.innerHTML = "";
    els.pluginsStoreNote.textContent = "";
    els.pluginsStoreEmpty.hidden = false;
    els.pluginsStoreEmpty.textContent = error.message || "Could not load the plugin store.";
  }
}

function renderStore(catalog, installed) {
  const installedIds = new Set(installed.map((plugin) => plugin.id));
  const available = catalog.plugins.filter((plugin) => !installedIds.has(plugin.id));
  const errors = catalog.errors || [];
  els.pluginsStoreNote.textContent = errors.length
    ? `Some repositories could not be reached: ${errors.map((e) => e.repository).join(", ")}`
    : "";
  els.pluginsStoreEmpty.hidden = available.length > 0;
  els.pluginsStoreEmpty.textContent = available.length
    ? ""
    : "Everything in the store is already installed.";
  els.pluginsStoreGrid.innerHTML = available
    .map((plugin) => {
      const banner = plugin.image_url
        ? `<img class="plugin-banner" src="${escapeHtml(plugin.image_url)}" alt="">`
        : `<div class="plugin-banner plugin-banner-empty"></div>`;
      const versions = plugin.versions || [];
      const latest = versions.length ? versions[versions.length - 1].version : "";
      const install = plugin.compatible
        ? `<button type="button" class="primary" data-store="${escapeHtml(plugin.id)}" data-url="${escapeHtml(plugin.repository)}" data-version="${escapeHtml(latest)}">Install</button>`
        : `<button type="button" class="ghost" disabled title="No version runs on this base">Incompatible</button>`;
      // The permissions the plugin asks for. The repository manifest carries
      // none, so this comes from the plugin's own manifest when this build
      // ships it; "unknown" means it is not part of this build.
      const perms = (plugin.permissions || []).length
        ? plugin.permissions.map((p) => `<code>${escapeHtml(p)}</code>`).join(" ")
        : '<span class="plugin-perms-unknown">not part of this build</span>';
      return `<article class="plugin-card">
        ${banner}
        <div class="plugin-card-body">
          <h4>${escapeHtml(plugin.name)}</h4>
          <p class="plugin-card-meta">${escapeHtml(plugin.category || "plugin")} · v${escapeHtml(latest)} · ${escapeHtml(plugin.owner || "")}</p>
          <p class="channel-desc">${escapeHtml(plugin.description)}</p>
          <p class="plugin-perms"><strong>Permissions:</strong> ${perms}</p>
          <div class="plugin-card-actions">${install}</div>
        </div>
      </article>`;
    })
    .join("");
}

let pluginRemoveId = null;

function openRemoveDialog(id, name) {
  pluginRemoveId = id;
  els.pluginRemoveName.textContent = name;
  els.pluginRemoveNote.textContent = "";
  els.pluginRemove.showModal();
}

async function removePlugin(dropDatabase) {
  if (!pluginRemoveId) return;
  const id = pluginRemoveId;
  els.pluginRemoveNote.textContent = "Removing…";
  try {
    await request(`/api/plugins/installed/${encodeURIComponent(id)}?drop_database=${dropDatabase}`, {
      method: "DELETE",
    });
    els.pluginRemove.close();
    pluginRemoveId = null;
    loadInstalledPlugins();
    loadPluginPages();
  } catch (error) {
    els.pluginRemoveNote.textContent = error.message || "Could not remove the plugin.";
  }
}

async function installPlugin(id, url, version) {
  try {
    await request("/api/plugins/install", {
      method: "POST",
      body: JSON.stringify({ id, url, version: version || undefined }),
    });
    loadPluginStore();
    loadInstalledPlugins();
    loadPluginPages();
  } catch (error) {
    els.pluginsStoreEmpty.hidden = false;
    els.pluginsStoreEmpty.textContent = error.message || "Could not install the plugin.";
  }
}

async function updatePlugin(id) {
  try {
    const data = await request(`/api/plugins/${encodeURIComponent(id)}/update`, { method: "PUT" });
    els.pluginsEmpty.hidden = false;
    els.pluginsEmpty.textContent = data.message || "Updated.";
  } catch (error) {
    els.pluginsEmpty.hidden = false;
    els.pluginsEmpty.textContent = error.message || "Could not update the plugin.";
  }
}

document.querySelectorAll("#plugins-tabs .inner-tab").forEach((tab) => {
  tab.addEventListener("click", () => showPluginsTab(tab.dataset.pluginsTab));
});

els.pluginsRows.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-installed]");
  if (!button) return;
  const id = button.dataset.installed;
  if (button.dataset.act === "remove") {
    const row = button.closest("tr");
    const name = row ? row.querySelector(".channel-name").textContent : id;
    openRemoveDialog(id, name);
  } else {
    updatePlugin(id);
  }
});

els.pluginsStoreGrid.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-store]");
  if (button) installPlugin(button.dataset.store, button.dataset.url, button.dataset.version);
});

els.pluginRemoveDrop.addEventListener("click", () => removePlugin(true));
els.pluginRemoveKeep.addEventListener("click", () => removePlugin(false));
els.pluginRemoveCancel.addEventListener("click", () => {
  pluginRemoveId = null;
  els.pluginRemove.close();
});

function renderLive(list) {
  const count = list.length;
  els.liveCount.textContent = count ? `${count} channel${count === 1 ? "" : "s"}` : "";
  els.liveEmpty.hidden = count > 0;
  els.liveEmpty.textContent = "No channels yet — add one on the Channels tab.";

  els.liveGrid.innerHTML = list
    .map((channel) => {
      const state = channel.enabled
        ? '<span class="pill on">ON AIR</span>'
        : '<span class="pill off">OFF</span>';
      const url = `${location.origin}/live/${channel.number}.m3u8`;
      return `
        <article class="live-card">
          <div class="live-card-head">
            <span class="live-num">${channel.number}</span>
            <h3>${escapeHtml(channel.name)}</h3>
            ${state}
          </div>
          <div class="live-url">
            <code>${escapeHtml(url)}</code>
            <button type="button" class="ghost" data-copy="${escapeHtml(url)}">Copy</button>
          </div>
        </article>`;
    })
    .join("");

  renderLiveLinks();
}

function renderLiveLinks() {
  const links = [
    [
      "M3U playlist",
      livePlaylistUrl("/live/channels.m3u"),
      "Every channel, for VLC, TiviMate, Jellyfin and the like.",
    ],
    ["XMLTV guide", livePlaylistUrl("/live/xmltv.xml"), "Programme data for the playlist above."],
  ];
  els.liveLinks.innerHTML = links
    .map(
      ([label, url, hint]) => `
        <div class="live-link">
          <div class="live-link-text">
            <span class="live-link-label">${label}</span>
            <small class="field-hint">${hint}</small>
          </div>
          <div class="live-url">
            <code>${escapeHtml(url)}</code>
            <button type="button" class="ghost" data-copy="${escapeHtml(url)}">Copy</button>
          </div>
        </div>`
    )
    .join("");
}

// The Clipboard API needs a secure context, which plain http on a LAN is not,
// so fall back to the old selection trick rather than leave the button dead.
async function copyToClipboard(button, text) {
  let copied = false;
  try {
    await navigator.clipboard.writeText(text);
    copied = true;
  } catch (error) {
    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    try {
      copied = document.execCommand("copy");
    } catch (inner) {
      copied = false;
    }
    area.remove();
  }

  const label = button.textContent;
  button.textContent = copied ? "Copied" : "Copy failed";
  button.disabled = true;
  setTimeout(() => {
    button.textContent = label;
    button.disabled = false;
  }, 1200);
}

// The cards and links are re-rendered wholesale, so the handler lives on the
// containers rather than on each button.
for (const root of [els.liveGrid, els.liveLinks]) {
  root.addEventListener("click", (event) => {
    const button = event.target.closest("button[data-copy]");
    if (button) copyToClipboard(button, button.dataset.copy);
  });
}

function showTab(key) {
  const entry = MENU[key];
  if (!entry) return;

  document.querySelectorAll(".drawer-nav a").forEach((link) => {
    const active = link.dataset.tab === key;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });

  // Menus with a real page behind them; everything else still lands on the
  // placeholder carrying that menu's own title.
  const panel = PANEL_FOR[key] || "tab-placeholder";
  [
    els.tabChannels,
    els.tabAbout,
    els.tabCredits,
    els.tabAi,
    els.tabTranscode,
    els.tabLiveTv,
    els.tabPlugins,
    els.tabLibrary,
    els.tabPlaceholder,
  ].forEach((element) => {
    element.hidden = element.id !== panel;
  });

  if (panel === "tab-placeholder") els.placeholderTitle.textContent = entry[0];
  els.pageTitle.textContent = entry[0];
  els.pageSubtitle.textContent = entry[1];

  // Fetched on each visit so settings changed elsewhere are picked up.
  if (key === "about") loadAbout();
  if (key === "ai") loadAi();
  if (key === "transcode") loadTranscode();
  if (key === "livetv") loadLiveTv();
  if (key === "plugins") loadPlugins();
  if (key === "jellyfin") loadLibrary();
}

function pathForTab(key) {
  for (const link of document.querySelectorAll(".drawer-nav a[data-tab]")) {
    if (link.dataset.tab === key) return link.getAttribute("href") || `/webui/${key}`;
  }
  return `/webui/${key}`;
}

function tabForPath(path) {
  let found = null;
  document.querySelectorAll(".drawer-nav a[data-tab]").forEach((link) => {
    if (link.getAttribute("href") === path) found = link.dataset.tab;
  });
  return found;
}

// --- Library (media connections) -------------------------------------------
// The Library tab copies 1.0.0's connections screen: a Connections inner tab
// (add/edit/test/delete servers) plus one inner tab per installed media
// source, whose servers carry a sync. Only installed plugins offer tabs.

const LIBRARY_ROUTES = {
  jellyfin: { base: "/api/plugins/com.channelflow.jellyfin" },
};
let librarySources = [];
let libraryPage = "connections";
let libraryConnections = [];
let connectionEditingId = null;

async function loadLibrary() {
  try {
    librarySources = (await request("/api/mediasources")).sources || [];
  } catch (error) {
    librarySources = [];
  }
  populateConnectionKinds();
  await refreshLibraryConnections();
  renderLibraryTabs();
  showLibraryPage(libraryPage);
  updateLibraryEmptyState();
}

async function refreshLibraryConnections() {
  try {
    libraryConnections = (await request("/api/connections")).connections || [];
  } catch (error) {
    libraryConnections = [];
  }
}

function sourceName(kind) {
  const source = librarySources.find((entry) => entry.type_id === kind);
  return source ? source.display_name : kind;
}

function populateConnectionKinds() {
  const select = $("ms-kind");
  if (!select) return;
  select.textContent = "";
  librarySources.forEach((source) => {
    const option = document.createElement("option");
    option.value = source.type_id;
    option.textContent = source.display_name;
    select.appendChild(option);
  });
  // Disabled while no media-source plugin is installed — there is nothing to
  // pick, and the note under the form says what to do.
  select.disabled = librarySources.length === 0;
}

function updateLibraryEmptyState() {
  const note = $("ms-result");
  if (!note) return;
  if (librarySources.length === 0) {
    note.textContent =
      "No media-source plugins are installed. Install one — for example the Jellyfin Media Source — from the Plugins page, then reload.";
  } else if (note.textContent.startsWith("No media-source plugins")) {
    note.textContent = "";
  }
}

function renderLibraryTabs() {
  const tabs = $("library-inner-tabs");
  if (!tabs) return;
  tabs.textContent = "";
  const add = (page, label) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "inner-tab";
    button.dataset.libraryPage = page;
    button.textContent = label;
    button.addEventListener("click", () => showLibraryPage(page));
    tabs.appendChild(button);
  };
  add("connections", "Connections");
  // A per-source tab only exists once one of its connections is saved — no
  // empty tabs for installed-but-unconfigured sources.
  librarySources.forEach((source) => {
    if (libraryConnections.some((connection) => connection.kind === source.type_id)) {
      add(source.type_id, source.display_name);
    }
  });
  tabs.querySelectorAll(".inner-tab").forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.libraryPage === libraryPage);
  });
}

function kindPage(kind, heading) {
  const id = `library-page-${kind}`;
  let page = $(id);
  if (!page) {
    page = document.createElement("div");
    page.id = id;
    page.className = "library-page";
    page.hidden = true;
    page.innerHTML =
      `<div class="panel"><div class="panel-head"><h2></h2><span class="count"></span></div>` +
      `<p class="hint">Sync the libraries each connection exposes. They are grouped by type; toggle a row to include or exclude that library from syncs.</p>` +
      `<div class="library-lists"></div></div>`;
    $("tab-library").appendChild(page);
  }
  page.querySelector("h2").textContent = heading;
  return page;
}

function showLibraryPage(page) {
  libraryPage = page;
  document.querySelectorAll("#library-inner-tabs .inner-tab").forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.libraryPage === page);
  });
  document.querySelectorAll("#tab-library .library-page").forEach((element) => {
    element.hidden = element.id !== `library-page-${page}`;
  });
  if (page === "connections") renderConnections();
  else renderKindLibraries(page);
}

function renderCurrentLibraryPage() {
  showLibraryPage(libraryPage);
}

function setCardStatus(card, result) {
  const status = card.querySelector(".ms-status");
  if (!status) return;
  const ok = result && result.ok;
  status.className = "ms-status" + (ok ? " ok" : " bad");
  status.textContent = result.detail || (ok ? "OK" : "Failed");
}

function restoreButton(button) {
  button.textContent = button.dataset.label || button.textContent;
}

function connectionActions(card, connection) {
  const actions = document.createElement("div");
  actions.className = "ms-actions";

  const run = async (button, busyLabel, work, done) => {
    button.disabled = true;
    button.textContent = busyLabel;
    try {
      done(await work());
    } catch (error) {
      setCardStatus(card, { ok: false, detail: error.message });
      restoreButton(button);
    } finally {
      button.disabled = false;
    }
  };

  if (LIBRARY_ROUTES[connection.kind]) {
    const sync = document.createElement("button");
    sync.type = "button";
    sync.className = "primary";
    sync.dataset.label = "Sync now";
    sync.textContent = "Sync now";
    sync.onclick = () =>
      run(sync, "Syncing…", () => syncConnection(connection), (report) => {
        setCardStatus(card, {
          ok: report.errors === 0,
          detail: `Synced: ${report.added} added · ${report.updated} updated · ${report.errors} errors`,
        });
        restoreButton(sync);
      });
    actions.appendChild(sync);
  }

  const test = document.createElement("button");
  test.type = "button";
  test.dataset.label = "Test";
  test.textContent = "Test";
  test.onclick = () =>
    run(test, "Testing…",
      async () => (await request(`/api/connections/${connection.id}/test`, { method: "POST" })).result,
      (result) => { setCardStatus(card, result); restoreButton(test); });
  const edit = document.createElement("button");
  edit.type = "button";
  edit.dataset.label = "Edit";
  edit.textContent = "Edit";
  edit.onclick = () => openConnectionForm(connection);
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "danger";
  remove.dataset.label = "Delete";
  remove.textContent = "Delete";
  remove.onclick = () =>
    run(remove, "Deleting…", async () => {
      if (!confirm(`Delete this connection? Its synced rows cascade and orphan posters are swept.`)) return "cancelled";
      await request(`/api/connections/${connection.id}`, { method: "DELETE" });
      await refreshLibraryConnections();
      renderLibraryTabs();
      renderCurrentLibraryPage();
      return "deleted";
    }, () => { if (card.parentNode) card.remove(); });
  actions.append(test, edit, remove);
  return actions;
}

function connectionCard(connection) {
  const card = document.createElement("div");
  card.className = "ms-card";
  const config = connection.config || {};
  const head = document.createElement("div");
  head.className = "ms-card-head";
  const title = document.createElement("h4");
  title.textContent = config.name || "(unnamed connection)";
  const kind = document.createElement("span");
  kind.className = "ms-kind";
  kind.textContent = sourceName(connection.kind) || connection.kind;
  head.append(title, kind);
  const meta = document.createElement("div");
  meta.className = "ms-meta";
  meta.innerHTML =
    `<span>${escapeHtml(config.url || "no URL")}</span>` +
    (config.enabled === false ? `<span class="ms-status">disabled</span>` : "") +
    `<span class="ms-status">Not tested</span>`;
  card.append(head, meta, connectionActions(card, connection));
  return card;
}

function renderConnections() {
  const list = $("ms-connection-list");
  if (!list) return;
  const count = $("ms-count");
  if (count) count.textContent = `${libraryConnections.length} connection(s)`;
  list.textContent = "";
  if (!libraryConnections.length) {
    list.innerHTML = '<div class="card section-card"><p class="hint">No media server connections yet. Add one below.</p></div>';
    return;
  }
  libraryConnections.forEach((connection) => list.appendChild(connectionCard(connection)));
}

// The grouped labels and order for Jellyfin's collection types. 3D movies
// and regular movies both arrive as collection_type "movies", so they share
// the Movies box.
const LIBRARY_TYPE_LABELS = {
  movies: "Movies",
  tvshows: "TV shows",
  music: "Music",
  musicvideos: "Music videos",
};
const LIBRARY_TYPE_ORDER = ["movies", "tvshows", "music", "musicvideos"];

function libraryTypeLabel(type) {
  return LIBRARY_TYPE_LABELS[type] || type || "Other";
}

function libraryCard(html) {
  const card = document.createElement("div");
  card.className = "card section-card";
  card.innerHTML = html;
  return card;
}

async function renderKindLibraries(kind) {
  const page = kindPage(kind, sourceName(kind) || kind);
  const list = page.querySelector(".library-lists");
  const count = page.querySelector(".count");
  const rows = libraryConnections.filter((connection) => connection.kind === kind);
  list.textContent = "";
  if (!rows.length) {
    if (count) count.textContent = "";
    list.appendChild(
      libraryCard(
        '<p class="hint">No connections yet — add one on the Connections tab.</p>'
      )
    );
    return;
  }
  if (count) count.textContent = `${rows.length} connection(s)`;
  await Promise.all(rows.map((connection) => renderConnectionLibraries(list, connection)));
}

async function renderConnectionLibraries(list, connection) {
  const route = LIBRARY_ROUTES[connection.kind];
  const config = connection.config || {};
  const apiKey = config.api_key || "";
  let libraries = [];
  if (route) {
    try {
      const data = await request(route.base + "/libraries", {
        method: "POST",
        body: JSON.stringify({ connection: config, api_key: apiKey }),
      });
      libraries = data.libraries || [];
    } catch (error) {
      list.appendChild(
        libraryCard(
          `<p class="hint bad">${escapeHtml(connection.config.name || "connection")}: ${escapeHtml(error.message)}</p>`
        )
      );
      return;
    }
  }
  const enabled = new Set(
    Array.isArray(config.enabled_libraries)
      ? config.enabled_libraries
      : libraries.map((library) => library.remote_id)
  );
  const grouped = new Map();
  for (const library of libraries) {
    const type = library.collection_type || "other";
    if (!grouped.has(type)) grouped.set(type, []);
    grouped.get(type).push(library);
  }
  const types = [...grouped.keys()].sort((a, b) => {
    const ia = LIBRARY_TYPE_ORDER.indexOf(a);
    const ib = LIBRARY_TYPE_ORDER.indexOf(b);
    return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib) || String(a).localeCompare(String(b));
  });

  const box = document.createElement("div");
  box.className = "library-box";
  const name = escapeHtml(config.name || "Connection");
  const sync = LIBRARY_ROUTES[connection.kind]
    ? `<button type="button" class="primary" data-lib-sync="${connection.id}">Sync now</button>`
    : "";
  box.innerHTML = `<div class="library-box-head"><h3>${name}</h3>${sync}</div>`;
  if (!libraries.length) {
    box.innerHTML +=
      '<p class="hint">This server exposes no libraries (or the key cannot list them).</p>';
  } else {
    types.forEach((type) => {
      const label = libraryTypeLabel(type);
      box.innerHTML += `<h4 class="library-type">${escapeHtml(label)}</h4>`;
      grouped.get(type).forEach((library) => {
        const checked = enabled.has(library.remote_id);
        box.innerHTML +=
          `<div class="lib-row">
             <span class="lib-name">${escapeHtml(library.name)}</span>
             <label class="switch" title="${checked ? "Included in syncs" : "Excluded from syncs"}">
               <input type="checkbox" class="lib-toggle" data-conn="${connection.id}" data-lib="${escapeHtml(library.remote_id)}" ${checked ? "checked" : ""}>
               <span class="track"></span><span class="thumb"></span>
             </label>
           </div>`;
      });
    });
  }
  list.appendChild(box);
}

// A library toggle persisted to the connection, then re-renders so the tabs
// and any syncs reflect the new selection.
async function toggleLibrary(connectionId, remoteId, enabledNow, list) {
  const connection = libraryConnections.find((row) => row.id === connectionId);
  if (!connection) return;
  const config = { ...(connection.config || {}) };
  // Build the enabled set from what is on screen right now, so the very first
  // toggle (before any selection was stored) starts from "all of them" rather
  // than an empty list.
  const toggles = [...document.querySelectorAll(`.lib-toggle[data-conn="${connectionId}"]`)];
  config.enabled_libraries = toggles
    .filter((toggle) => toggle.checked)
    .map((toggle) => toggle.dataset.lib);
  try {
    await request(`/api/connections/${connectionId}`, {
      method: "PUT",
      body: JSON.stringify({ config }),
    });
    await refreshLibraryConnections();
    renderLibraryTabs();
    renderKindLibraries(connection.kind);
  } catch (error) {
    setCardStatus(list, { ok: false, detail: error.message });
  }
}

// The toggles and the Sync button live on the library pages.
document.getElementById("tab-library").addEventListener("change", (event) => {
  const toggle = event.target.closest(".lib-toggle");
  if (toggle) toggleLibrary(Number(toggle.dataset.conn), toggle.dataset.lib, toggle.checked, document.getElementById("tab-library"));
});
document.getElementById("tab-library").addEventListener("click", async (event) => {
  const button = event.target.closest("[data-lib-sync]");
  if (!button) return;
  const connection = libraryConnections.find((row) => row.id === Number(button.dataset.libSync));
  if (!connection) return;
  button.disabled = true;
  const original = button.textContent;
  button.textContent = "Syncing…";
  try {
    const report = await syncConnection(connection);
    const note = document.createElement("p");
    note.className = "hint" + (report.errors === 0 ? "" : " bad");
    note.textContent =
      `Synced: ${report.added} added · ${report.updated} updated · ${report.errors} errors`;
    button.parentElement.appendChild(note);
  } catch (error) {
    const note = document.createElement("p");
    note.className = "hint bad";
    note.textContent = error.message || "Sync failed.";
    button.parentElement.appendChild(note);
  } finally {
    button.disabled = false;
    button.textContent = original;
  }
});

async function syncConnection(connection) {
  const route = LIBRARY_ROUTES[connection.kind];
  if (!route) throw new Error(`no sync built for ${connection.kind}`);
  const config = connection.config || {};
  const apiKey = config.api_key || "";
  const connectionBody = { connection: config, api_key: apiKey };
  // Sync only the toggled-on libraries. With no stored selection every library
// is included; an empty stored selection means the operator turned them all
// off, so nothing syncs.
  const all = (await request(route.base + "/libraries", { method: "POST", body: JSON.stringify(connectionBody) })).libraries || [];
  const hasSelection = Array.isArray(config.enabled_libraries);
  const preferred = new Set(hasSelection ? config.enabled_libraries : []);
  const libraries = hasSelection
    ? all.filter((library) => preferred.has(library.remote_id))
    : all;
  let imageRoot = "config/Images";
  try {
    const about = await request("/api/about");
    if (about.system && about.system.configFolder) imageRoot = `${about.system.configFolder}/Images`;
  } catch (error) { /* keep the default */ }
  const report = await request(route.base + "/sync", {
    method: "POST",
    body: JSON.stringify({ connection_id: connection.id, ...connectionBody, libraries, image_root: imageRoot }),
  });
  return report.report || report;
}

// --- connection form ---

function linesToRemaps(text) {
  const remaps = {};
  String(text || "").split(/\r?\n/).forEach((line) => {
    const match = line.match(/^\s*(\S+)\s*->\s*(\S+)\s*$/);
    if (match) remaps[match[1]] = match[2];
  });
  return remaps;
}

function remapsToLines(remaps) {
  if (!remaps || typeof remaps !== "object") return "";
  return Object.entries(remaps).map(([from, to]) => `${from} -> ${to}`).join("\n");
}

function currentFormConfig() {
  return {
    name: $("ms-name").value.trim(),
    url: $("ms-url").value.trim(),
    api_key: $("ms-api-key").value.trim(),
    path_remaps: linesToRemaps($("ms-remaps").value),
    verify_tls: $("ms-verify-tls").checked,
    enabled: $("ms-enabled").checked,
  };
}

function openConnectionForm(connection) {
  connectionEditingId = connection ? connection.id : null;
  $("ms-form-title").textContent = connection ? "Edit connection" : "Add connection";
  const config = (connection && connection.config) || {};
  $("ms-kind").value = connection ? connection.kind : (librarySources[0] && librarySources[0].type_id) || "";
  $("ms-kind").disabled = !!connection;
  $("ms-name").value = config.name || "";
  $("ms-url").value = config.url || "";
  $("ms-api-key").value = config.api_key || "";
  $("ms-remaps").value = remapsToLines(config.path_remaps);
  $("ms-verify-tls").checked = config.verify_tls !== false;
  $("ms-enabled").checked = config.enabled !== false;
  $("ms-test").hidden = !connection;
  $("ms-cancel").hidden = !connection;
  $("ms-result").textContent = "";
  $("ms-form").scrollIntoView({ block: "nearest" });
  $("ms-name").focus();
}

async function saveConnection(event) {
  event.preventDefault();
  const kind = $("ms-kind").value;
  const config = currentFormConfig();
  const save = $("ms-save");
  save.disabled = true;
  try {
    await request(connectionEditingId ? `/api/connections/${connectionEditingId}` : "/api/connections", {
      method: connectionEditingId ? "PUT" : "POST",
      body: JSON.stringify(connectionEditingId ? { config } : { kind, config }),
    });
    openConnectionForm(null);
    $("ms-result").textContent = "Saved.";
    await refreshLibraryConnections();
    renderLibraryTabs();
    renderCurrentLibraryPage();
  } catch (error) {
    $("ms-result").textContent = error.message;
  } finally {
    save.disabled = false;
  }
}

async function testConnectionForm() {
  if (!connectionEditingId) return;
  try {
    await request(`/api/connections/${connectionEditingId}`, { method: "PUT", body: JSON.stringify({ config: currentFormConfig() }) });
    const data = await request(`/api/connections/${connectionEditingId}/test`, { method: "POST" });
    const result = data.result || {};
    $("ms-result").textContent = `${result.ok ? "OK" : "Failed"}: ${result.detail || ""}`;
  } catch (error) {
    $("ms-result").textContent = error.message;
  }
}

{
  const form = $("ms-form");
  if (form) form.addEventListener("submit", saveConnection);
  const test = $("ms-test");
  if (test) test.addEventListener("click", testConnectionForm);
  const cancel = $("ms-cancel");
  if (cancel) cancel.addEventListener("click", () => openConnectionForm(null));
}

document.querySelectorAll(".drawer-nav a").forEach((link) => {
  link.addEventListener("click", (event) => {
    // Only intercept the tab links. The brand link (no data-tab) must do a
    // real navigation: a full load of / is what leaves the walkthrough for the
    // app once setup is done. Swallowing it and assigning location.href leaves
    // the address bar on /first-time, and boot() bounces straight back.
    if (!link.dataset.tab) return;
    event.preventDefault();
    history.pushState(null, "", link.getAttribute("href") || `/webui/${link.dataset.tab}`);
    showTab(link.dataset.tab);
    link.scrollIntoView({ block: "nearest", inline: "nearest" });
  });
});

// Back and forward hop between tabs exactly like pages: each tab has its own
// URL, and the browser history moves through them.
window.addEventListener("popstate", () => {
  const key = tabForPath(location.pathname);
  if (key) showTab(key);
});

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
  load();
  loadPluginPages();
  showTab(tabForPath(location.pathname) || "guide");
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

// Each walkthrough step has its own URL under /first-time (e.g.
// /first-time/database), so a step can be deep-linked and the address bar
// always says where you are.
const OB_STEP_PATHS = [
  "welcome",
  "plugins",
  "database",
  "transcoding",
  "media-source",
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

document.getElementById("ob-next").addEventListener("click", () => {
  if (obIndex < OB.length - 1) {
    obIndex++;
    renderOnboarding();
  } else {
    // Final step: land on /webui/guide. boot() will show the login screen
    // (no session yet) and, once the password checks out, the Guide.
    location.assign("/webui/guide");
  }
});

// ── login and forgotten password ────────────────────────────────────────────
// Once setup is complete the API (and the app) locks behind a password. The
// reset pin is written to a file only in the server's config directory, so
// nothing sensitive travels through this page. Resets are on a 10-minute
// cooldown.

document.getElementById("login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const username = document.getElementById("login-username").value.trim();
  const password = document.getElementById("login-password").value;
  setLoginNote("");
  try {
    await request("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ username, password }),
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

// ── log out ───────────────────────────────────────────────────────────────────
// Every /webui page has a Log out button in the top right. Clearing the
// session returns to the login screen.

document.getElementById("logout").addEventListener("click", async () => {
  try {
    await request("/api/auth/logout", { method: "POST" });
  } catch (error) {
    // Logging out is best-effort; even if the call fails the session is gone
    // once setup is re-entered, and a reload shows login either way.
  }
  location.replace("/");
});

const UI_BUILD = "37";

// There is no login screen: an unreachable server never has a reason to show a
// password form, so the walkthrough appears with the error instead.
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

// A server restart can take a moment; don't let a transient miss decide what
// the page shows. Try the state check a few times before giving up.
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
    // The server wasn't reachable. On /first-time show the walkthrough with
    // the error; anywhere else the page was served, so show the app shell.
    if (onFirstTime) {
      showSetupUnreachable(new Error("the server is not responding"));
    } else {
      showApp();
    }
    return;
  }
  if (!state.setup_done) {
    // Not set up yet: the walkthrough. Anything other than /first-time
    // redirects there (the server does this too).
    if (!onFirstTime) {
      location.replace("/first-time");
      return;
    }
    startOnboarding();
    return;
  }
  if (onFirstTime) {
    // Setup finished; /first-time is no longer ours. The app (or login for
    // the app) lives under /webui.
    location.replace(APP_HOME);
    return;
  }
  if (!state.authenticated) {
    // Setup is done but there is no session: the login screen. No state, no
    // app until the password checks out.
    showLogin();
    return;
  }
  showApp();
}

boot();
