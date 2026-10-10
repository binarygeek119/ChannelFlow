const ERSATZTV_API = "/api/plugins/com.channelflow.ersatztv";

// The field list is served, not hard-coded here: the plugin builds it from
// next's `channel_config.json`, and a test in the plugin asserts it names
// exactly the settings next accepts. That is why the form cannot offer a
// setting next would reject, or quietly miss one it would take — a field
// added upstream shows up here after a refresh.
//
// next keeps these settings per channel. ChannelFlow keeps instance defaults
// on the Transcode page and stores only a channel's *differences* from them,
// so editing a default still reaches every channel that has not overridden
// that one field. The dialog diffs the edited values against the defaults to
// work out which fields to store, which is also what marks a field as
// overridden — no separate toggle to keep in sync with the value.

// Distinguishes "unchanged" from "changed to null" while diffing.
const NO_CHANGE = Symbol("no-change");

let transcodeSpec = null;
let transcodeDraft = null;
let channelTranscode = null;
let channelTranscodeDraft = null;

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function getPath(object, path) {
  return path
    .split(".")
    .reduce((value, key) => (value == null ? undefined : value[key]), object);
}

function setPath(object, path, value) {
  const keys = path.split(".");
  let cursor = object;
  for (const key of keys.slice(0, -1)) {
    if (!isPlainObject(cursor[key])) cursor[key] = {};
    cursor = cursor[key];
  }
  cursor[keys[keys.length - 1]] = value;
}

// next's schema releases a nullable field when the value is null; the note
// under each field in the dialog reads back what inheriting would give.
function describeField(field, value) {
  if (value === null || value === undefined) return field.nullLabel || "Not set";
  if (field.kind === "bool") return value ? "On" : "Off";
  if (field.kind === "enum") {
    const match = (field.options || []).find((option) => option.value === value);
    return match ? match.label : String(value);
  }
  if (field.kind === "enum_list") {
    const labels = (field.options || [])
      .filter((option) => (value || []).includes(option.value))
      .map((option) => option.label);
    return labels.length ? labels.join(", ") : "None";
  }
  if (field.kind === "text_list") {
    return value && value.length ? value.join(", ") : "None";
  }
  return String(value);
}

function fieldId(field) {
  return `f-${field.path.replace(/\./g, "-")}`;
}

// Build the control for one field, reporting every edit through `onChange`.
// The control is seeded from `value`, which is the object being edited.
function buildControl(field, value, onChange) {
  const control = document.createElement("input");
  const extras = [];

  if (field.kind === "bool") {
    control.type = "checkbox";
    control.checked = value === true;
    control.addEventListener("change", () => onChange(control.checked));
    return { control, extras };
  }

  if (field.kind === "enum") {
    const select = document.createElement("select");
    if (field.nullable) {
      const blank = document.createElement("option");
      blank.value = "";
      blank.textContent = field.nullLabel || "Not set";
      blank.selected = value == null;
      select.appendChild(blank);
    }
    for (const option of field.options || []) {
      const entry = document.createElement("option");
      entry.value = option.value;
      entry.textContent = option.label;
      entry.selected = value === option.value;
      select.appendChild(entry);
    }
    select.addEventListener("change", () => {
      if (field.nullable && select.selectedIndex === 0) {
        onChange(null);
        return;
      }
      const offset = field.nullable ? 1 : 0;
      const option = (field.options || [])[select.selectedIndex - offset];
      // Emit the option's own value, not the select's string, so a numeric
      // choice like bit depth stores 8 and not "8".
      onChange(option ? option.value : null);
    });
    return { control: select, extras };
  }

  if (field.kind === "enum_list") {
    const group = document.createElement("div");
    group.className = "checks";
    const selected = new Set(Array.isArray(value) ? value : []);
    for (const option of field.options || []) {
      const wrapper = document.createElement("label");
      wrapper.className = "check inline";
      const box = document.createElement("input");
      box.type = "checkbox";
      box.value = option.value;
      box.checked = selected.has(option.value);
      box.addEventListener("change", () => {
        if (box.checked) selected.add(option.value);
        else selected.delete(option.value);
        onChange(
          (field.options || [])
            .filter((entry) => selected.has(entry.value))
            .map((entry) => entry.value)
        );
      });
      wrapper.append(box, document.createTextNode(option.label));
      group.appendChild(wrapper);
    }
    return { control: group, extras };
  }

  if (field.kind === "text_list") {
    const textarea = document.createElement("textarea");
    textarea.rows = 3;
    textarea.value = Array.isArray(value) ? value.join("\n") : "";
    textarea.addEventListener("input", () =>
      onChange(
        textarea.value
          .split("\n")
          .map((line) => line.trim())
          .filter(Boolean)
      )
    );
    return { control: textarea, extras };
  }

  if (field.kind === "int" || field.kind === "float") {
    control.type = "number";
    if (field.min != null) control.min = field.min;
    if (field.max != null) control.max = field.max;
    control.step = field.kind === "int" ? field.step || 1 : field.step || "any";
    control.value = value == null ? "" : String(value);
    control.addEventListener("input", () => {
      if (control.value === "") {
        onChange(field.nullable ? null : 0);
        return;
      }
      const parsed =
        field.kind === "int"
          ? Number.parseInt(control.value, 10)
          : Number.parseFloat(control.value);
      onChange(Number.isFinite(parsed) ? parsed : null);
    });
    return { control, extras };
  }

  control.type = "text";
  control.value = value == null ? "" : String(value);
  if (field.suggestions && field.suggestions.length) {
    const list = document.createElement("datalist");
    list.id = `${fieldId(field)}-list`;
    for (const suggestion of field.suggestions) {
      const option = document.createElement("option");
      option.value = suggestion;
      list.appendChild(option);
    }
    control.setAttribute("list", list.id);
    extras.push(list);
  }
  control.addEventListener("input", () => {
    const text = control.value.trim();
    onChange(text === "" ? (field.nullable ? null : "") : text);
  });
  return { control, extras };
}

