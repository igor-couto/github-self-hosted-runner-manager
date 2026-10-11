"use strict";
(() => {
  const key = "runner-room-preferences-v1";
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(key) || "{}"); if (!saved || typeof saved !== "object" || Array.isArray(saved)) saved = {}; } catch { saved = {}; }
  const defaults = { language: "en", dateFormat: "locale", clock: "24", timeZone: "local", display: "normal", controls: {}, widgets: {}, order: [], runnerPage: 1, historyPage: 1 };
  const prefs = { ...defaults, ...saved };
  for (const k of ["controls", "widgets"]) if (!prefs[k] || typeof prefs[k] !== "object" || Array.isArray(prefs[k])) prefs[k] = {};
  if (!Array.isArray(prefs.order)) prefs.order = [];
  if (typeof prefs.historyRunner !== "string" || !/^[a-f0-9]{64}$/.test(prefs.historyRunner)) prefs.historyRunner = "";
  for (const [k, values] of Object.entries({ language: ["en", "pt-PT"], dateFormat: ["locale", "iso"], clock: ["12", "24"], timeZone: ["local", "UTC"], display: ["normal", "compact", "tv"] })) if (!values.includes(prefs[k])) prefs[k] = defaults[k];
  window.roomPrefs = prefs;
  window.saveRoomPrefs = () => { try { localStorage.setItem(key, JSON.stringify(prefs)); } catch { } };
  window.roomDate = (value, timeOnly = false) => {
    const date = new Date(value); if (!value || !Number.isFinite(date.getTime())) return "Unavailable";
    if (prefs.dateFormat === "iso") { const stamp = prefs.timeZone === "UTC" ? date.toISOString().replace("T", " ").slice(0, 19) + " UTC" :
      date.getFullYear() + "-" + String(date.getMonth() + 1).padStart(2,"0") + "-" + String(date.getDate()).padStart(2,"0") + " " + date.toLocaleTimeString("en-GB", { hour12: false }); return timeOnly ? stamp.slice(11) : stamp; }
    const opts = { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: prefs.clock === "12", ...(prefs.timeZone === "UTC" ? { timeZone: "UTC", timeZoneName: "short" } : {}) };
    if (!timeOnly) Object.assign(opts, { year: "numeric", month: "short", day: "2-digit" });
    return new Intl.DateTimeFormat(prefs.language, opts).format(date);
  };
  const ids = ["search", "status-filter", "group-by", "runner-sort", "runner-page-size", "history-period", "history-from", "history-to", "history-result", "alerts-severity", "process-view", "process-sort"];
  document.addEventListener("DOMContentLoaded", () => {
    for (const id of ids) {
      const control = document.getElementById(id); if (!control) continue;
      const value = prefs.controls[id]; if (typeof value === "string" && value.length <= 300 && (control.tagName !== "SELECT" || [...control.options].some(o => o.value === value))) control.value = value;
      for (const event of ["input", "change"]) control.addEventListener(event, () => { prefs.controls[id] = control.value; window.saveRoomPrefs(); });
    }
    window.dispatchEvent(new Event("preferences-ready"));
  });
})();
