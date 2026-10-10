// TV Guide — a channel/hour grid of what's on, ported from v1.0.0. Times are
// shown in the Guide time zone from General Settings; programmes come from the
// stored EPG (`/api/guide`). The grid scrolls horizontally across a day and
// positions a "now" bar on today.

const GUIDE_PX_PER_MIN = 4;
const GUIDE_CHANNEL_COL = 168;

let guideData = null;
let guideDateFilter = null;
let guideClockTimer = null;

function guideTimeZone() {
  return (guideData && guideData.timeZone) || undefined;
}

function formatGuideClock(iso) {
  try {
    return new Date(iso).toLocaleTimeString([], {
      hour: "numeric",
      minute: "2-digit",
      timeZone: guideTimeZone(),
    });
  } catch (error) {
    return new Date(iso).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  }
}

function formatGuideDate(iso) {
  try {
    return new Intl.DateTimeFormat("en-CA", {
      timeZone: guideTimeZone(),
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).format(new Date(iso));
  } catch (error) {
    return new Date(iso).toISOString().slice(0, 10);
  }
}

function todayGuideDate() {
  return formatGuideDate(new Date().toISOString());
}

function addGuideDate(ymd, days) {
  const parts = (ymd || todayGuideDate()).split("-").map(Number);
  const dt = new Date(Date.UTC(parts[0], (parts[1] || 1) - 1, (parts[2] || 1) + days));
  const month = String(dt.getUTCMonth() + 1).padStart(2, "0");
  const day = String(dt.getUTCDate()).padStart(2, "0");
  return `${dt.getUTCFullYear()}-${month}-${day}`;
}

function formatGuideDayHeading(iso) {
  try {
    return new Date(iso).toLocaleDateString([], {
      weekday: "long",
      month: "short",
      day: "numeric",
      timeZone: guideTimeZone(),
    });
  } catch (error) {
    return formatGuideDate(iso);
  }
}

function shiftGuideDays(days) {
  const current = guideDateFilter || (guideData ? formatGuideDate(guideData.from) : todayGuideDate());
  guideDateFilter = addGuideDate(current, days);
  loadGuide({ scrollToNow: guideDateFilter === todayGuideDate() });
}

function jumpGuideToNow() {
  guideDateFilter = null;
  loadGuide({ scrollToNow: true });
}

function jumpGuideToDate(date) {
  guideDateFilter = date || null;
  loadGuide({ scrollToNow: !date || date === todayGuideDate() });
}

async function loadGuide() {
  const root = $("tv-guide");
  if (!root) return;
  root.innerHTML = '<p class="tv-guide-empty">Loading guide…</p>';
  try {
    const params = new URLSearchParams({ _: String(Date.now()) });
    if (guideDateFilter) params.set("date", guideDateFilter);
    guideData = await request(`/api/guide?${params}`);
    guideDateFilter = formatGuideDate(guideData.from);
    const dateInput = $("guide-date");
    if (dateInput) dateInput.value = guideDateFilter;
    const range = $("guide-range-label");
    if (range) {
      range.textContent = `${formatGuideDayHeading(guideData.from)}${
        guideData.timeZone ? ` · ${guideData.timeZone}` : ""
      }`;
    }
    renderGuide();
    startGuideClock();
    if (guideDateFilter === todayGuideDate()) scrollGuideToNow();
  } catch (error) {
    root.innerHTML = `<p class="tv-guide-empty">Could not load the TV guide: ${escapeHtml(error.message)}</p>`;
  }
}

function guideNowX() {
  if (!guideData) return null;
  const from = new Date(guideData.from).getTime();
  const to = new Date(guideData.to).getTime();
  const now = Date.now();
  if (now < from || now > to) return null;
  return ((now - from) / 60000) * GUIDE_PX_PER_MIN;
}

function renderGuide() {
  const root = $("tv-guide");
  if (!root || !guideData) return;
  const channels = guideData.channels || [];
  const programs = guideData.programs || [];
  if (!channels.length) {
    root.innerHTML = '<p class="tv-guide-empty">No enabled channels — create channels first.</p>';
    return;
  }
  const from = new Date(guideData.from).getTime();
  const to = new Date(guideData.to).getTime();
  const totalMin = Math.max(30, (to - from) / 60000);
  const width = totalMin * GUIDE_PX_PER_MIN;
  const byChannel = {};
  programs.forEach((program) => {
    (byChannel[program.channelId] = byChannel[program.channelId] || []).push(program);
  });

  let ticks = "";
  for (let minute = 0; minute < totalMin; minute += 30) {
    ticks += `<div class="tv-guide-tick" style="left:${minute * GUIDE_PX_PER_MIN}px">${escapeHtml(
      formatGuideClock(new Date(from + minute * 60000).toISOString())
    )}</div>`;
  }

  const rows = channels
    .map((channel) => {
      const blocks = (byChannel[channel.id] || [])
        .map((program) => {
          const start = new Date(program.start).getTime();
          const finish = new Date(program.finish).getTime();
          const left = (Math.max(from, start) - from) / 60000 * GUIDE_PX_PER_MIN;
          const w =
            Math.max(10, (Math.min(to, finish) - Math.max(from, start)) / 60000 * GUIDE_PX_PER_MIN - 2);
          return (
            `<button type="button" class="tv-guide-block${program.isNow ? " is-now" : ""}" ` +
            `data-program="${escapeHtml(program.id)}" style="left:${left}px;width:${w}px">` +
            `<strong>${escapeHtml(program.title || "Untitled")}</strong>` +
            `${program.subTitle ? `<span>${escapeHtml(program.subTitle)}</span>` : ""}` +
            `</button>`
          );
        })
        .join("");
      return (
        `<div class="tv-guide-row">` +
        `<button type="button" class="tv-guide-channel" data-channel="${escapeHtml(channel.id)}">` +
        `<div><div class="num">${escapeHtml(channel.number)}</div><div class="name">${escapeHtml(channel.name)}</div></div>` +
        `</button>` +
        `<div class="tv-guide-track" style="width:${width}px"><div class="tv-guide-now-bar"></div>${blocks}</div>` +
        `</div>`
      );
    })
    .join("");

  const dayWidth = GUIDE_CHANNEL_COL + width;
  root.innerHTML =
    `<div class="tv-guide-scroll">` +
    `<div class="tv-guide-header" style="width:${dayWidth}px">` +
    `<div class="tv-guide-corner">Channel</div>` +
    `<div class="tv-guide-times"><div class="tv-guide-times-inner" style="width:${width}px">${ticks}<div class="tv-guide-now"></div></div></div>` +
    `</div>` +
    `<div class="tv-guide-body">${rows}</div>` +
    `</div>`;
  root.querySelectorAll("[data-program]").forEach((button) => {
    button.addEventListener("click", () => openGuideProgram(button.dataset.program));
  });
  positionGuideNowLine();
}

function positionGuideNowLine() {
  const x = guideNowX();
  document.querySelectorAll(".tv-guide-now, .tv-guide-now-bar").forEach((line) => {
    line.style.left = x == null ? "-9999px" : `${x}px`;
  });
}

function startGuideClock() {
  if (guideClockTimer) clearInterval(guideClockTimer);
  guideClockTimer = setInterval(positionGuideNowLine, 30000);
}

function scrollGuideToNow() {
  const target = guideNowX();
  if (target == null) return;
  const scroller = document.querySelector("#tv-guide .tv-guide-scroll");
  if (!scroller) return;
  const pastPx = 60 * GUIDE_PX_PER_MIN;
  const left = Math.min(Math.max(0, target - pastPx), Math.max(0, scroller.scrollWidth - scroller.clientWidth));
  scroller.scrollLeft = left;
}

function openGuideProgram(id) {
  const program = (guideData.programs || []).find((entry) => entry.id === id);
  if (!program) return;
  const dialog = $("guide-program-dialog");
  if (!dialog) return;
  const channel = (guideData.channels || []).find((entry) => entry.id === program.channelId);
  const when = `${formatGuideClock(program.start)} – ${formatGuideClock(program.finish)}`;
  const meta = [program.episode, program.year, program.rating, (program.categories || []).join(", ")]
    .filter(Boolean)
    .join(" · ");
  $("guide-program-title").textContent = program.title || "Programme";
  const poster = program.posterUrl
    ? `<img class="tv-guide-poster" src="${escapeHtml(program.posterUrl)}" alt="" onerror="this.remove()">`
    : "";
  $("guide-program-body").innerHTML =
    `<div class="tv-guide-program">` +
    poster +
    `<div class="tv-guide-program-meta">` +
    `<p class="hint">${escapeHtml(`${channel ? channel.number + " · " + channel.name + " · " : ""}${when}`)}</p>` +
    `${meta ? `<p class="hint">${escapeHtml(meta)}</p>` : ""}` +
    `${program.description ? `<p>${escapeHtml(program.description)}</p>` : ""}` +
    `</div></div>`;
  dialog.showModal();
}

{
  const prev = $("btn-guide-prev");
  if (prev) prev.addEventListener("click", () => shiftGuideDays(-1));
  const next = $("btn-guide-next");
  if (next) next.addEventListener("click", () => shiftGuideDays(1));
  const now = $("btn-guide-now");
  if (now) now.addEventListener("click", jumpGuideToNow);
  const dateInput = $("guide-date");
  if (dateInput) dateInput.addEventListener("change", () => jumpGuideToDate(dateInput.value));
  const close = $("guide-program-close");
  if (close) close.addEventListener("click", () => $("guide-program-dialog").close());
  const dialog = $("guide-program-dialog");
  if (dialog) dialog.addEventListener("click", (event) => {
    if (event.target === dialog) dialog.close();
  });
}

CF.define("guide", { onShow: loadGuide });