// `defaults` is only passed for the per-channel dialog; with it each field
// grows a note reading back what inheriting would give, and lights up once the
// value differs from the default.
function renderField(field, value, defaults) {
  const wrapper = document.createElement("div");
  wrapper.className = "field";
  wrapper.dataset.path = field.path;

  const label = document.createElement("label");
  label.className = "field-label";
  label.textContent = field.label;
  label.htmlFor = fieldId(field);
  wrapper.appendChild(label);

  const note = defaults ? document.createElement("small") : null;
  if (note) note.className = "field-note";

  const updateNote = () => {
    const base = getPath(defaults, field.path);
    const current = getPath(value, field.path);
    const overridden = JSON.stringify(current ?? null) !== JSON.stringify(base ?? null);
    wrapper.classList.toggle("overridden", overridden);
    note.textContent = `${overridden ? "Overridden — default" : "Default"}: ${describeField(
      field,
      base
    )}`;
  };

  const { control, extras } = buildControl(field, getPath(value, field.path), (next) => {
    setPath(value, field.path, next);
    if (note) updateNote();
  });
  control.id = fieldId(field);
  wrapper.appendChild(control);
  for (const extra of extras) wrapper.appendChild(extra);

  if (field.hint) {
    const hint = document.createElement("small");
    hint.className = "field-hint";
    hint.textContent = field.hint;
    wrapper.appendChild(hint);
  }
  if (note) {
    wrapper.appendChild(note);
    updateNote();
  }

  return wrapper;
}

function renderSettings(container, groups, value, defaults) {
  container.innerHTML = "";
  for (const group of groups) {
    const card = document.createElement("div");
    card.className = "card section-card settings-group";
    const heading = document.createElement("h3");
    heading.textContent = group.title;
    card.appendChild(heading);
    if (group.hint) {
      const hint = document.createElement("p");
      hint.className = "hint";
      hint.textContent = group.hint;
      card.appendChild(hint);
    }
    for (const field of group.fields) {
      card.appendChild(renderField(field, value, defaults));
    }
    container.appendChild(card);
  }
}

// What a channel needs to store: the keys of `value` that differ from `base`,
// recursing through objects. A null is a real value here, not "inherit".
function diffAgainst(base, value) {
  if (isPlainObject(base) && isPlainObject(value)) {
    const patch = {};
    let changed = false;
    for (const key of Object.keys(value)) {
      const child = diffAgainst(base[key], value[key]);
      if (child !== NO_CHANGE) {
        patch[key] = child;
        changed = true;
      }
    }
    return changed ? patch : NO_CHANGE;
  }
  return JSON.stringify(base ?? null) === JSON.stringify(value ?? null) ? NO_CHANGE : value;
}

function setTranscodeNote(message, bad) {
  els.transcodeNote.textContent = message || "";
  els.transcodeNote.className = bad ? "hint bad" : "hint";
}

