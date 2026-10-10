// General Settings: the public and local URLs. The local URL is detected once
// on first boot (server side) and stored; this page loads the stored value and
// lets the operator override it, and set the public URL by hand. It also sets
// the time zone the TV Guide shows times in.

function browserTimeZone() {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "";
  } catch (error) {
    return "";
  }
}

// The full IANA zone list comes from the browser; the current value is always
// present even when the browser does not enumerate it.
function fillTimeZones(selected) {
  const select = $("general-timezone");
  if (!select) return;
  let zones = [];
  try {
    zones = Intl.supportedValuesOf("timeZone") || [];
  } catch (error) {
    zones = [];
  }
  select.textContent = "";
  const auto = document.createElement("option");
  auto.value = "";
  auto.textContent = "Server default";
  select.appendChild(auto);
  zones.forEach((zone) => {
    const option = document.createElement("option");
    option.value = zone;
    option.textContent = zone.replace(/_/g, " ");
    select.appendChild(option);
  });
  const value = selected || browserTimeZone();
  if (value && !zones.includes(value)) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value;
    select.appendChild(option);
  }
  select.value = value;
}

async function loadGeneral() {
  try {
    const data = await request("/api/settings/general");
    const settings = data.settings || {};
    $("general-local-url").value = settings.local_url || "";
    $("general-public-url").value = settings.public_url || "";
    fillTimeZones(settings.timezone || "");
    $("general-result").textContent = "";
  } catch (error) {
    $("general-result").textContent = error.message;
  }
}

async function saveGeneral(event) {
  event.preventDefault();
  const save = $("general-save");
  save.disabled = true;
  $("general-result").textContent = "";
  try {
    const data = await request("/api/settings/general", {
      method: "PUT",
      body: JSON.stringify({
        public_url: $("general-public-url").value.trim(),
        local_url: $("general-local-url").value.trim(),
        timezone: $("general-timezone").value,
      }),
    });
    const settings = data.settings || {};
    $("general-local-url").value = settings.local_url || "";
    $("general-public-url").value = settings.public_url || "";
    fillTimeZones(settings.timezone || "");
    $("general-result").textContent = "Saved.";
  } catch (error) {
    $("general-result").textContent = error.message;
  } finally {
    save.disabled = false;
  }
}

async function changePassword(event) {
  event.preventDefault();
  const note = $("pw-result");
  const oldPassword = $("pw-old").value;
  const newPassword = $("pw-new").value;
  const confirm = $("pw-new2").value;
  if (!oldPassword) {
    note.textContent = "Enter your current password.";
    return;
  }
  if (newPassword.length < 4) {
    note.textContent = "The new password must be at least 4 characters.";
    return;
  }
  if (newPassword !== confirm) {
    note.textContent = "The new passwords do not match.";
    return;
  }
  const button = $("pw-save");
  button.disabled = true;
  note.textContent = "Saving…";
  try {
    await request("/api/settings/password", {
      method: "POST",
      body: JSON.stringify({ old_password: oldPassword, new_password: newPassword }),
    });
    $("pw-old").value = "";
    $("pw-new").value = "";
    $("pw-new2").value = "";
    note.textContent = "Password changed.";
  } catch (error) {
    note.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

{
  const form = $("general-form");
  if (form) form.addEventListener("submit", saveGeneral);
  const passwordForm = $("password-form");
  if (passwordForm) passwordForm.addEventListener("submit", changePassword);
}

CF.define("general", { onShow: loadGeneral });
