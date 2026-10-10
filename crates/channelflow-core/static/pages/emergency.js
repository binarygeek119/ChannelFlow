const EMERGENCY_API = "/api/plugins/com.channelflow.emergency";

function emergencyDisplayLabel(value) {
  const labels = {
    off: "Off",
    cutin: "Switch to the alerts screen every so often",
    ticker: "Scrolling alert text at the bottom",
  };
  return labels[value] || String(value);
}

function syncEmergencyFields() {
  const cutin = $("alert-display") && $("alert-display").value === "cutin";
  const interval = $("alert-cutin-fields");
  const duration = $("alert-cutin-duration-field");
  if (interval) interval.hidden = !cutin;
  if (duration) duration.hidden = !cutin;
}

function fillEmergencyDisplay(selected) {
  const select = $("alert-display");
  if (!select) return;
  select.textContent = "";
  ["off", "cutin", "ticker"].forEach((value) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = emergencyDisplayLabel(value);
    select.appendChild(option);
  });
  if (selected) select.value = selected;
}

function collectEmergencySettings() {
  return {
    alert_display: $("alert-display").value,
    cutin_interval_minutes: Number($("alert-cutin-interval").value) || 15,
    cutin_duration_seconds: Number($("alert-cutin-duration").value) || 20,
  };
}

async function loadEmergency() {
  try {
    const data = await request(EMERGENCY_API + "/");
    const settings = data.settings || {};
    fillEmergencyDisplay(settings.alert_display);
    const interval = $("alert-cutin-interval");
    if (interval) interval.value = settings.cutin_interval_minutes;
    const duration = $("alert-cutin-duration");
    if (duration) duration.value = settings.cutin_duration_seconds;
    syncEmergencyFields();
    $("alert-result").textContent = "";
  } catch (error) {
    $("alert-result").textContent = error.message;
  }
}

async function saveEmergency(event) {
  if (event && event.preventDefault) event.preventDefault();
  const save = $("alert-save");
  if (save) save.disabled = true;
  try {
    await request(EMERGENCY_API + "/", { method: "PUT", body: JSON.stringify(collectEmergencySettings()) });
    if ($("alert-result")) $("alert-result").textContent = "Saved.";
    syncEmergencyFields();
  } catch (error) {
    if ($("alert-result")) $("alert-result").textContent = error.message;
  } finally {
    if (save) save.disabled = false;
  }
}

async function testEmergency() {
  const test = $("alert-test");
  if (test) test.disabled = true;
  try {
    // Save first so the test describes what the form holds.
    await request(EMERGENCY_API + "/", { method: "PUT", body: JSON.stringify(collectEmergencySettings()) });
    const data = await request(EMERGENCY_API + "/test");
    const result = data.result || {};
    $("alert-result").textContent = result.detail || (result.ok ? "OK" : "Off");
  } catch (error) {
    $("alert-result").textContent = error.message;
  } finally {
    if (test) test.disabled = false;
  }
}

{
  const form = $("alert-form");
  if (form) form.addEventListener("submit", saveEmergency);
  const test = $("alert-test");
  if (test) test.addEventListener("click", testEmergency);
  const display = $("alert-display");
  if (display) display.addEventListener("change", syncEmergencyFields);
}


CF.define("emergency", { onShow: loadEmergency });
