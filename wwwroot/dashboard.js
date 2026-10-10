"use strict";
(() => {
  const definitions = [
    ["summary", ".summary", "Runner counts"], ["runners", ".runner-panel", "Runners"],
    ["server", ".server-panel", "Server details"], ["activity", ".activity-panel", "Process activity"],
    ["monitoring", ".monitoring-panel", "Detailed system monitoring"], ["alerts", ".alerts-panel", "Alerts"],
    ["quotas", null, "AI provider quotas"]
  ];
  const board = element("div", "widget-board"); board.id = "widget-board";
  document.querySelector(".dashboard-grid").before(board);
  const frames = new Map();
  let editing = false, installPrompt, wakeLock;
  for (const [id, selector, title] of definitions) {
    const frame = element("section", "widget-frame"); frame.dataset.widget = id;
    const tools = element("div", "widget-tools"); tools.setAttribute("aria-label", title + " layout controls");
    tools.append(element("strong", "", title));
    const body = element("div", "widget-body"); body.id = "widget-body-" + id;
    if (selector) body.append(document.querySelector(selector));
    else { const panel = element("section", "panel quota-panel"); panel.append(element("h2", "panel-heading", title)); const content = element("div", "quota-content"); content.id = "quota-content"; panel.append(content); body.append(panel); }
    for (const [action, label] of [["collapse", "Collapse"], ["pin", "Pin"], ["up", "Move up"], ["down", "Move down"], ["hide", "Hide"]]) {
      const button = element("button", "", label); button.type = "button"; button.dataset.action = action;
      button.addEventListener("click", () => {
        const state = roomPrefs.widgets[id] ||= {};
        if (action === "collapse") state.collapsed = !state.collapsed;
        if (action === "pin") state.pinned = !state.pinned;
        if (action === "hide") state.hidden = true;
        if (action === "up" || action === "down") {
          const siblings = [...board.children].filter(n => !n.hidden && !!roomPrefs.widgets[n.dataset.widget]?.pinned === !!state.pinned).map(n => n.dataset.widget);
          const other = siblings[siblings.indexOf(id) + (action === "up" ? -1 : 1)];
          if (other) { const a = roomPrefs.order.indexOf(id), b = roomPrefs.order.indexOf(other); [roomPrefs.order[a], roomPrefs.order[b]] = [roomPrefs.order[b], roomPrefs.order[a]]; }
        }
        saveRoomPrefs(); layout();
        if (action === "hide") $("preferences-open").focus(); else button.focus();
      }); tools.append(button);
    }
    frame.append(tools, body); frames.set(id, frame);
  }
  const note = document.querySelector(".status-note"); board.after(note);
  document.querySelector(".dashboard-grid").remove();
  roomPrefs.order = [...new Set([...roomPrefs.order.filter(id => frames.has(id)), ...frames.keys()])];
  for (const id of frames.keys()) { const s = roomPrefs.widgets[id]; if (!s || typeof s !== "object" || Array.isArray(s)) roomPrefs.widgets[id] = {}; }
  function layout() {
    board.classList.toggle("editing", editing); $("widget-list").replaceChildren();
    const order = [...roomPrefs.order].sort((a,b) => Number(!!roomPrefs.widgets[b]?.pinned) - Number(!!roomPrefs.widgets[a]?.pinned));
    for (const id of order) {
      const frame = frames.get(id), state = roomPrefs.widgets[id], title = definitions.find(d => d[0] === id)[2];
      frame.hidden = !!state.hidden; frame.classList.toggle("pinned", !!state.pinned);
      frame.querySelector(".widget-body").hidden = !!state.collapsed;
      const collapse = frame.querySelector('[data-action="collapse"]'); collapse.textContent = state.collapsed ? "Expand" : "Collapse";
      collapse.setAttribute("aria-expanded", String(!state.collapsed)); collapse.setAttribute("aria-controls", "widget-body-" + id);
      const pin = frame.querySelector('[data-action="pin"]'); pin.textContent = state.pinned ? "Unpin" : "Pin"; pin.setAttribute("aria-pressed", String(!!state.pinned));
      frame.classList.toggle("collapsed", !!state.collapsed); board.append(frame);
      const label = element("label", "widget-choice"); const check = element("input"); check.type = "checkbox"; check.checked = !state.hidden;
      check.addEventListener("change", () => { state.hidden = !check.checked; saveRoomPrefs(); layout(); }); label.append(check, document.createTextNode(title)); $("widget-list").append(label);
    }
  }
  async function display() {
    document.documentElement.dataset.display = roomPrefs.display; $("exit-tv").hidden = roomPrefs.display !== "tv";
    if (roomPrefs.display !== "tv") { await wakeLock?.release(); wakeLock = null; }
    else if (!wakeLock && !document.hidden && navigator.wakeLock) try { wakeLock = await navigator.wakeLock.request("screen"); wakeLock.addEventListener("release", () => { wakeLock = null; }); } catch { /* Display stays usable without a wake lock. */ }
  }
  $("preferences-open").addEventListener("click", () => { layout(); $("preferences-dialog").showModal(); });
  $("preferences-close").addEventListener("click", () => $("preferences-dialog").close());
  $("pref-theme").addEventListener("change", e => setRoomTheme(e.target.value));
  for (const key of ["language", "dateFormat", "clock", "timeZone", "display"]) {
    const input = $("pref-" + key); input.value = roomPrefs[key]; input.addEventListener("change", () => {
      roomPrefs[key] = input.value; saveRoomPrefs(); display(); if (snapshot) render(); if (historyData) renderHistory(); window.translateRoom?.();
    });
  }
  $("edit-layout").addEventListener("click", () => { editing = !editing; $("edit-layout").setAttribute("aria-pressed", String(editing)); layout(); $("preferences-dialog").close(); location.hash = "overview"; });
  $("exit-tv").addEventListener("click", () => { roomPrefs.display = "normal"; $("pref-display").value = "normal"; saveRoomPrefs(); display(); if (document.fullscreenElement) document.exitFullscreen().catch(() => {}); });
  $("fullscreen").addEventListener("click", async () => { $("preferences-dialog").close(); try { if (document.fullscreenElement) await document.exitFullscreen(); else await document.documentElement.requestFullscreen(); } catch { $("install-message").textContent = "Full screen is unavailable in this browser."; $("preferences-dialog").showModal(); } });
  $("reset-preferences").addEventListener("click", () => { try { localStorage.removeItem("runner-room-preferences-v1"); localStorage.removeItem("runner-room-theme"); } catch {} location.reload(); });
  window.addEventListener("beforeinstallprompt", e => { e.preventDefault(); installPrompt = e; });
  $("install-app").addEventListener("click", async () => { if (installPrompt) { await installPrompt.prompt(); installPrompt = null; } else $("install-message").textContent = isSecureContext ? "Use your browser’s install menu. On iPhone, Share → Add to Home Screen. This browser may not offer installation, or the app may already be installed." : "Open this dashboard over HTTPS to install it on your phone or computer. Local network HTTP addresses cannot install a service worker."; });
  document.addEventListener("visibilitychange", display);
  if ("serviceWorker" in navigator && isSecureContext) navigator.serviceWorker.register("/sw.js").catch(() => {});
  async function quotas() {
    const box = $("quota-content");
    if (!window.dashboardAccess.canAdmin) { box.replaceChildren(element("p", "detail-hint", "Administrator access is required to view provider quotas.")); return; }
    try {
      const res = await fetch("/api/quotas", {cache:"no-store", signal:AbortSignal.timeout(10000)}); if (!res.ok) throw Error();
      const data = await res.json(); box.replaceChildren(element("p", "detail-hint", data.demo ? "Sample quota data · demo only" : "Provider spending caps or adapter readings. These are separate from consumer chat subscriptions."));
      if (!data.enabled) box.append(element("p", "detail-hint", "Optional monitoring is disabled. Configure Quotas in the server settings to connect OpenRouter or a JSON file adapter."));
      for (const p of data.providers) {
        const card = element("article", "quota-card"); card.append(element("h3", "", p.name), element("p", "", `${p.remaining ?? "Unknown"} ${p.unit || ""} remaining · limit ${p.limit ?? "Unknown"}`), element("p", "detail-hint", `${p.status} · ${dateTime(p.observedAt)}${p.resetAt ? " · resets " + dateTime(p.resetAt) : ""}`));
        if (p.message) card.append(element("p", "detail-hint", p.message)); box.append(card);
      }
    } catch { box.replaceChildren(element("p", "detail-hint", "Quota readings unavailable. Retrying automatically.")); }
  }
  layout(); display(); window.accessReady.then(quotas); setInterval(quotas, 60000);
})();
