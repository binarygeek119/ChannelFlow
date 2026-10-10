// --- Tasks: the Jellyfin library scan -------------------------------------

function setTaskNote(message, bad) {
  const note = $("jf-task-note");
  if (!note) return;
  note.hidden = !message;
  note.textContent = message || "";
  note.classList.toggle("bad", !!bad);
}

async function loadTasks() {
  try {
    const data = await request("/api/tasks/jellyfin-sync");
    renderJellyfinTask(data.config || {}, data.runs || []);
  } catch (error) {
    setTaskNote("Could not load the library scan task: " + error.message, true);
  }
}

function renderJellyfinTask(config, runs) {
  const enabled = config.enabled !== false;
  const schedule = config.schedule || {};
  $("jf-task-enabled").checked = enabled;
  $("jf-task-time").value = schedule.daily_time || "03:00";
  renderTaskRuns(runs);
}

function renderTaskRuns(runs) {
  const box = $("jf-task-runs");
  if (!box) return;
  if (!runs.length) {
    box.innerHTML =
      '<p class="hint">No runs yet — the daily timer (or Run now) fills this in.</p>';
    return;
  }
  box.innerHTML =
    "<h4>Recent runs</h4>" +
    runs
      .map((run) => {
        const when = run.at ? new Date(run.at).toLocaleString() : "?";
        return `<div class="task-run"><span class="task-run-when">${escapeHtml(when)}</span>
          <span class="task-run-trigger">${escapeHtml(run.trigger || "?")}</span>
          <span>${escapeHtml(String(run.added))} added · ${escapeHtml(String(run.updated))} updated · ${escapeHtml(String(run.errors))} errors · ${escapeHtml(String(run.connections))} connection(s)</span></div>`;
      })
      .join("");
}

$("jellyfin-task-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const enabled = $("jf-task-enabled").checked;
  const schedule = {
    mode: "daily",
    daily_time: $("jf-task-time").value || "03:00",
    cron: "",
  };
  const save = $("jf-task-save");
  save.disabled = true;
  try {
    const data = await request("/api/tasks/jellyfin-sync", {
      method: "PUT",
      body: JSON.stringify({ enabled, schedule }),
    });
    renderJellyfinTask(data.config || {}, data.runs || []);
    setTaskNote("Schedule saved.", false);
  } catch (error) {
    setTaskNote(error.message, true);
  } finally {
    save.disabled = false;
  }
});

$("jf-task-run").addEventListener("click", () => {
  setTaskNote("");
  taskPopup.run(
    "Jellyfin library scan",
    () => request("/api/tasks/jellyfin-sync/run", {
      method: "POST",
      body: JSON.stringify({}),
    }),
    (data) => {
      renderTaskRuns(data.runs || []);
      const run = data.run || {};
      const note =
        `Ran ${run.connections || 0} connection(s): ${run.added || 0} added · ${run.updated || 0} updated · ${run.removed || 0} removed · ${run.errors || 0} errors`;
      setTaskNote(note, (run.errors || 0) > 0);
      return `Scan finished: ${run.added || 0} added · ${run.updated || 0} updated · ${run.errors || 0} errors (${run.connections || 0} connection(s))`;
    }
  );
});

// Maintenance jobs are user-triggered one-offs. They are stubbed for now —
// the buttons exist and say so until they are wired to real work.
for (const [id, label] of [
  ["maintenance-rebuild-playouts", "Rebuild All Playouts"],
  ["maintenance-clear-guide", "Clear Guide Data"],
]) {
  const button = document.getElementById(id);
  if (!button) continue;
  button.addEventListener("click", () => {
    const note = document.getElementById("maintenance-note");
    if (note) {
      note.hidden = false;
      note.textContent = `${label} is not wired up yet.`;
    }
  });
}

// Catalog cleanup: the grace period, the daily time, and the two one-off
// buttons are stubbed until the cleanup and local-file scan are implemented.
for (const [id, label] of [
  ["catalog-save-grace", "Save Grace Period"],
  ["catalog-run", "Run Catalog Cleanup"],
  ["catalog-scan", "Scan Local Files"],
]) {
  const button = document.getElementById(id);
  if (!button) continue;
  button.addEventListener("click", () => {
    const note = document.getElementById("catalog-note");
    if (note) {
      note.hidden = false;
      note.textContent = `${label} is not wired up yet.`;
    }
  });
}

CF.define("tasks", { onShow: loadTasks });
