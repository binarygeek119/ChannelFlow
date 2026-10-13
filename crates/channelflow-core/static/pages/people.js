// People page: everyone in ChannelFlow's own catalog, alphabetical. Clicking a
// person opens /webui/people/<name> — a page like a media item's, listing
// everything that person appears in. Clicking one of those opens the item's
// Media-page detail.
const PEOPLE_ROUTE = "/api/people";
const MEDIA_ITEM_BASE = "/webui/media/item/";
const PEOPLE_PAGE = 200;
let peopleList = [];
let peopleTotal = 0;
let peopleOffset = 0;
let peopleFilter = "";
let peopleLoading = false;
let peopleObserver = null;
let peopleFilterTimer = null;

const PEOPLE_KIND_LABEL = {
  movie: "Movie",
  series: "Series",
  album: "Album",
  artist: "Artist",
  musicvideo: "Music video",
};

function peopleInitials(name) {
  return String(name)
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((word) => (word[0] || "").toUpperCase())
    .join("");
}

function peoplePhotoUrl(imagePath) {
  return imagePath ? `/api/media/image?path=${encodeURIComponent(imagePath)}` : null;
}

// Which person (if any) the current URL points at.
function personNameFromPath() {
  const match = location.pathname.match(/^\/webui\/people\/(.+)$/);
  return match ? decodeURIComponent(match[1]) : null;
}

function openPerson(name) {
  history.pushState(null, "", `/webui/people/${encodeURIComponent(name)}`);
  loadPeople();
}

function openPersonList() {
  history.pushState(null, "", "/webui/people");
  loadPeople();
}

function openMediaItem(matchKey) {
  history.pushState(null, "", MEDIA_ITEM_BASE + encodeURIComponent(matchKey));
  showTab("media");
}

// ── People list ───────────────────────────────────────────────────────────

function peopleCard(person) {
  const url = peoplePhotoUrl(person.image_path);
  const card = document.createElement("div");
  card.className = "people-card" + (url ? "" : " no-photo");
  card.tabIndex = 0;
  card.setAttribute("role", "button");
  card.title = person.name;
  const photo = url
    ? `<img class="people-photo" src="${escapeHtml(url)}" alt="${escapeHtml(person.name)}" loading="lazy" onerror="this.remove(); this.closest('.people-avatar').querySelector('.people-initial').style.display='flex';">`
    : "";
  card.innerHTML =
    `<div class="people-avatar">` +
      `<span class="people-initial">${escapeHtml(peopleInitials(person.name))}</span>` +
      photo +
    `</div>` +
    `<div class="people-name" title="${escapeHtml(person.name)}">${escapeHtml(person.name)}</div>`;
  card.addEventListener("click", () => openPerson(person.name));
  card.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      openPerson(person.name);
    }
  });
  return card;
}

function renderPeopleList() {
  showPeopleChrome(true);
  const heading = $("people-heading");
  if (heading) heading.textContent = "People";
  const grid = $("people-grid");
  if (!grid) return;
  grid.className = "people-grid";
  grid.textContent = "";
  peopleFilter = $("people-filter") ? $("people-filter").value.trim() : "";
  peopleSentinel();
  loadPeoplePage(true);
}

// The element the observer watches; when it scrolls into view, fetch the next
// page. Created once and parked after the grid.
function peopleSentinel() {
  let sentinel = $("people-sentinel");
  if (!sentinel) {
    const grid = $("people-grid");
    if (!grid || !grid.parentElement) return null;
    sentinel = document.createElement("div");
    sentinel.id = "people-sentinel";
    sentinel.className = "people-sentinel";
    grid.parentElement.appendChild(sentinel);
    if ("IntersectionObserver" in window) {
      peopleObserver = new IntersectionObserver(
        (entries) => entries.forEach((entry) => entry.isIntersecting && loadPeoplePage(false)),
        { rootMargin: "700px" }
      );
      peopleObserver.observe(sentinel);
    } else {
      window.addEventListener("scroll", () => {
        const rect = sentinel.getBoundingClientRect();
        if (rect.top < window.innerHeight + 700) loadPeoplePage(false);
      });
    }
  }
  return sentinel;
}

// Fetch one page of people and append it. `reset` starts over (new filter).
async function loadPeoplePage(reset) {
  if (peopleLoading || personNameFromPath()) return;
  const grid = $("people-grid");
  if (!grid) return;
  if (reset) {
    peopleOffset = 0;
    peopleList = [];
    peopleTotal = 0;
    grid.textContent = "";
  }
  if (peopleOffset > 0 && peopleTotal > 0 && peopleOffset >= peopleTotal) return;
  peopleLoading = true;
  const sentinel = peopleSentinel();
  if (sentinel) sentinel.textContent = peopleOffset === 0 ? "Loading…" : "Loading more…";
  try {
    const params = new URLSearchParams({ offset: String(peopleOffset), limit: String(PEOPLE_PAGE) });
    if (peopleFilter) params.set("q", peopleFilter);
    const data = await request(`${PEOPLE_ROUTE}?${params}`);
    peopleTotal = data.total || 0;
    const page = data.people || [];
    peopleList = peopleList.concat(page);
    peopleOffset += page.length;
    page.forEach((person) => grid.appendChild(peopleCard(person)));
  } catch (error) {
    if (sentinel) sentinel.textContent = error.message;
  }
  peopleLoading = false;
  const count = $("people-count");
  if (count) count.textContent = `${peopleTotal} person(s)`;
  if ($("people-empty")) $("people-empty").hidden = peopleTotal > 0;
  if (sentinel) sentinel.textContent = peopleOffset >= peopleTotal ? "" : "Loading more…";
}

