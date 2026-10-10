// Clients: the paired ChannelFlow TV apps and copied playlist URLs. Each has
// its own random API key; only a hint is shown, never the whole key.

function clientsWhen(value) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}

function clientRow(client) {
  const label = client.label || "ChannelFlow TV";
  return (
    `<tr>` +
      `<td>${escapeHtml(label)}</td>` +
      `<td><code>${escapeHtml(client.key_hint || "••••")}</code></td>` +
      `<td>${escapeHtml(clientsWhen(client.created_at))}</td>` +
      `<td>${escapeHtml(clientsWhen(client.last_seen_at))}</td>` +
      `<td><button type="button" class="ghost clients-remove" data-client="${escapeHtml(client.id)}">Remove</button></td>` +
    `</tr>`
  );
}

async function loadClients() {
  const host = $("clients-table");
  if (!host) return;
  host.textContent = "Loading…";
  try {
    const data = await request("/api/clients");
    const clients = data.clients || [];
    const count = $("clients-count");
    if (count) count.textContent = `${clients.length} client(s)`;
    const serverKey = $("clients-server-key");
    if (serverKey) serverKey.textContent = data.server_key_hint || "••••";
    if (!clients.length) {
      host.innerHTML =
        '<p class="hint">No clients yet. Pair an app from Quick Pin, or copy an M3U/XMLTV URL — each copy makes its own key.</p>';
      return;
    }
    host.innerHTML =
      `<table class="data-table">` +
      `<thead><tr><th>Name</th><th>Key</th><th>Paired</th><th>Last seen</th><th></th></tr></thead>` +
      `<tbody>${clients.map(clientRow).join("")}</tbody>` +
      `</table>`;
    host.querySelectorAll(".clients-remove").forEach((button) => {
      button.addEventListener("click", () => removeClient(button.dataset.client));
    });
  } catch (error) {
    host.innerHTML = `<p class="hint bad">${escapeHtml(error.message)}</p>`;
  }
}

async function removeClient(id) {
  if (!id) return;
  if (!window.confirm("Remove this client? Its API key is destroyed and it can no longer connect.")) {
    return;
  }
  try {
    await request(`/api/clients/${encodeURIComponent(id)}`, { method: "DELETE" });
    showToast("Client removed");
  } catch (error) {
    showToast(error.message);
  }
  await loadClients();
}

{
  const refresh = $("clients-refresh");
  if (refresh) refresh.addEventListener("click", loadClients);
}

CF.define("clients", { onShow: loadClients });