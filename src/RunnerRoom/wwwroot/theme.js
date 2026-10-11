"use strict";
(() => {
  const root = document.documentElement, media = matchMedia("(prefers-color-scheme: dark)");
  let mode = "dark";
  try { const value = localStorage.getItem("runner-room-theme"); if (["dark", "light", "system", "night"].includes(value)) mode = value; } catch { }
  function apply() {
    root.dataset.theme = mode === "system" ? (media.matches ? "dark" : "light") : mode; root.dataset.themeMode = mode;
    const label = document.getElementById("theme-label"), toggle = document.getElementById("theme-toggle");
    if (label) label.textContent = root.dataset.theme === "light" ? "Dark theme" : "Light theme";
    if (toggle) { toggle.hidden = false; toggle.setAttribute("aria-label", label?.textContent || "Change theme"); }
    const select = document.getElementById("pref-theme"); if (select) select.value = mode;
    document.querySelector('meta[name="theme-color"]')?.setAttribute("content", root.dataset.theme === "light" ? "#edf1f6" : root.dataset.theme === "night" ? "#10141b" : "#273340");
  }
  window.setRoomTheme = value => { if (!["dark", "light", "system", "night"].includes(value)) return; mode = value; try { localStorage.setItem("runner-room-theme", mode); } catch { } apply(); };
  apply(); media.addEventListener("change", apply);
  document.addEventListener("DOMContentLoaded", () => { apply(); document.getElementById("theme-toggle")?.addEventListener("click", () => window.setRoomTheme(root.dataset.theme === "light" ? "dark" : "light")); });
})();
