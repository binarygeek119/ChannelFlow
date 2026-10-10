// Quick Pin: pair an app by typing the PIN it shows. The server encrypts this
// instance's Live TV URLs with the PIN and hands the ciphertext to the relay;
// this page just collects the PIN and shows what will be sent.

async function loadQuickPin() {
  try {
    const data = await request("/api/quickpin");
    $("qp-server").value = data.server || "";
    const urls = data.urls || {};
    $("qp-local-m3u").textContent = urls.m3uLocal || "not set";
    $("qp-local-xmltv").textContent = urls.xmltvLocal || "not set";
    $("qp-public-m3u").textContent = urls.m3uPublic || "not set";
    $("qp-public-xmltv").textContent = urls.xmltvPublic || "not set";
  } catch (error) {
    $("qp-result").textContent = error.message;
  }
}

async function pairQuickPin(event) {
  event.preventDefault();
  const pin = $("qp-pin").value.trim();
  const server = $("qp-server").value.trim();
  const button = $("qp-pair");
  if (!pin) {
    $("qp-result").textContent = "Enter the PIN shown on the app.";
    return;
  }
  button.disabled = true;
  $("qp-result").textContent = "Pairing…";
  try {
    const data = await request("/api/quickpin/pair", {
      method: "POST",
      body: JSON.stringify({ pin, server }),
    });
    await loadQuickPin();
    if (data.delivered) {
      $("qp-pin").value = "";
      $("qp-result").textContent = "Paired — the app received the details.";
    } else {
      $("qp-result").textContent = "That PIN was not waiting any more. Check the app and try again.";
    }
  } catch (error) {
    $("qp-result").textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

{
  const form = $("quickpin-form");
  if (form) form.addEventListener("submit", pairQuickPin);
}

CF.define("quickpin", { onShow: loadQuickPin });
