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

CF.define("ai", { onShow: () => loadAi() });
