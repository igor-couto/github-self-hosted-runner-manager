"use strict";
(() => {
  let pending = false;
  const statuses = {
    demo: "Update checks paused in demo mode",
    checking: "Checking for updates…", development: "Development build", disabled: "Update checks disabled",
    update_available: "Update available", up_to_date: "Up to date", ahead: "Newer than the latest stable release",
    unavailable: "Update check unavailable"
  };
  const releaseUrl = value => {
    if (typeof value !== "string") return null;
    try {
      const url = new URL(value);
      return url.origin === "https://github.com" && !url.username && !url.password && !url.search && !url.hash &&
        /^\/igor-couto\/github-self-hosted-runner-manager\/releases\/tag\/v\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$/.test(url.pathname) ? url.href : null;
    } catch { return null; }
  };
  async function refreshUpdates() {
    if (pending) return;
    pending = true;
    try {
      const response = await fetch("/api/updates", {cache:"no-store", signal:AbortSignal.timeout(12000)});
      if (!response.ok) throw new Error();
      const data = await response.json();
      if (typeof data.currentVersion !== "string" || !Object.hasOwn(statuses, data.status)) throw new Error();
      $("app-installed-version").textContent = data.currentVersion;
      $("app-latest-version").textContent = data.latestVersion || "Unavailable";
      $("app-update-status").textContent = data.stale ? "Update check unavailable · last result is stale" : statuses[data.status];
      $("app-update-status").classList.toggle("available", data.updateAvailable === true && !data.stale);
      $("app-update-message").textContent = data.message || "";
      $("app-update-checked").textContent = data.checkedAt ? "Last successful check: " + dateTime(data.checkedAt) : "No successful check yet.";
      const url = releaseUrl(data.releaseUrl);
      for (const id of ["app-release-link", "app-update-indicator"]) {
        const link = $(id); link.hidden = !url || id === "app-update-indicator" && (data.updateAvailable !== true || data.stale);
        if (url) link.href = url; else link.removeAttribute("href");
      }
      $("app-update-indicator").textContent = data.demo ? "Demo update available" : "Update available";
      $("app-release-link").textContent = data.demo ? "Sample release link ↗" : "View release notes ↗";
    } catch {
      $("app-update-status").textContent = "Update status unavailable";
      $("app-update-status").classList.remove("available");
      $("app-update-message").textContent = "Could not refresh update information. Previously displayed versions may be out of date.";
      $("app-update-indicator").hidden = true;
    } finally { pending = false; }
  }
  $("refresh").addEventListener("click", refreshUpdates);
  window.accessReady.then(refreshUpdates);
  setInterval(refreshUpdates, 60000);
})();
