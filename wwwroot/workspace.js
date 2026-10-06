"use strict";
(() => {
  const tabs = [...document.querySelectorAll("[data-workspace]")];
  function show(name) {
    for (const tab of tabs) {
      const selected = tab.dataset.workspace === name;
      tab.setAttribute("aria-selected", String(selected));
      tab.tabIndex = selected ? 0 : -1;
      document.getElementById(tab.getAttribute("aria-controls")).hidden = !selected;
    }
  }
  function syncLocation() {
    let anchor;
    try { anchor = decodeURIComponent(location.hash.slice(1)); } catch { anchor = ""; }
    const target = document.getElementById(anchor);
    // Keep existing links to the analytics heading and its controls working.
    const matching = tabs.find(tab => anchor === tab.dataset.workspace || target?.closest("#" + tab.getAttribute("aria-controls")));
    show(matching?.dataset.workspace || "overview");
    if (target) requestAnimationFrame(() => target.scrollIntoView());
  }
  function activate(tab) {
    const name = tab.dataset.workspace;
    if (location.hash !== "#" + name) window.history.pushState(null, "", "#" + name);
    show(name);
    window.scrollTo(0, 0);
  }
  for (const tab of tabs) {
    tab.addEventListener("click", () => activate(tab));
    tab.addEventListener("keydown", event => {
      const index = tabs.indexOf(tab);
      const next = event.key === "ArrowRight" ? (index + 1) % tabs.length :
        event.key === "ArrowLeft" ? (index + tabs.length - 1) % tabs.length :
        event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 : null;
      if (next === null) return;
      event.preventDefault();
      activate(tabs[next]);
      tabs[next].focus({ preventScroll: true });
    });
  }
  window.addEventListener("popstate", syncLocation);
  window.addEventListener("hashchange", syncLocation);
  syncLocation();
})();
