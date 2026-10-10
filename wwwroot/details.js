"use strict";
(() => {
  let request;
  const fields = values => {
    const dl = element("dl", "detail-grid");
    for (const [label, value] of values) { const entry = element("div"), dd = element("dd", "", value ?? "Unavailable"); dd.dataset.noTranslate = ""; entry.append(element("dt", "", label), dd); dl.append(entry); } return dl;
  };
  async function show() {
    if (!location.hash.startsWith("#/")) { request?.abort(); return; }
    request?.abort(); const controller = new AbortController(); request = controller;
    const hash = location.hash, parts = hash.slice(2).split("/"), kind = parts[0];
    let id; try { id = decodeURIComponent(parts.slice(1).join("/")); } catch { id = ""; }
    const content = $("detail-content"); $("detail-message").textContent = "Loading details…";
    const timeout = setTimeout(() => controller.abort(), 12000);
    try {
      const url = kind === "job" ? `/api/history/jobs/${encodeURIComponent(id)}` : "/api/runners";
      const response = await fetch(url, {cache:"no-store", signal:controller.signal});
      if (!response.ok) throw Error(response.status === 404 ? "This item is no longer available or has left the retention window." : "Cannot refresh these details. Please retry.");
      const data = await response.json(); if (hash !== location.hash || request !== controller) return;
      if (data.error) throw Error(data.error);
      let title, children = [];
      if (kind === "runner" || kind === "current-job") {
        const r = data.runners.find(r => r.id === id); if (!r) throw Error("Runner not found in the latest scan.");
        title = kind === "runner" ? r.displayName : `${r.displayName} · Current job`;
        children = kind === "runner" ? [badge(r.status, labels[r.status]), runnerDetails(r, data.checkedAt)] : [detailLink("runner", r.id, r.displayName), currentJobPanel(r, data.checkedAt)];
      } else if (kind === "repository") {
        const runners = data.runners.filter(r => r.repository === id); if (!runners.length) throw Error("No currently discovered runners belong to this repository.");
        title = id; children = [fields([["Runners", runners.length], ["Busy", runners.filter(r => r.status === "busy").length]])];
        children.push(monitorTable(["Runner", "Status", "Version", "Current job"], runners.map(r => [detailLink("runner", r.id, r.displayName), r.status, r.version || "Unavailable", r.currentJob ? detailLink("current-job", r.id, r.currentJob.name || "Current job") : "None observed"])));
      } else if (kind === "server") {
        title = data.host; const s = data.system;
        children = [fields([["Watched directories", (data.roots || [data.root]).join(" · ")], ["CPU", Number.isFinite(s?.cpuUsagePercent) ? `${s.cpuUsagePercent.toFixed(1)}%` : "Unavailable"], ["Logical cores", s?.logicalProcessors], ["Architecture", s?.architecture], ["RAM used / total", `${formatBytes(s?.memory?.usedBytes)} / ${formatBytes(s?.memory?.totalBytes)}`], ["System uptime", duration(s?.uptimeSeconds)]])];
        const metrics = await fetch("/api/system", {signal:controller.signal, cache:"no-store"});
        if (metrics.ok) { const m = await metrics.json(); children.push(element("h2", "", "Filesystems"), element("p", "detail-hint", `Measured ${dateTime(m.checkedAt)} · ${(m.warnings || []).join(" ")}`), monitorTable(["Mount", "Used", "Total"], (m.fileSystems || []).map(f => [f.mount, formatBytes(f.usage?.usedBytes), formatBytes(f.usage?.totalBytes)]))); }
        else children.push(element("p", "detail-hint", "Detailed system readings are unavailable. Retrying automatically."));
        const link = element("a", "project-link", "Detailed system monitoring"); link.href = "#monitoring-title"; children.push(link);
      } else if (kind === "job") {
        title = data.name; children = [detailLink("runner", data.runnerId, "Runner details"), fields([["Started", dateTime(data.startedAt)], ["Completed", dateTime(data.completedAt)], ["Outcome", data.result || "Completion not observed"], ["Duration", duration(data.durationSeconds)]])];
      } else throw Error("Unknown detail page.");
      if (hash !== location.hash || request !== controller) return;
      $("detail-title").textContent = title; content.replaceChildren(...children);
      $("detail-checked").textContent = "Checked " + roomDate(data.checkedAt || new Date().toISOString(), true); $("detail-message").textContent = data.warning || "";
    } catch (error) { if (hash === location.hash && request === controller) { content.replaceChildren(); $("detail-title").textContent = "Details unavailable"; $("detail-message").textContent = error.name === "AbortError" ? "Details timed out. Retrying automatically." : error.message; } }
    finally { clearTimeout(timeout); }
  }
  window.addEventListener("detail-route", show);
  $("refresh").addEventListener("click", show); setInterval(show, 15000); show();
})();