// Toggle the list-only chrome (hint + filter) when a person page is open.
function showPeopleChrome(visible) {
  const toolbar = $("people-toolbar");
  if (toolbar) toolbar.hidden = !visible;
  const hint = $("people-hint");
  if (hint) hint.hidden = !visible;
  const empty = $("people-empty");
  if (empty && visible) empty.hidden = true;
}

// ── One person: their page, like a media item's ──────────────────────────

function personItemCard(item, matchKey, posterPath) {
  const card = document.createElement("div");
  const clickable = Boolean(matchKey);
  card.className = "people-item" + (clickable ? "" : " unlinked");
  const url = peoplePhotoUrl(posterPath || item.poster_path);
  const poster = url
    ? `<div class="people-item-poster" style="background-image:url('${escapeHtml(url)}')"></div>`
    : `<div class="people-item-poster people-item-poster-fallback"><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><circle cx="10" cy="10" r="2"/><path d="M4 18l4.5-4.5 3 3L16 12l4 4"/></svg></div>`;
  const secondary = item.year
    ? String(item.year)
    : PEOPLE_KIND_LABEL[item.kind] || item.kind || "";
  card.innerHTML =
    poster +
    `<div class="people-item-title" title="${escapeHtml(item.title || "")}">${escapeHtml(item.title || "")}</div>` +
    (item.character
      ? `<div class="people-item-sub" title="${escapeHtml(item.character)}">${escapeHtml(item.character)}</div>`
      : `<div class="people-item-sub">${escapeHtml(secondary)}</div>`);
  if (clickable) {
    card.tabIndex = 0;
    card.setAttribute("role", "button");
    const go = () => openMediaItem(matchKey);
    card.addEventListener("click", go);
    card.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        go();
      }
    });
  }
  return card;
}

async function renderPerson(name) {
  showPeopleChrome(false);
  const heading = $("people-heading");
  if (heading) heading.textContent = name;
  const grid = $("people-grid");
  const empty = $("people-empty");
  if (empty) empty.hidden = true;
  if (!grid) return;
  grid.className = "people-person";
  grid.textContent = "";

  let person = null;
  try {
    const data = await request(`${PEOPLE_ROUTE}/${encodeURIComponent(name)}`);
    person = data.person || null;
  } catch (error) {
    person = null;
  }
  if (!person) {
    grid.appendChild(
      libraryCard('<p class="hint bad">That person is not in the catalog.</p>')
    );
    return;
  }

  const back = document.createElement("button");
  back.type = "button";
  back.className = "media-back";
  back.textContent = "← People";
  back.addEventListener("click", openPersonList);
  grid.appendChild(back);

  // Header: photo + name + how many titles.
  const header = document.createElement("div");
  header.className = "people-person-head";
  const url = peoplePhotoUrl(person.image_path);
  const photo = url
    ? `<img class="people-person-photo" src="${escapeHtml(url)}" alt="${escapeHtml(person.name)}" onerror="this.remove(); this.closest('.people-person-photo-frame').querySelector('.people-initial').style.display='flex';">`
    : "";
  header.innerHTML =
    `<div class="people-person-photo-frame">` +
      `<span class="people-initial">${escapeHtml(peopleInitials(person.name))}</span>` +
      photo +
    `</div>` +
    `<div class="people-person-meta">` +
      `<h1>${escapeHtml(person.name)}</h1>` +
      `<div class="people-person-sub">${(person.items || []).length} title(s) in your catalog</div>` +
    `</div>`;
  grid.appendChild(header);

  // The person's titles, each linking into the Media catalog.
  const seen = new Set();
  const titles = document.createElement("div");
  titles.className = "people-items";
  (person.items || []).forEach((item) => {
    const matchKey = item.match_key || null;
    const dedup = matchKey || `${item.kind}:${item.title}:${item.year}`;
    if (seen.has(dedup)) return;
    seen.add(dedup);
    titles.appendChild(personItemCard(item, matchKey, item.poster_path || null));
  });
  if (!titles.children.length) {
    grid.appendChild(libraryCard('<p class="hint">No catalog titles for this person yet.</p>'));
    return;
  }
  grid.appendChild(titles);
}

async function loadPeople() {
  const personName = personNameFromPath();
  if (personName) {
    await renderPerson(personName);
    return;
  }
  renderPeopleList();
}

// Filtering goes to the server (debounced), so a large cast list is never
// filtered row-by-row in the browser.
document.addEventListener("input", (event) => {
  if (event.target && event.target.id === "people-filter") {
    clearTimeout(peopleFilterTimer);
    peopleFilterTimer = setTimeout(() => {
      peopleFilter = $("people-filter").value.trim();
      loadPeoplePage(true);
    }, 250);
  }
});

CF.define("people", { onShow: loadPeople });