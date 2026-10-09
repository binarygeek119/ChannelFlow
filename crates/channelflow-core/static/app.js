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
  status: $("status"),
  version: $("version"),
  pageTitle: $("page-title"),
  pageSubtitle: $("page-subtitle"),
  tabChannels: $("tab-channels"),
  tabAbout: $("tab-about"),
  tabCredits: $("tab-credits"),
  tabAi: $("tab-ai"),
  tabTranscode: $("tab-transcode"),
  tabLiveTv: $("tab-live-tv"),
  tabPlugins: $("tab-plugins"),
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
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
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
    els.version.textContent = health.version;
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

// Plugin-declared pages add their own drawer entries. The catalog only lists
// installed plugins, so an AI plugin that is not installed leaves no AI tab —
// exactly the same rule that governs the connection types.
async function loadPluginPages() {
  let plugins;
  try {
    plugins = (await request("/api/plugins")).plugins || [];
  } catch (error) {
    return;
  }
  const nav = document.getElementById("drawer-nav");
  if (!nav) return;
  plugins.forEach((plugin) => {
    (plugin.ui_contributions || []).forEach((contribution) => {
      if (!contribution || contribution.type !== "page") return;
      const key = contribution.id;
      if (!key) return;
      if (MENU[key]) return; // a core page keeps precedence
      MENU[key] = [contribution.title || key, plugin.description || ""];
      PANEL_FOR[key] = PLUGIN_PANELS[contribution.component] || "tab-placeholder";
      const link = document.createElement("a");
      link.className = "nav-item";
      link.dataset.tab = key;
      link.href = contribution.path || `/${key}`;
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
        showTab(key);
      });
      nav.appendChild(link);
    });
  });
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
      return `<tr>
        <td><span class="channel-name">${escapeHtml(plugin.name)}</span>
            <div class="channel-desc">${escapeHtml(plugin.id)} · ${status}</div></td>
        <td>${escapeHtml(plugin.category || "—")}</td>
        <td>${escapeHtml(plugin.version)}</td>
        <td>${plugin.permissions.length}</td>
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
      return `<article class="plugin-card">
        ${banner}
        <div class="plugin-card-body">
          <h4>${escapeHtml(plugin.name)}</h4>
          <p class="plugin-card-meta">${escapeHtml(plugin.category || "plugin")} · v${escapeHtml(latest)} · ${escapeHtml(plugin.owner || "")}</p>
          <p class="channel-desc">${escapeHtml(plugin.description)}</p>
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
}

document.querySelectorAll(".drawer-nav a").forEach((link) => {
  link.addEventListener("click", (event) => {
    event.preventDefault();
    showTab(link.dataset.tab);
    link.scrollIntoView({ block: "nearest", inline: "nearest" });
  });
});

// --- First boot: the walkthrough and the login screen -----------------------
// boot() runs once on load. No setup yet → the walkthrough; setup done but no
// session → log in; otherwise the app. The API is open while setup is not
// complete, so this works before any account exists.

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
      `<label class="field-label" for="ob-user">Username</label>
       <input id="ob-user" type="text" autocomplete="username" spellcheck="false">
       <label class="field-label" for="ob-pass">Password</label>
       <input id="ob-pass" type="password" autocomplete="new-password">
       <label class="field-label" for="ob-pass2">Confirm password</label>
       <input id="ob-pass2" type="password" autocomplete="new-password">
       <div class="ob-action"><button type="button" class="primary" id="ob-do">Create account</button></div>`,
    after: () => attachObAccount(),
  },
  {
    next: () => true,
    body: () =>
      `<p>That's it — the ErsatzTV engine and the Jellyfin media source are installed, and your account is ready. Log in to start using ChannelFlow.</p>`,
  },
];

function renderOnboarding() {
  renderObSteps();
  const step = OB[obIndex];
  document.getElementById("ob-body").innerHTML = step.body();
  document.getElementById("ob-back").hidden = obIndex === 0;
  const $next = document.getElementById("ob-next");
  $next.textContent = obIndex === OB.length - 1 ? "Log in" : "Next";
  $next.hidden = obIndex === OB.length - 1;
  refreshObNext();
  if (step.after) step.after();
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

function startOnboarding() {
  hideScreens();
  obIndex = 0;
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
    showLogin();
  }
});

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
    location.reload();
  } catch (error) {
    setLoginNote(error.message || "Wrong username or password.", true);
  }
});

async function boot() {
  try {
    const state = await request("/api/auth/state");
    if (!state.setup_done) {
      startOnboarding();
      return;
    }
    if (!state.authenticated) {
      showLogin();
      return;
    }
    showApp();
  } catch (error) {
    showLogin("Could not reach ChannelFlow.");
  }
}

boot();
