// Lineups, laid out the way 1.0.0's schedule editor was: a channel picker and
// preview date up top, a Save Lineup / Add Override toolbar, a 48 half-hour
// grid of clickable slot cards, a modal slot editor (weighted candidates,
// rerun, block length), and a Week lineups row — each override is a card that
// loads that day's template into the grid. The roster document is the base's
// own; playout reads it later.

const LINEUPS_API = "/api/lineups";
const SLOTS_PER_DAY = 48;
const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const KIND_TABS = { movie: "Movies", series: "TV Shows", album: "Music", artist: "Music", musicvideo: "Music Videos", news: "News" };

let lineupChannels = [];
let selectedChannelId = null;
// The stored roster: the default day plus the overrides.
let lineupRoster = { slots: [], overrides: [] };
// The slots the grid is editing — a live reference into the default day or an
// override, so Save writes exactly what is on screen.
let lineupSlots = [];
let selectedLineupOverrideId = null; // null = the default day

function fmtNum(value) {
  return Number.isInteger(value) ? String(value) : String(value);
}
function slotTime(index) {
  const minutes = Math.max(0, Math.min(SLOTS_PER_DAY - 1, index)) * 30;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  const period = h >= 12 ? "PM" : "AM";
  const hour12 = h % 12 === 0 ? 12 : h % 12;
  return `${hour12}:${String(m).padStart(2, "0")} ${period}`;
}
function todayIso() {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}
function emptySlots() {
  return Array.from({ length: SLOTS_PER_DAY }, (_, i) => ({
    slot_index: i, span_slots: 1, is_rerun: false, candidates: [],
  }));
}
function compactSlots(slots) {
  const copy = (slots || []).map((s) => ({
    slot_index: s.slot_index,
    span_slots: s.span_slots || 1,
    is_rerun: !!s.is_rerun,
    candidates: (s.candidates || []).map((c) => ({ ...c })),
  }));
  copy.sort((a, b) => a.slot_index - b.slot_index);
  return copy.length ? copy : emptySlots();
}
function spanLabel(span) {
  if (span <= 1) return "";
  if (span === 2) return " · 1h";
  return ` · ${span * 30}m`;
}
function candidateSummary(candidate) {
  if (!candidate) return "";
  return candidate.title || `Item ${candidate.match_key || ""}`.trim();
}

function setLineupResult(message, bad) {
  const el = $("lineup-result");
  if (el) {
    el.textContent = message || "";
    el.className = bad ? "field-note bad" : "field-note";
  }
}

// --- Loading ---------------------------------------------------------------

async function loadLineups() {
  try {
    const data = await request(LINEUPS_API);
    lineupChannels = (data && data.channels) || [];
    const count = $("lineup-count");
    if (count) count.textContent = lineupChannels.length ? `${lineupChannels.length} channel${lineupChannels.length === 1 ? "" : "s"}` : "";
    const select = $("lineup-channel-select");
    select.replaceChildren();
    lineupChannels.forEach((channel) => {
      const option = document.createElement("option");
      option.value = channel.id;
      option.textContent = `${fmtNum(channel.number)} · ${channel.name}` +
        (!channel.enabled ? " (disabled)" : "") + (channel.has_lineup ? "" : " — default");
      select.appendChild(option);
    });
    if (lineupChannels.length) {
      if (!selectedChannelId || !lineupChannels.some((c) => c.id === selectedChannelId)) {
        selectedChannelId = lineupChannels[0].id;
      }
      select.value = selectedChannelId;
      const date = $("lineup-preview-date");
      if (date && !date.value) date.value = todayIso();
      await loadChannelLineup(selectedChannelId);
    }
  } catch (error) {
    setLineupResult(error.message, true);
  }
}

async function loadChannelLineup(id) {
  selectedChannelId = id;
  selectedLineupOverrideId = null;
  try {
    const data = await request(`${LINEUPS_API}/${id}`);
    lineupRoster = (data && data.lineup) || { slots: [], overrides: [] };
    lineupSlots = compactSlots(lineupRoster.slots);
    lineupRoster.slots = lineupSlots;
    renderDefaultState();
    renderGrid();
    renderOverrideList();
    hidePreviewBanner();
    setLineupResult("");
  } catch (error) {
    setLineupResult(error.message, true);
  }
}

function renderDefaultState() {
  $("lineup-editor-title").textContent = "Default day";
  $("lineup-hint").textContent =
    "Click a 30-minute slot to edit candidates. A block can span several slots. Check Rerun to fill the slot from the previous day's primetime.";
}

// --- The grid --------------------------------------------------------------

