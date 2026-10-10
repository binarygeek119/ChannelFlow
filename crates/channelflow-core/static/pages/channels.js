let channels = [];

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

CF.define("channels", { onShow: () => load() });
