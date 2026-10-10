// Client Logs: the log lines ChannelFlow TV apps sent back, read from the
// server's per-device files. Copied from v1.0.0's client-logs viewer.

let clientLogDevices = [];

function currentLogDeviceId() {
  return $("client-logs-device") ? $("client-logs-device").value || "" : "";
}

function currentLogFile() {
  return $("client-logs-file") ? $("client-logs-file").value || "" : "";
}

function fillLogOptions(select, items, labelOf, keep) {
  if (!select) return;
  const preferred = keep || select.value;
  select.textContent = "";
  if (!items.length) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "Nothing yet";
    select.appendChild(option);
    return;
  }
  items.forEach((item) => {
    const option = document.createElement("option");
    option.value = labelOf(item);
    option.textContent = labelOf(item);
    select.appendChild(option);
  });
  if (preferred && items.some((item) => labelOf(item) === preferred)) {
    select.value = preferred;
  }
}

async function loadClientLogContents() {
  const view = $("client-logs-view");
  if (!view) return;
  const deviceId = currentLogDeviceId();
  if (!deviceId) {
    fillLogOptions($("client-logs-file"), [], (file) => "");
    view.textContent = "No ChannelFlow TV clients have sent logs yet.";
    return;
  }
  view.textContent = "Loading…";
  try {
    const params = new URLSearchParams();
    const file = currentLogFile();
    if (file) params.set("file", file);
    const tail = "tail=262144";
    const detail = await request(
      `/api/client-logs/${encodeURIComponent(deviceId)}?${params.toString() ? params + "&" : ""}${tail}`
    );
    fillLogOptions($("client-logs-file"), detail.files || [], (file) => file.name, detail.latest_file);
    view.textContent = detail.content || "(empty log)";
  } catch (error) {
    view.textContent = error.message || "Could not load client logs.";
  }
}

async function loadClientLogList() {
  const view = $("client-logs-view");
  if (!view) return;
  try {
    const data = await request("/api/client-logs");
    clientLogDevices = Array.isArray(data.devices) ? data.devices : [];
    fillLogOptions(
      $("client-logs-device"),
      clientLogDevices,
      (device) => {
        const label = device.device_name || device.device_id;
        const extra = [device.app_version, device.os_version].filter(Boolean).join(" · ");
        return extra ? `${label} (${extra})` : label;
      },
      clientLogDevices.length ? currentLogDeviceId() : ""
    );
    if (!clientLogDevices.length) {
      view.textContent = "No ChannelFlow TV clients have sent logs yet.";
      return;
    }
    await loadClientLogContents();
  } catch (error) {
    view.textContent = error.message || "Could not load client logs.";
  }
}

{
  const device = $("client-logs-device");
  if (device) device.addEventListener("change", loadClientLogContents);
  const file = $("client-logs-file");
  if (file) file.addEventListener("change", loadClientLogContents);
  const refresh = $("clients-refresh-logs");
  if (refresh) refresh.addEventListener("click", loadClientLogList);
}

CF.define("clientlogs", { onShow: loadClientLogList });