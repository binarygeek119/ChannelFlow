// TV Guide. The grid arrives later; for now the page reports which time zone
// it will show times in, taken from General Settings ("Server default" follows
// this machine's clock).

async function loadGuide() {
  const note = $("guide-tz");
  if (!note) return;
  let zone = "";
  try {
    const data = await request("/api/settings/general");
    zone = (data.settings || {}).timezone || "";
  } catch (error) {
    zone = "";
  }
  const effective = zone || (Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
  let clock = "";
  try {
    clock = new Date().toLocaleTimeString(undefined, {
      timeZone: zone || undefined,
      timeZoneName: "short",
    });
  } catch (error) {
    clock = "";
  }
  note.textContent = `Guide time zone: ${effective}${clock ? ` — now ${clock}` : ""}.`;
}

CF.define("guide", { onShow: loadGuide });