function renderGrid() {
  const grid = $("lineup-grid");
  if (!grid) return;
  const occupied = new Array(SLOTS_PER_DAY).fill(false);
  const byIndex = {};
  lineupSlots.forEach((slot) => { byIndex[slot.slot_index] = slot; });

  let html = "";
  for (let i = 0; i < SLOTS_PER_DAY; i++) {
    if (occupied[i]) continue;
    const slot = byIndex[i] || { slot_index: i, span_slots: 1, candidates: [] };
    slot.span_slots = slot.span_slots || 1;
    const count = (slot.candidates || []).length;
    const rerun = !!slot.is_rerun;
    for (let j = i; j < i + slot.span_slots && j < SLOTS_PER_DAY; j++) occupied[j] = true;

    const summary = rerun
      ? "Rerun · yesterday primetime"
      : (count ? candidateSummary(slot.candidates[0]) : "Empty slot");
    const classes = ["slot-card", rerun ? "rerun-slot" : (count ? "has-items" : "empty"), slot.span_slots > 1 ? "span-block" : ""]
      .filter(Boolean)
      .join(" ");
    const style = slot.span_slots > 1 ? `style="grid-column: span ${slot.span_slots}"` : "";
    html += `<button type="button" class="${classes}" data-slot="${slot.slot_index}" ${style} role="gridcell">` +
      `<span class="time">${slotTime(slot.slot_index)}${spanLabel(slot.span_slots)}</span>` +
      `<span class="summary">${escapeHtml(summary)}</span>` +
      `<span class="count">${rerun ? "Rerun slot" : `${count} candidate${count === 1 ? "" : "s"}`}</span>` +
      `</button>`;
  }
  grid.innerHTML = html;
  grid.querySelectorAll(".slot-card").forEach((card) => {
    card.addEventListener("click", () => openSlotEditor(Number(card.dataset.slot)));
  });
}

// --- The slot editor (modal) ----------------------------------------------

