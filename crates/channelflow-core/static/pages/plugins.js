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

CF.define("plugins", { onShow: loadPlugins });
