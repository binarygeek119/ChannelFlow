const OFFAIR_API = "/api/plugins/com.channelflow.offair";

function offAirLabel(value) {
  const labels = {
    slate_image: "Slate image",
    color_bars: "Color bars",
    static: "TV static",
    usa: "USA slate",
    international: "World slate",
    background_music: "Background music",
    white_noise: "White noise",
    silence: "Silence",
    beep_tone: "Beep tone",
    all_music_libraries: "All music libraries",
    named_library: "One music library",
    local_packs: "Local music packs",
  };
  return labels[value] || String(value).replace(/_/g, " ");
}

function fillSelect(id, values, selected) {
  const select = $(id);
  if (!select || !values) return;
  select.textContent = "";
  values.forEach((value) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = offAirLabel(value);
    select.appendChild(option);
  });
  if (selected) select.value = selected;
}

function syncOffAirFields() {
  const slate = $("ebs-slate-field");
  if (slate) slate.hidden = $("ebs-display-mode").value !== "slate_image";
  const music = $("ebs-audio-mode").value === "background_music";
  const source = $("ebs-music-source-field");
  if (source) source.hidden = !music;
  const library = $("ebs-music-library-field");
  if (library) library.hidden = !music || $("ebs-music-source").value !== "named_library";
}

async function loadOffAir() {
  try {
    const data = await request(OFFAIR_API + "/");
    const options = data.options || {};
    const settings = data.settings || {};
    fillSelect("ebs-display-mode", options.display_modes, settings.display_mode);
    fillSelect("ebs-slate-variant", options.slate_variants, settings.slate_variant);
    fillSelect("ebs-audio-mode", options.audio_modes, settings.audio_mode);
    fillSelect("ebs-music-source", options.music_sources, settings.music_source);
    $("ebs-music-library").value = settings.music_library_name || "";
    syncOffAirFields();
    $("ebs-result").textContent = "";
  } catch (error) {
    $("ebs-result").textContent = error.message;
  }
}

async function saveOffAir(event) {
  event.preventDefault();
  const body = {
    display_mode: $("ebs-display-mode").value,
    slate_variant: $("ebs-slate-variant").value,
    audio_mode: $("ebs-audio-mode").value,
    music_source: $("ebs-music-source").value,
    music_library_name: $("ebs-music-library").value.trim(),
    music_library_id: "",
  };
  const save = $("ebs-save");
  save.disabled = true;
  try {
    await request(OFFAIR_API + "/", { method: "PUT", body: JSON.stringify(body) });
    $("ebs-result").textContent = "Saved.";
    syncOffAirFields();
  } catch (error) {
    $("ebs-result").textContent = error.message;
  } finally {
    save.disabled = false;
  }
}

{
  const form = $("ebs-form");
  if (form) form.addEventListener("submit", saveOffAir);
  ["ebs-display-mode", "ebs-audio-mode", "ebs-music-source"].forEach((id) => {
    const select = $(id);
    if (select) select.addEventListener("change", syncOffAirFields);
  });
}

CF.define("ebs", { onShow: loadOffAir });