function openSlotEditor(index) {
  let slot = lineupSlots.find((s) => s.slot_index === index);
  if (!slot) {
    slot = { slot_index: index, span_slots: 1, is_rerun: false, candidates: [] };
    lineupSlots.push(slot);
    lineupSlots.sort((a, b) => a.slot_index - b.slot_index);
  }
  const working = {
    slot_index: slot.slot_index,
    span_slots: slot.span_slots || 1,
    is_rerun: !!slot.is_rerun,
    candidates: (slot.candidates || []).map((c) => ({ ...c })),
  };

  const shell = lineupModalShell();
  const head = `<h2>Slot ${slotTime(index)}</h2>` +
    `<p class="hint">Editing ${slotTime(index)} · add multiple weighted candidates for smart rotation.</p>`;
  const body =
    `<label class="field checkbox-field"><input id="slot-rerun" type="checkbox"${working.is_rerun ? " checked" : ""}><span>Rerun slot</span></label>` +
    `<p class="hint">Highlighted on the grid. Playout fills the slot from the previous day's primetime; candidates below are used only when no rerun is available.</p>` +
    `<label class="field"><span class="field-label">Block length (30-min slots)</span>` +
    `<input id="slot-span-slots" type="number" min="1" max="48" step="1" value="${working.span_slots}"></label>` +
    `<div id="slot-candidates" class="candidate-list"></div>` +
    `<div class="field"><span class="field-label">Add candidate</span>` +
    `<select id="slot-add-kind"><option value="text">Text</option><option value="item">Catalog item</option></select></div>` +
    `<div id="slot-add-panel"></div>` +
    `<p class="hint" id="lineup-slot-note"></p>`;
  const actions =
    `<button type="button" class="ghost" data-close>Cancel</button>` +
    `<button type="button" class="primary" id="slot-save">Save Slot</button>`;
  shell.innerHTML = lineupModalFrame(head, body, actions);

  const candidates = $("slot-candidates");
  function renderCandidates() {
    candidates.replaceChildren();
    working.candidates.forEach((candidate, index) => {
      const row = document.createElement("div");
      row.className = "candidate-row";
      const title = document.createElement("span");
      title.className = "cand-title";
      title.textContent = candidate.title || "Untitled";
      const meta = document.createElement("span");
      meta.className = "cand-meta";
      meta.textContent =
        (candidate.kind === "item" ? "catalog item" : "text") +
        ` · weight ${candidate.weight || 1}`;
      const remove = document.createElement("button");
      remove.type = "button";
      remove.textContent = "Remove";
      remove.addEventListener("click", () => {
        working.candidates.splice(index, 1);
        renderCandidates();
      });
      row.append(title, meta, remove);
      candidates.appendChild(row);
    });
    if (!working.candidates.length) {
      const empty = document.createElement("p");
      empty.className = "hint";
      empty.textContent = "No candidates yet — a weighted pick between several makes for smart rotation.";
      candidates.appendChild(empty);
    }
  }
  renderCandidates();

  const addPanel = $("slot-add-panel");
  function renderAddPanel() {
    const kind = $("slot-add-kind").value;
    if (kind === "text") {
      addPanel.innerHTML =
        `<label class="field"><span class="field-label">Title</span>` +
        `<input id="slot-text-title" type="text" placeholder="e.g. Groundhog Day (1993)"><div class="actions">` +
        `<button type="button" class="ghost" id="slot-add-text">Add text candidate</button></div></label>`;
      $("slot-add-text").addEventListener("click", () => {
        const title = $("slot-text-title").value.trim();
        if (!title) return;
        working.candidates.push({ kind: "text", title, weight: 1 });
        renderCandidates();
      });
    } else {
      addPanel.innerHTML =
        `<label class="field"><span class="field-label">Search the catalog</span>` +
        `<input id="slot-search" type="search" placeholder="Type at least 2 characters…"></label>` +
        `<div id="slot-search-results" class="search-results"></div>` +
        `<p class="hint">Picks a media item from the base catalog; playout will resolve it to a real file.</p>`;
      let timer;
      $("slot-search").addEventListener("input", (event) => {
        clearTimeout(timer);
        timer = setTimeout(() => searchCatalog(event.target.value), 250);
      });
    }
  }
  $("slot-add-kind").addEventListener("change", renderAddPanel);
  renderAddPanel();

  async function searchCatalog(query) {
    const panel = $("slot-search-results");
    query = (query || "").trim();
    if (query.length < 2) {
      panel.replaceChildren();
      return;
    }
    let items = [];
    try {
      const data = await request(`/api/media?q=${encodeURIComponent(query)}&limit=12`);
      items = (data && data.items) || [];
    } catch (error) {
      items = [];
    }
    panel.replaceChildren();
    if (!items.length) {
      const none = document.createElement("p");
      none.className = "hint";
      none.textContent = "No matches.";
      panel.appendChild(none);
      return;
    }
    items.forEach((item) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "search-result";
      button.textContent =
        `${item.title}${item.year ? ` (${item.year})` : ""} · ${KIND_TABS[item.kind] || item.kind}`;
      button.addEventListener("click", () => {
        working.candidates.push({
          kind: "item",
          match_key: item.match_key,
          title: item.title,
          weight: 1,
        });
        renderCandidates();
        $("slot-search").value = "";
        searchCatalog("");
      });
      panel.appendChild(button);
    });
  }

  $("slot-save").addEventListener("click", () => {
    const span = Math.max(1, Number($("slot-span-slots").value) || 1);
    working.is_rerun = $("slot-rerun").checked;
    // A span can never push past the next block's start.
    const next = lineupSlots
      .filter((candidate) => candidate.slot_index !== working.slot_index && candidate.slot_index > working.slot_index)
      .reduce((min, candidate) => Math.min(min, candidate.slot_index), SLOTS_PER_DAY);
    working.span_slots = Math.min(span, Math.max(1, next - working.slot_index));
    // Extending over blocks below consumes them (merge), like resizing in v1.
    lineupSlots = lineupSlots
      .filter((candidate) => candidate.slot_index === working.slot_index || candidate.slot_index >= working.slot_index + working.span_slots);
    Object.assign(slot, working);
    lineupSlots.sort((a, b) => a.slot_index - b.slot_index);
    $("#lineup-slot-note").textContent = span > working.span_slots
      ? `Block ends at ${slotTime(working.slot_index + working.span_slots)} (clamped to the next block).`
      : "";
    closeLineupModal();
    renderGrid();
  });

  shell.querySelector("[data-close]").addEventListener("click", closeLineupModal);
  openLineupModal(shell);
}

// --- Week lineups (overrides) ----------------------------------------------

