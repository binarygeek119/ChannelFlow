// General Settings: the public and local URLs. The local URL is detected once
// on first boot (server side) and stored; this page loads the stored value and
// lets the operator override it, and set the public URL by hand.

async function loadGeneral() {
  try {
    const data = await request("/api/settings/general");
    const settings = data.settings || {};
    $("general-local-url").value = settings.local_url || "";
    $("general-public-url").value = settings.public_url || "";
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
      }),
    });
    const settings = data.settings || {};
    $("general-local-url").value = settings.local_url || "";
    $("general-public-url").value = settings.public_url || "";
    $("general-result").textContent = "Saved.";
  } catch (error) {
    $("general-result").textContent = error.message;
  } finally {
    save.disabled = false;
  }
}

{
  const form = $("general-form");
  if (form) form.addEventListener("submit", saveGeneral);
}

CF.define("general", { onShow: loadGeneral });
