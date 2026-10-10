const CB_API = "/api/plugins/com.channelflow.commercialbrainz";

function cbBoolField(id) { const el = $(id); return el ? el.checked : false; }
function cbIntField(id) {
  const value = $(id) ? $(id).value.trim() : "";
  return value === "" ? null : Number(value);
}
function cbListField(id) {
  const value = $(id) ? $(id).value : "";
  return value.split(",").map((part) => part.trim()).filter(Boolean);
}

function cbPoolLabel(value) {
  const labels = {
    jellyfin_only: "Jellyfin only",
    commercialbrainz_only: "CommercialBrainz only",
    both: "Both",
  };
  return labels[value] || String(value);
}

function fillCbSelect(id, values, selected) {
  const select = $(id);
  if (!select || !values) return;
  select.textContent = "";
  values.forEach((value) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = cbPoolLabel(value);
    select.appendChild(option);
  });
  if (selected) select.value = selected;
}

function setCbValue(id, value) {
  const el = $(id);
  if (!el) return;
  if (Array.isArray(value)) el.value = value.join(", ");
  else if (value === null || value === undefined) el.value = "";
  else el.value = String(value);
}

// Settings arrive from the plugin; every field the form has is filled from
// them, and unknown-extra ones are ignored.
function applyCbSettings(settings) {
  if (!settings) return;
  fillCbSelect("cb-pool-mode", ["jellyfin_only", "commercial_brainz_only", "both"], settings.pool_mode);
  setCbValue("cb-base-url", settings.base_url);
  setCbValue("cb-api-token", settings.api_token);
  setCbValue("cb-max-sync", settings.max_sync_results);
  setCbValue("cb-min-year", settings.min_year);
  setCbValue("cb-max-year", settings.max_year);
  setCbValue("cb-decades", settings.decades);
  setCbValue("cb-brands", settings.brands);
  setCbValue("cb-tags", settings.tags);
  setCbValue("cb-exclude-tags", settings.exclude_tags);
  setCbValue("cb-genres", settings.genres);
  setCbValue("cb-networks", settings.networks);
  setCbValue("cb-channel-names", settings.channel_names);
  const checks = {
    "cb-enabled": "enabled",
    "cb-allow-spoof": "allow_spoof",
    "cb-allow-fake": "allow_fake",
    "cb-allow-real": "allow_real",
    "cb-allow-ai": "allow_ai_enhanced",
    "cb-allow-latenight": "allow_late_night",
    "cb-allow-adult": "allow_adult_rated",
    "cb-allow-banned": "allow_banned",
  };
  Object.entries(checks).forEach(([id, key]) => {
    const el = $(id);
    if (el) el.checked = settings[key] !== false;
  });
  const status = $("cb-status");
  if (status) status.textContent = settings.enabled === false ? "disabled" : "";
}

function collectCbSettings() {
  return {
    enabled: cbBoolField("cb-enabled"),
    base_url: $("cb-base-url").value.trim(),
    api_token: $("cb-api-token").value,
    pool_mode: $("cb-pool-mode").value,
    max_sync_results: Number($("cb-max-sync").value) || 500,
    min_year: cbIntField("cb-min-year"),
    max_year: cbIntField("cb-max-year"),
    decades: cbListField("cb-decades").map(Number),
    brands: cbListField("cb-brands"),
    tags: cbListField("cb-tags"),
    exclude_tags: cbListField("cb-exclude-tags"),
    genres: cbListField("cb-genres"),
    networks: cbListField("cb-networks"),
    channel_names: cbListField("cb-channel-names"),
    allow_spoof: cbBoolField("cb-allow-spoof"),
    allow_fake: cbBoolField("cb-allow-fake"),
    allow_real: cbBoolField("cb-allow-real"),
    allow_ai_enhanced: cbBoolField("cb-allow-ai"),
    allow_late_night: cbBoolField("cb-allow-latenight"),
    allow_adult_rated: cbBoolField("cb-allow-adult"),
    allow_banned: cbBoolField("cb-allow-banned"),
  };
}

async function loadCommercialBrainz() {
  try {
    const data = await request(CB_API + "/");
    if (data.options && data.options.pool_modes) {
      fillCbSelect("cb-pool-mode", data.options.pool_modes, data.settings && data.settings.pool_mode);
    }
    applyCbSettings(data.settings);
    $("cb-result").textContent = "";
  } catch (error) {
    $("cb-result").textContent = error.message;
  }
}

async function saveCommercialBrainz(event) {
  event.preventDefault();
  const save = $("cb-save");
  save.disabled = true;
  try {
    await request(CB_API + "/", { method: "PUT", body: JSON.stringify(collectCbSettings()) });
    $("cb-result").textContent = "Saved.";
  } catch (error) {
    $("cb-result").textContent = error.message;
  } finally {
    save.disabled = false;
  }
}

async function testCommercialBrainz() {
  const test = $("cb-test");
  test.disabled = true;
  test.textContent = "Testing…";
  try {
    // Save first so the server tests what the form holds.
    await request(CB_API + "/", { method: "PUT", body: JSON.stringify(collectCbSettings()) });
    const data = await request(CB_API + "/test");
    const result = data.result || {};
    $("cb-result").textContent = `${result.ok ? "OK" : "Failed"}: ${result.detail || ""}`;
  } catch (error) {
    $("cb-result").textContent = error.message;
  } finally {
    test.disabled = false;
    test.textContent = "Test connection";
  }
}

{
  const form = $("cb-form");
  if (form) form.addEventListener("submit", saveCommercialBrainz);
  const test = $("cb-test");
  if (test) test.addEventListener("click", testCommercialBrainz);
}


CF.define("commercialbrainz", { onShow: loadCommercialBrainz });