function renderOverrideList() {
  const list = $("override-list");
  if (!list) return;

  const defaultCard = overrideCardHtml({
    id: null,
    name: "Default day",
    meta: "Every day not listed below",
    filled: rosterFilledCount(lineupRoster.slots, true),
  }, selectedLineupOverrideId === null);
  const overrides = (lineupRoster.overrides || []).map((o) => overrideCardHtml({
    id: o.id,
    name: o.name,
    meta: overrideWhen(o),
    filled: rosterFilledCount(o.slots, false),
  }, selectedLineupOverrideId === o.id));

  list.innerHTML = defaultCard + (overrides.join("") || '<div class="empty-state">No week lineups yet — Add Override to give a day its own lineup.</div>');
  list.querySelectorAll(".override-card").forEach((card) => {
    card.addEventListener("click", () => {
      const id = card.dataset.overrideId || null;
      if (id === null) {
        lineupSlots = compactSlots(lineupRoster.slots);
        lineupRoster.slots = lineupSlots;
        selectedLineupOverrideId = null;
        renderDefaultState();
      } else {
        const override = lineupRoster.overrides.find((o) => o.id === id);
        if (!override) return;
        lineupSlots = compactSlots(override.slots);
        override.slots = lineupSlots;
        selectedLineupOverrideId = id;
        $("lineup-editor-title").textContent = `Week lineup — ${override.name || "…"}`;
        $("lineup-hint").textContent = `Showing ${override.name || overrideWhen(override)}. Click a 30-minute slot to edit candidates. Save Lineup writes this day's template.`;
      }
      renderGrid();
      renderOverrideList();
      hidePreviewBanner();
    });
    const del = card.querySelector("[data-delete-override]");
    if (del) del.addEventListener("click", (event) => {
      event.stopPropagation();
      const id = card.dataset.overrideId;
      if (id && confirm("Delete this week lineup and its slots?")) deleteOverride(id);
    });
  });
}

function overrideCardHtml(entry, selected) {
  return `<div class="override-card${selected ? " selected" : ""}" data-override-id="${entry.id || ""}" role="button" tabindex="0" aria-pressed="${selected ? "true" : "false"}">` +
    `<strong>${escapeHtml(entry.name)}</strong>` +
    `<div class="meta">${escapeHtml(entry.meta)} · ${entry.filled} filled slots</div>` +
    (entry.id ? `<div class="row-actions"><button type="button" data-delete-override="${entry.id}">Delete</button></div>` : "") +
    `</div>`;
}

function overrideWhen(override) {
  if (override.kind === "specific_date" && override.specific_date) return override.specific_date;
  if (override.day_of_week !== undefined && override.day_of_week !== null) return DAY_NAMES[override.day_of_week] || "?day";
  return "Schedule";
}

function rosterFilledCount(slots, isDefault) {
  return (slots || []).filter((s) => (s.candidates || []).length).length;
}

function addOverrideFlow() {
  const shell = lineupModalShell();
  const head = `<h2>Add Override</h2>` +
    `<p class="hint">A day that doesn't follow this channel's default lineup. It starts as a copy of the default day.</p>`;
  const body =
    `<label class="field"><span class="field-label">Name</span>` +
    `<input id="override-name" type="text" placeholder="e.g. Football Sunday"></label>` +
    `<label class="field"><span class="field-label">Applies to</span>` +
    `<select id="override-kind"><option value="day_of_week">Day of week</option><option value="specific_date">Specific date</option></select></label>` +
    `<div id="override-when">` + dayOfWeekSelect() + `</div>` +
    `<p class="hint" id="lineup-slot-note"></p>`;
  const actions =
    `<button type="button" class="ghost" data-close>Cancel</button>` +
    `<button type="button" class="primary" id="override-add">Add Override</button>`;
  shell.innerHTML = lineupModalFrame(head, body, actions);
  $("override-kind").addEventListener("change", () => {
    $("override-when").innerHTML = $("override-kind").value === "day_of_week" ? dayOfWeekSelect() :
      `<label class="field"><span class="field-label">Date</span><input id="override-date" type="date" value="${$("lineup-preview-date").value || todayIso()}"></label>`;
  });
  $("override-add").addEventListener("click", async () => {
    const kind = $("override-kind").value;
    const name = $("override-name").value.trim() || (kind === "day_of_week" ? "Day override" : "Date override");
    const body = { name, kind, slots: snapshot(lineupRoster.slots) };
    if (kind === "day_of_week") body.day_of_week = Number($("override-day").value);
    else body.specific_date = $("override-date").value;
    try {
      const data = await request(`${LINEUPS_API}/${selectedChannelId}/overrides`, {
        method: "POST",
        body: JSON.stringify(body),
      });
      lineupRoster = data.lineup;
      lineupSlots = compactSlots(lineupRoster.slots);
      lineupRoster.slots = lineupSlots;
      selectedLineupOverrideId = null;
      renderDefaultState();
      renderGrid();
      renderOverrideList();
      closeLineupModal();
      setLineupResult("Override added — click its card to edit that day.");
    } catch (error) {
      $("#lineup-slot-note").textContent = error.message;
    }
  });
  shell.querySelector("[data-close]").addEventListener("click", closeLineupModal);
  openLineupModal(shell);
}

