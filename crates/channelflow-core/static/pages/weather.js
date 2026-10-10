const WEATHER_API = "/api/plugins/com.channelflow.weather";
const WEATHER_SCREEN_LABELS = {
  hazards: "Weather alerts",
  current: "Current conditions",
  latest_observations: "Latest observations",
  hourly: "Hourly forecast",
  hourly_graph: "Hourly graph",
  travel: "Travel cities",
  regional: "Regional forecast",
  local: "Local forecast",
  extended: "Extended forecast",
  almanac: "Almanac",
  spc_outlook: "Storm outlook",
  radar: "Radar",
};

function fillWxSelect(id, values, labels, selected) {
  const select = $(id);
  if (!select || !values) return;
  select.textContent = "";
  values.forEach((value) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = (labels && labels[value]) || String(value);
    select.appendChild(option);
  });
  if (selected) select.value = selected;
}

function renderWxScreens(enabled) {
  const container = $("wx-screens");
  if (!container) return;
  container.textContent = "";
  Object.entries(WEATHER_SCREEN_LABELS).forEach(([id, label]) => {
    const labelEl = document.createElement("label");
    labelEl.className = "field-note";
    const box = document.createElement("input");
    box.type = "checkbox";
    box.dataset.wxScreen = id;
    box.checked = !enabled || enabled.includes(id);
    labelEl.appendChild(box);
    labelEl.appendChild(document.createTextNode(` ${label}`));
    container.appendChild(labelEl);
  });
}

function collectWeatherSettings() {
  return {
    weatherstar_variant: $("wx-variant").value,
    source: $("wx-source").value,
    default_location: $("wx-location").value.trim(),
    units: $("wx-units").value,
    auto_wide_169: $("wx-wide169").checked,
    screens: Array.from(document.querySelectorAll("#wx-screens input[data-wx-screen]:checked")).map((el) => el.dataset.wxScreen),
  };
}

async function loadWeather() {
  try {
    const data = await request(WEATHER_API + "/");
    const settings = data.settings || {};
    const options = data.options || {};
    fillWxSelect("wx-variant", options.star_variants, { ws4kp: "WeatherStar 4000", ws3kp: "WeatherStar 3000" }, settings.weatherstar_variant);
    fillWxSelect("wx-source", options.sources, { auto: "Auto (NOAA in the US, Open-Meteo worldwide)", us: "United States (NOAA)", world: "World (Open-Meteo)" }, settings.source);
    fillWxSelect("wx-units", options.units, { us: "US", si: "Metric" }, settings.units);
    $("wx-location").value = settings.default_location || "";
    $("wx-wide169").checked = settings.auto_wide_169 !== false;
    renderWxScreens(settings.screens);
    $("wx-result").textContent = "";
  } catch (error) {
    $("wx-result").textContent = error.message;
  }
}

async function saveWeather(event) {
  if (event && event.preventDefault) event.preventDefault();
  const save = $("wx-save");
  if (save) save.disabled = true;
  try {
    await request(WEATHER_API + "/", { method: "PUT", body: JSON.stringify(collectWeatherSettings()) });
    if ($("wx-result")) $("wx-result").textContent = "Saved.";
  } catch (error) {
    if ($("wx-result")) $("wx-result").textContent = error.message;
  } finally {
    if (save) save.disabled = false;
  }
}

async function testWeather() {
  const test = $("wx-test");
  if (test) test.disabled = true;
  try {
    // Save first so the test uses the form's location.
    await request(WEATHER_API + "/", { method: "PUT", body: JSON.stringify(collectWeatherSettings()) });
    const data = await request(WEATHER_API + "/test");
    const result = data.result || {};
    $("wx-result").textContent = `${result.ok ? "OK" : "Failed"}: ${result.detail || ""}`;
  } catch (error) {
    $("wx-result").textContent = error.message;
  } finally {
    if (test) test.disabled = false;
  }
}

{
  const form = $("weather-form");
  if (form) form.addEventListener("submit", saveWeather);
  const test = $("wx-test");
  if (test) test.addEventListener("click", testWeather);
}

CF.define("weather", { onShow: loadWeather });
