"use strict";
(() => {
  const storageKey = "runner-room-theme";
  const root = document.documentElement;

  // Run before the stylesheet loads to avoid flashing the wrong theme.
  let theme = "dark";
  try {
    if (localStorage.getItem(storageKey) === "light") theme = "light";
  } catch { /* The default also works when browser storage is unavailable. */ }
  root.dataset.theme = theme;

  document.addEventListener("DOMContentLoaded", () => {
    const toggle = document.getElementById("theme-toggle");
    const label = document.getElementById("theme-label");
    function updateButton() {
      const next = root.dataset.theme === "dark" ? "light" : "dark";
      label.textContent = `${next === "light" ? "Light" : "Dark"} theme`;
      toggle.setAttribute("aria-label", `Switch to ${next} theme`);
      toggle.title = `Switch to ${next} theme`;
    }
    updateButton();
    toggle.hidden = false;
    toggle.addEventListener("click", () => {
      root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
      updateButton();
      try {
        localStorage.setItem(storageKey, root.dataset.theme);
      } catch { /* Keep the toggle working even without persistence. */ }
    });
  });
})();