function dayOfWeekSelect() {
  return `<label class="field"><span class="field-label">Day</span><select id="override-day">` +
    DAY_NAMES.map((name, index) => `<option value="${index}">${name}</option>`).join("") +
    `</select></label>`;
}

async function deleteOverride(id) {
  try {
    await request(`${LINEUPS_API}/overrides/${id}`, { method: "DELETE" });
    await loadChannelLineup(selectedChannelId);
    renderOverrideList();
    setLineupResult("Week lineup deleted.");
  } catch (error) {
    setLineupResult(error.message, true);
  }
}

// --- Saving ----------------------------------------------------------------

async function saveLineup() {
  try {
    let data;
    if (selectedLineupOverrideId) {
      const override = lineupRoster.overrides.find((o) => o.id === selectedLineupOverrideId);
      if (!override) throw new Error("That week lineup is gone — reload the page.");
      data = await request(`${LINEUPS_API}/overrides/${override.id}`, {
        method: "PUT",
        body: JSON.stringify({
          name: override.name,
          kind: override.kind,
          day_of_week: override.day_of_week,
          specific_date: override.specific_date,
          slots: compactSlots(lineupSlots),
        }),
      });
    } else {
      data = await request(`${LINEUPS_API}/${selectedChannelId}`, {
        method: "PUT",
        body: JSON.stringify({ slots: compactSlots(lineupSlots) }),
      });
    }
    lineupRoster = data.lineup;
    lineupSlots = compactSlots(lineupRoster.slots);
    lineupRoster.slots = lineupSlots;
    if (selectedLineupOverrideId) {
      const override = lineupRoster.overrides.find((o) => o.id === selectedLineupOverrideId);
      if (override) override.slots = lineupSlots;
    }
    renderGrid();
    renderOverrideList();
    setLineupResult("Saved.");
  } catch (error) {
    setLineupResult(error.message, true);
  }
}

// --- Preview ---------------------------------------------------------------

async function runPreview() {
  const date = $("lineup-preview-date").value;
  if (!date || !selectedChannelId) return;
  try {
    const data = await request(`${LINEUPS_API}/${selectedChannelId}/preview`, {
      method: "POST",
      body: JSON.stringify({ date }),
    });
    const filled = (data.slots || []).filter((s) => (s.candidates || []).length).length;
    const overrideName = data.override ? data.override.name : null;
    const banner = $("lineup-preview-banner");
    banner.hidden = false;
    banner.textContent = `Preview for ${data.date}: ${filled}/48 slots have candidates.` +
      (overrideName ? ` This day uses the “${overrideName}” week lineup.` : "");
  } catch (error) {
    const banner = $("lineup-preview-banner");
    banner.hidden = false;
    banner.textContent = error.message;
  }
}

function hidePreviewBanner() {
  const banner = $("lineup-preview-banner");
  if (banner) banner.hidden = true;
}

// --- The modal -------------------------------------------------------------

function lineupModalShell() {
  let shell = $("lineup-modal");
  if (!shell) {
    shell = document.createElement("div");
    shell.id = "lineup-modal";
    shell.className = "lineup-modal";
    shell.hidden = true;
    shell.addEventListener("click", (event) => {
      if (event.target === shell) closeLineupModal();
    });
    $("page").appendChild(shell);
  }
  return shell;
}

function lineupModalFrame(head, body, actions) {
  return `<div class="lineup-modal-box" role="dialog" aria-modal="true">` +
    `<div class="dialog-head"><div>${head}</div><button type="button" class="ghost" data-close>Close</button></div>` +
    `${body}<div class="actions dialog-actions">${actions}</div></div>`;
}

function openLineupModal(shell) {
  shell.hidden = false;
}
function closeLineupModal() {
  const shell = $("lineup-modal");
  if (shell) shell.hidden = true;
}

function snapshot(value) {
  return JSON.parse(JSON.stringify(value));
}

// --- Wiring ----------------------------------------------------------------

function wireLineups() {
  $("lineup-channel-select").addEventListener("change", (event) => {
    if (event.target.value) loadChannelLineup(event.target.value);
  });
  $("btn-save-lineup").addEventListener("click", saveLineup);
  $("btn-add-override").addEventListener("click", addOverrideFlow);
  $("lineup-preview-run").addEventListener("click", runPreview);
}

CF.define("lineups", { onShow: loadLineups });
wireLineups();