"use strict";
(() => {
  const statuses = {
    demo: "Update checks paused in demo mode",
    checking: "Checking for updates…", development: "Development build", disabled: "Update checks disabled",
    update_available: "Update available", up_to_date: "Up to date", ahead: "Newer than the latest stable release",
    unavailable: "Update check unavailable"
  };
  const installationStates = {
    idle: "", unsupported: "", queued: "Update queued…", checking: "Preparing update…",
    downloading: "Downloading update…", installing: "Installing update…", restarting: "Restarting dashboard…",
    succeeded: "Update complete", failed: "Update failed", rolled_back: "Previous version restored"
  };
  const activeStates = new Set(["queued", "checking", "downloading", "installing", "restarting"]);
  let metadata = null, installation = null, loadedVersion = null, pending = false, checking = false, submitting = false;
  let metadataFresh = false, operationPending = false, pollTimer, pollDeadline = 0, retryDelay = 1500;
  let feedback = "", connectionLost = false, pollExpired = false, awaitingConfirmation = false, previousRequestId = null, actionFailed = false;
  const canUpdate = () => window.dashboardAccess?.enabled === false || window.dashboardAccess?.canAdmin === true;
  const releaseUrl = value => {
    if (typeof value !== "string") return null;
    try {
      const url = new URL(value);
      return url.origin === "https://github.com" && !url.username && !url.password && !url.search && !url.hash &&
        /^\/igor-couto\/github-self-hosted-runner-manager\/releases\/tag\/v\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$/.test(url.pathname) ? url.href : null;
    } catch { return null; }
  };
  async function request(path, method = "GET") {
    const response = await fetch(path, { method, cache: "no-store", signal: AbortSignal.timeout(12000),
      ...(method === "POST" ? { headers: { "Content-Type": "application/json" }, body: "{}" } : {}) });
    let data; try { data = await response.json(); } catch { }
    if (!response.ok) {
      const error = new Error(typeof data?.message === "string" ? data.message : "The request could not be completed. Try checking again.");
      error.status = response.status; throw error;
    }
    return data;
  }
  function acceptMetadata(data) {
    if (typeof data?.currentVersion !== "string" || !Object.hasOwn(statuses, data.status)) throw new Error("Invalid update information.");
    metadata = data; metadataFresh = true; loadedVersion ??= data.currentVersion;
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
  }
  function metadataUnavailable() {
    metadataFresh = false;
    $("app-update-status").textContent = "Update status unavailable";
    $("app-update-status").classList.remove("available");
    $("app-update-message").textContent = "Could not refresh update information. Previously displayed versions may be out of date.";
    $("app-update-indicator").hidden = true;
  }
  function render() {
    const state = installation?.state || "idle", busy = submitting || operationPending || activeStates.has(state);
    const demo = installation?.demo === true;
    const needsReload = state === "succeeded" && !demo && metadataFresh && metadata?.currentVersion !== loadedVersion;
    $("preferences-updates").dataset.state = state;
    $("preferences-updates").dataset.updating = String(busy && !pollExpired);
    $("preferences-updates").querySelector(".preferences-update-actions").setAttribute("aria-busy", String(busy && !pollExpired));
    $("preferences-installed-version").textContent = metadata?.currentVersion || "Unavailable";
    $("preferences-latest-version").textContent = metadata?.latestVersion || "Unavailable";
    $("preferences-update-status").textContent = pollExpired ? "Update status unavailable" : actionFailed ? "Update not started" : connectionLost ? "Reconnecting…" :
      checking ? statuses.checking : submitting ? "Requesting update…" :
      demo && state === "succeeded" ? "Preview complete" : installationStates[state] ||
      (!metadataFresh ? "Update status unavailable" : metadata.stale ? "Update check unavailable · last result is stale" : statuses[metadata.status]);
    $("preferences-update-status").classList.toggle("available", state === "succeeded" || metadataFresh && metadata?.updateAvailable === true && !metadata.stale);
    $("preferences-update-message").textContent = feedback || (installationStates[state] ? installation.message : "") || metadata?.message || "";
    $("preferences-update-support").textContent = !canUpdate() ? "Administrator access is required to install application updates." :
      installation?.supported === false ? installation.message || "Updates are managed by this deployment." :
      !installation ? "Could not check whether this installation supports updates. Check again to retry." :
      demo ? "Preview only. No software will be downloaded or changed." : "";
    $("preferences-update-support").hidden = !$("preferences-update-support").textContent;
    $("preferences-update-check").hidden = !canUpdate();
    $("preferences-update-check").disabled = checking || submitting || busy && !pollExpired;
    $("preferences-update-install").hidden = !canUpdate() || !installation?.supported || needsReload;
    $("preferences-update-install").disabled = busy || checking || !metadataFresh || metadata?.stale === true || !demo && metadata?.updateAvailable !== true;
    $("preferences-update-install").textContent = demo ? "Preview update" : "Update now";
    $("preferences-update-reload").hidden = !needsReload;
    $("preferences-update-notice").hidden = !canUpdate() || !installation?.supported || demo || state === "succeeded";
  }
  function acceptInstallation(data) {
    if (typeof data?.supported !== "boolean" || !Object.hasOwn(installationStates, data.state)) throw new Error("Invalid installation status.");
    const previous = installation?.state;
    installation = data; connectionLost = false; retryDelay = 1500;
    if (awaitingConfirmation && (data.state === "idle" || data.state === "unsupported" ||
      !activeStates.has(data.state) && data.requestId === previousRequestId)) {
      operationPending = false; awaitingConfirmation = false; pollDeadline = 0;
      clearTimeout(pollTimer); pollTimer = null; actionFailed = true;
      feedback = "The server has not confirmed a new update. Check for updates before trying again.";
      return;
    }
    if (activeStates.has(data.state)) {
      operationPending = true; awaitingConfirmation = false; actionFailed = false; feedback = "";
      if (!pollDeadline) pollDeadline = Date.now() + 20 * 60000;
      schedulePoll();
    } else if (["succeeded", "failed", "rolled_back"].includes(data.state)) {
      operationPending = false; awaitingConfirmation = false; actionFailed = false; pollExpired = false; pollDeadline = 0;
      clearTimeout(pollTimer); pollTimer = null; feedback = "";
      if (previous !== data.state) void refreshMetadata();
    }
  }
  async function refreshMetadata() {
    try { acceptMetadata(await request("/api/updates")); } catch { metadataUnavailable(); }
    render();
  }
  function schedulePoll() {
    clearTimeout(pollTimer);
    if (pollExpired) return;
    if (Date.now() >= pollDeadline) {
      pollExpired = true; connectionLost = false;
      feedback = "The update has not reported a result yet. Check for updates to reconnect. If the server stays unavailable, check its Runner Room service.";
      render(); return;
    }
    pollTimer = setTimeout(pollInstallation, retryDelay);
  }
  async function pollInstallation() {
    pollTimer = null;
    try {
      acceptInstallation(await request("/api/updates/installation"));
      if (operationPending) schedulePoll();
    } catch {
      connectionLost = true;
      feedback = "Waiting for the dashboard to reconnect. The update may still be running.";
      retryDelay = Math.min(retryDelay * 1.5, 10000); schedulePoll();
    }
    render();
  }
  async function refreshUpdates() {
    if (pending || checking || submitting || operationPending) return;
    pending = true;
    const results = await Promise.allSettled([request("/api/updates"), request("/api/updates/installation")]);
    try { if (results[0].status === "rejected") throw results[0].reason; acceptMetadata(results[0].value); } catch { metadataUnavailable(); }
    try { if (results[1].status === "rejected") throw results[1].reason; acceptInstallation(results[1].value); } catch { installation = null; }
    pending = false; render();
  }
  $("preferences-update-check").addEventListener("click", async () => {
    if (!canUpdate() || checking || submitting || operationPending && !pollExpired) return;
    checking = true; actionFailed = false; feedback = ""; pollExpired = false; render();
    try {
      acceptMetadata(await request("/api/updates/check", "POST"));
      pollDeadline = operationPending ? Date.now() + 20 * 60000 : 0;
      acceptInstallation(await request("/api/updates/installation"));
      if (operationPending) schedulePoll();
    } catch (error) { metadataUnavailable(); feedback = error.message; if (operationPending) pollExpired = true; }
    finally { checking = false; render(); }
  });
  $("preferences-update-install").addEventListener("click", async () => {
    if (!canUpdate() || $("preferences-update-install").disabled) return;
    submitting = true; actionFailed = false; feedback = ""; pollExpired = false; previousRequestId = installation?.requestId || null;
    pollDeadline = Date.now() + 20 * 60000; render();
    try { acceptInstallation(await request("/api/updates/install", "POST")); }
    catch (error) {
      if (error.status === 409) {
        try {
          const status = await request("/api/updates/installation");
          acceptInstallation(status);
          if (!activeStates.has(status.state)) { actionFailed = true; feedback = error.message; pollDeadline = 0; }
        } catch { actionFailed = true; feedback = error.message; pollDeadline = 0; }
      } else if (error.status) { actionFailed = true; feedback = error.message; pollDeadline = 0; }
      else {
        // A dropped response may already have started the update. Poll; never submit it again automatically.
        operationPending = true; awaitingConfirmation = true; connectionLost = true;
        feedback = "Confirming the update status. The dashboard may be restarting."; schedulePoll();
      }
    }
    finally { submitting = false; render(); }
  });
  $("refresh").addEventListener("click", refreshUpdates);
  $("preferences-update-reload").addEventListener("click", () => location.reload());
  $("preferences-open").addEventListener("click", refreshUpdates);
  window.accessReady.then(refreshUpdates);
  setInterval(refreshUpdates, 60000);
})();