async function loadTranscode() {
  try {
    const data = await request(ERSATZTV_API);
    transcodeSpec = data.spec;
    transcodeDraft = data.defaults;
    renderSettings(els.transcodeGroups, data.spec.groups, transcodeDraft);
    setTranscodeNote("");
  } catch (error) {
    setTranscodeNote(error.message || "Could not load transcode settings.", true);
  }
}

async function saveTranscode() {
  try {
    const data = await request(ERSATZTV_API, {
      method: "PUT",
      body: JSON.stringify(transcodeDraft),
    });
    transcodeDraft = data.defaults;
    renderSettings(els.transcodeGroups, transcodeSpec.groups, transcodeDraft);
    setTranscodeNote("Saved. Channels with no override for a field pick up the new value.");
  } catch (error) {
    setTranscodeNote(error.message || "Could not save transcode settings.", true);
  }
}

function setChannelTranscodeNote(message, bad) {
  if (els.channelTranscodeNote) {
    els.channelTranscodeNote.textContent = message || "";
    els.channelTranscodeNote.className = bad ? "hint bad" : "hint";
  }
  showChannelTranscodeError(bad ? message : "");
}

function showChannelTranscodeError(message) {
  els.channelTranscodeError.textContent = message || "";
  els.channelTranscodeError.hidden = !message;
}

async function openChannelTranscode(id) {
  try {
    const data = await request(`${ERSATZTV_API}/channels/${id}`);
    channelTranscode = data;
    // Edit a copy of the effective settings; the diff against the defaults is
    // what gets stored.
    channelTranscodeDraft = data.effective;
    els.channelTranscodeTitle.textContent = `Transcode — ${data.channel.number} ${data.channel.name}`;
    renderSettings(
      els.channelTranscodeGroups,
      data.spec.groups,
      channelTranscodeDraft,
      data.defaults
    );
    setChannelTranscodeNote("");
    els.channelTranscode.showModal();
  } catch (error) {
    setStatus("transcode failed", "bad");
    showError(error.message);
  }
}

async function saveChannelTranscode() {
  if (!channelTranscode) return;
  const diff = diffAgainst(channelTranscode.defaults, channelTranscodeDraft);
  const patch = diff === NO_CHANGE ? {} : diff;
  try {
    const data = await request(`${ERSATZTV_API}/channels/${channelTranscode.channel.id}`, {
      method: "PUT",
      body: JSON.stringify(patch),
    });
    channelTranscode.overrides = data.overrides;
    channelTranscode.effective = data.effective;
    channelTranscodeDraft = data.effective;
    renderSettings(
      els.channelTranscodeGroups,
      channelTranscode.spec.groups,
      channelTranscodeDraft,
      channelTranscode.defaults
    );
    const count = countLeaves(data.overrides);
    setChannelTranscodeNote(
      count
        ? `Saved ${count} override${count === 1 ? "" : "s"} on this channel.`
        : "Saved. This channel follows the Transcode page exactly."
    );
  } catch (error) {
    setChannelTranscodeNote(error.message || "Could not save overrides.", true);
  }
}

async function clearChannelTranscode() {
  if (!channelTranscode) return;
  try {
    const data = await request(`${ERSATZTV_API}/channels/${channelTranscode.channel.id}`, {
      method: "DELETE",
    });
    channelTranscode.overrides = data.overrides;
    channelTranscode.effective = data.effective;
    channelTranscodeDraft = data.effective;
    renderSettings(
      els.channelTranscodeGroups,
      channelTranscode.spec.groups,
      channelTranscodeDraft,
      channelTranscode.defaults
    );
    setChannelTranscodeNote("This channel follows the Transcode page again.");
  } catch (error) {
    setChannelTranscodeNote(error.message || "Could not clear overrides.", true);
  }
}

function countLeaves(value) {
  if (!isPlainObject(value)) return 1;
  return Object.values(value).reduce((total, child) => total + countLeaves(child), 0);
}

els.transcodeSave.addEventListener("click", saveTranscode);
els.transcodeReload.addEventListener("click", loadTranscode);
els.channelTranscodeSave.addEventListener("click", saveChannelTranscode);
els.channelTranscodeClear.addEventListener("click", clearChannelTranscode);
els.channelTranscodeClose.addEventListener("click", () => els.channelTranscode.close());

CF.define("transcode", { onShow: loadTranscode });
