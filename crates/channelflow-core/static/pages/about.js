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

CF.define("about", { onShow: loadAbout });
