let channels = [];
// The watermark a *new* upload will replace: the data URL read from the file
// picker, plus the stored path of the art this channel already has.
let pendingWatermarkImage = null;
let pendingWatermarkPath = null;

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

// ── watermark form ───────────────────────────────────────────────────────────

function renderWatermarkFields() {
  const on = els.wmEnabled && els.wmEnabled.checked;
  if (els.wmFields) els.wmFields.hidden = !on;
}

function resetWatermarkForm() {
  pendingWatermarkImage = null;
  pendingWatermarkPath = null;
  if (els.wmEnabled) els.wmEnabled.checked = false;
  if (els.wmFile) els.wmFile.value = "";
  if (els.wmPreview) {
    els.wmPreview.hidden = true;
    els.wmPreview.removeAttribute("src");
  }
  if (els.wmClearImage) els.wmClearImage.hidden = true;
  if (els.wmFileName) els.wmFileName.textContent = "";
  renderWatermarkFields();
}

function watermarkPreviewUrl() {
  if (pendingWatermarkImage) return pendingWatermarkImage;
  if (pendingWatermarkPath) return `/api/media/image?path=${encodeURIComponent(pendingWatermarkPath)}`;
  return null;
}

function showWatermarkArt() {
  const url = watermarkPreviewUrl();
  if (!els.wmPreview) return;
  if (url) {
    els.wmPreview.src = url;
    els.wmPreview.hidden = false;
  } else {
    els.wmPreview.hidden = true;
    els.wmPreview.removeAttribute("src");
  }
  if (els.wmClearImage) els.wmClearImage.hidden = !pendingWatermarkImage;
  if (els.wmFileName) {
    els.wmFileName.textContent = pendingWatermarkImage ? "New image chosen — save to upload." : "";
  }
}

function fillWatermark(watermark) {
  const wm = watermark || {};
  pendingWatermarkImage = null;
  pendingWatermarkPath = wm.image_path || null;
  if (els.wmEnabled) els.wmEnabled.checked = !!wm.image_path || !!pendingWatermarkImage;
  if (els.wmPosition) els.wmPosition.value = wm.position || "bottom-right";
  if (els.wmOffsetX) els.wmOffsetX.value = wm.offset_x != null ? wm.offset_x : 12;
  if (els.wmOffsetY) els.wmOffsetY.value = wm.offset_y != null ? wm.offset_y : 12;
  if (els.wmScale) els.wmScale.value = wm.scale != null ? wm.scale : 100;
  setRangeLabel("wm-scale", "wm-scale-value");
  if (els.wmOpacity) els.wmOpacity.value = wm.opacity != null ? wm.opacity : 100;
  setRangeLabel("wm-opacity", "wm-opacity-value");
  if (els.wmFadeBreak) els.wmFadeBreak.checked = !!wm.fade_before_break;
  if (els.wmFadeBack) els.wmFadeBack.checked = !!wm.fade_back_after_break;
  if (els.wmFadeDelay) els.wmFadeDelay.value = wm.fade_back_delay_seconds != null ? wm.fade_back_delay_seconds : 5;
  if (els.wmFadeDuration) els.wmFadeDuration.value = wm.fade_duration_seconds != null ? wm.fade_duration_seconds : 1.5;
  showWatermarkArt();
  renderWatermarkFields();
}

function setRangeLabel(id, labelId) {
  const input = document.getElementById(id);
  const label = document.getElementById(labelId);
  if (input && label) label.textContent = input.value;
}

/// The watermark object for the payload, or null when it is turned off.
function collectWatermark() {
  if (!els.wmEnabled || !els.wmEnabled.checked) return null;
  const watermark = {
    position: els.wmPosition ? els.wmPosition.value : "bottom-right",
    offset_x: Number(els.wmOffsetX ? els.wmOffsetX.value : 12) || 0,
    offset_y: Number(els.wmOffsetY ? els.wmOffsetY.value : 12) || 0,
    scale: Number(els.wmScale ? els.wmScale.value : 100) || 100,
    opacity: Math.max(0, Math.min(100, Number(els.wmOpacity ? els.wmOpacity.value : 100) || 0)),
    fade_before_break: !!(els.wmFadeBreak && els.wmFadeBreak.checked),
    fade_back_after_break: !!(els.wmFadeBack && els.wmFadeBack.checked),
    fade_back_delay_seconds: Number(els.wmFadeDelay ? els.wmFadeDelay.value : 5) || 0,
    fade_duration_seconds: Number(els.wmFadeDuration ? els.wmFadeDuration.value : 1.5) || 0,
  };
  if (pendingWatermarkPath) watermark.image_path = pendingWatermarkPath;
  if (pendingWatermarkImage) watermark.image_data = pendingWatermarkImage;
  return watermark;
}

// ── form lifecycle ───────────────────────────────────────────────────────────

function resetForm() {
  els.form.reset();
  els.id.value = "";
  els.enabled.checked = true;
  els.formTitle.textContent = "Add channel";
  els.save.textContent = "Add channel";
  els.cancel.hidden = true;
  resetWatermarkForm();
  showError("");
}

function editChannel(channel) {
  els.id.value = channel.id;
  els.number.value = channel.number;
  els.name.value = channel.name;
  els.description.value = channel.description;
  els.aiPrompt.value = channel.ai_prompt || "";
  els.enabled.checked = channel.enabled;
  els.formTitle.textContent = `Edit ${channel.name}`;
  els.save.textContent = "Save changes";
  els.cancel.hidden = false;
  fillWatermark(channel.watermark);
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
    ai_prompt: (els.aiPrompt.value || "").trim(),
    watermark: collectWatermark(),
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

if (els.wmEnabled) els.wmEnabled.addEventListener("change", renderWatermarkFields);
if (els.wmPick) {
  els.wmPick.addEventListener("click", () => els.wmFile && els.wmFile.click());
}
if (els.wmFile) {
  els.wmFile.addEventListener("change", async () => {
    const file = els.wmFile.files && els.wmFile.files[0];
    if (!file) return;
    if (!/^image\/(png|jpeg)$/.test(file.type)) {
      showError("Watermark must be a PNG or JPEG image.");
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      pendingWatermarkImage = String(reader.result || "");
      showError("");
      showWatermarkArt();
    };
    reader.readAsDataURL(file);
  });
}
if (els.wmClearImage) {
  els.wmClearImage.addEventListener("click", () => {
    pendingWatermarkImage = null;
    if (els.wmFile) els.wmFile.value = "";
    showWatermarkArt();
  });
}
if (els.wmScale) els.wmScale.addEventListener("input", () => setRangeLabel("wm-scale", "wm-scale-value"));
if (els.wmOpacity) els.wmOpacity.addEventListener("input", () => setRangeLabel("wm-opacity", "wm-opacity-value"));

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
      // The per-channel transcode editor is not wired into the shell yet;
      // take the user to the Transcode settings page (instance defaults)
      // rather than calling a function that only exists on that page.
      history.pushState(null, "", "/webui/transcode");
      await showTab("transcode");
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

CF.define("channels", { onShow: () => load() });