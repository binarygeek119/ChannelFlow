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

// The cards and links below were defined above the Plugins section in the
// monolithic shell; here they close out the Live TV page.

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

CF.define("livetv", { onShow: loadLiveTv });
