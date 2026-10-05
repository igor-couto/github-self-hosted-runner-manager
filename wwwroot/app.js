"use strict";
const $ = id => document.getElementById(id);
let snapshot = null;
let loading = false;
const labels = { busy: "Busy", idle: "Idle", offline: "Offline", unknown: "Unknown" };

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes < 0) return "—";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let unit = 0;
  while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit++; }
  return `${bytes.toFixed(unit ? 1 : 0)} ${units[unit]}`;
}

function setResource(id, percent, detail) {
  const available = Number.isFinite(percent) && percent >= 0 && percent <= 100;
  $(`${id}-value`).textContent = available ? `${Math.round(percent)}%` : "—";
  $(`${id}-meter`).hidden = !available;
  $(`${id}-meter`).value = available ? percent : 0;
  $(`${id}-meter`).classList.toggle("high", available && percent >= 90);
  $(`${id}-detail`).textContent = detail;
}

function renderSystem(system) {
  const cores = Number.isInteger(system?.logicalProcessors) && system.logicalProcessors > 0
    ? `${system.logicalProcessors} logical ${system.logicalProcessors === 1 ? "core" : "cores"}` : null;
  const cpuDetail = [cores, system?.architecture?.toUpperCase()].filter(Boolean).join(" · ");
  const cpuAvailable = Number.isFinite(system?.cpuUsagePercent);
  setResource("cpu", system?.cpuUsagePercent, cpuAvailable ? cpuDetail :
    (cores ? `Sampling… ${cpuDetail}` : "Unavailable"));
  for (const id of ["memory", "disk"]) {
    const usage = system?.[id];
    const available = usage && Number.isFinite(usage.totalBytes) && usage.totalBytes > 0 &&
      Number.isFinite(usage.usedBytes) && usage.usedBytes >= 0 && usage.usedBytes <= usage.totalBytes;
    setResource(id, available ? usage.usedPercent : null, available ?
      `${formatBytes(usage.usedBytes)} / ${formatBytes(usage.totalBytes)} used` : "Unavailable");
  }
  $("disk-available").textContent = Number.isFinite(system?.disk?.availableBytes)
    ? `${formatBytes(system.disk.availableBytes)} available` : "";
  const seconds = system?.uptimeSeconds;
  if (Number.isFinite(seconds) && seconds >= 0) {
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor(seconds / 3600) % 24;
    const minutes = Math.floor(seconds / 60) % 60;
    $("system-uptime").textContent = `${days ? `${days}d ` : ""}${hours || days ? `${hours}h ` : ""}${minutes}m`;
  } else {
    $("system-uptime").textContent = "Unavailable";
  }
}

const expanded = new Set();
const githubLabels = { online: "Online", offline: "Offline", unknown: "Unknown", not_configured: "Not checked" };
function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}
function duration(seconds) {
  if (!Number.isFinite(seconds)) return "Unavailable";
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor(seconds / 3600) % 24;
  const minutes = Math.floor(seconds / 60) % 60;
  return `${days ? `${days}d ` : ""}${hours || days ? `${hours}h ` : ""}${minutes}m`;
}
function dateTime(value) { return value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString() : "Unavailable"; }
function badge(status, text) {
  const node = element("span", `badge ${status}`);
  const dot = element("i", `dot ${status}`);
  dot.setAttribute("aria-hidden", "true");
  node.append(dot, document.createTextNode(text));
  return node;
}
function runnerDetails(runner) {
  const box = element("div", "runner-details");
  const fields = element("dl", "detail-grid");
  const add = (label, value) => {
    const field = element("div");
    field.append(element("dt", "", label), element("dd", "", value || "Unavailable"));
    fields.append(field);
  };
  add("Registered name", runner.name);
  add("Repository", runner.repository || (runner.organization ? "Organization runner" : null));
  add("Organization / owner", runner.organization);
  add("Machine", runner.host);
  add("Platform", [runner.operatingSystem, runner.architecture].filter(Boolean).join(" · "));
  add("Runner version", runner.version);
  add("Local process", `${runner.processStatus || "unknown"}${runner.pid ? ` · PID ${runner.pid}` : ""}`);
  add("Runner uptime", duration(runner.uptimeSeconds));
  add("Service state", `${runner.serviceState.replaceAll("_", " ")}${runner.serviceSubState ? ` / ${runner.serviceSubState}` : ""}`);
  add("Custom group", runner.group || "Ungrouped");
  add("Registered runner group", runner.runnerGroup);
  add("Last observed job", runner.lastJob ? `${runner.lastJob.name} · ${runner.lastJob.result || "Started (completion not recorded)"}` : "No recent job record");
  add("Last job activity", dateTime(runner.lastActivityAt));
  add("GitHub checked", dateTime(runner.gitHub.checkedAt));
  add("GitHub activity", runner.gitHub.busy === true ? "Busy" : runner.gitHub.busy === false ? "Not busy" : "Not checked");
  box.append(fields);
  const tags = element("div", "runner-tags");
  tags.append(element("span", "detail-label", "GitHub labels"));
  if (runner.gitHub.labels?.length) for (const label of runner.gitHub.labels) tags.append(element("span", "runner-tag", label));
  else tags.append(element("span", "detail-hint", runner.gitHub.labels ? "No registered labels" : "Unavailable until GitHub can be checked"));
  box.append(tags);
  for (const [label, value] of [["Directory", runner.path], ["Service", runner.serviceName]]) {
    if (!value) continue;
    const line = element("div", "detail-path");
    line.append(element("span", "detail-label", label), element("code", "", value));
    box.append(line);
  }
  if (runner.gitHub.message) box.append(element("p", "detail-hint", runner.gitHub.message));
  if (runner.gitHubUrl) {
    try {
      const url = new URL(runner.gitHubUrl);
      if (url.protocol === "https:" && !url.username && !url.password) {
        const link = element("a", "project-link", "Open on GitHub ↗");
        link.href = url.href; link.target = "_blank"; link.rel = "noopener noreferrer";
        box.append(link);
      }
    } catch { /* Missing registration URL. */ }
  }
  return box;
}
function render() {
  renderSystem(snapshot.system);
  const runners = snapshot.runners;
  const totals = Object.fromEntries(Object.keys(labels).map(status => [status, runners.filter(r => r.status === status).length]));
  const running = runners.filter(r => r.processStatus === "running").length;
  const unknownProcesses = runners.filter(r => r.processStatus === "unknown").length;
  const onShare = runners.length ? running / runners.length * 100 : 0;
  const unknownShare = runners.length ? unknownProcesses / runners.length * 100 : 0;
  $("demo").hidden = !snapshot.demo;
  $("total").textContent = runners.length;
  for (const key of Object.keys(labels)) {
    if ($(key)) $(key).textContent = totals[key];
    $(`legend-${key}`).textContent = totals[key];
  }
  $("activity-percent").textContent = runners.length ? `${Math.round(onShare)}%` : "—";
  $("activity-caption").textContent = snapshot.error ? "Waiting for runner status" : runners.length ? `${running} of ${runners.length} runner processes active` : "No runners detected";
  $("activity-ring").setAttribute("aria-label", `${running} processes running, ${unknownProcesses} unknown`);
  $("ring-on").setAttribute("stroke-dasharray", `${onShare} ${100 - onShare}`);
  $("ring-unknown").setAttribute("stroke-dasharray", `${unknownShare} ${100 - unknownShare}`);
  $("ring-unknown").setAttribute("stroke-dashoffset", -onShare);
  $("count").textContent = runners.length;
  $("host").textContent = snapshot.host;
  $("root").textContent = snapshot.roots?.length ? snapshot.roots.join("\n") : snapshot.root || "Not configured";
  const query = $("search").value.trim().toLowerCase();
  const selectedStatus = $("status-filter").value;
  const grouping = $("group-by").value;
  const visible = runners.filter(r => [r.name, r.displayName, r.folder, r.repository, r.organization, r.group, r.host, ...(r.gitHub.labels || [])].join(" ").toLowerCase().includes(query) &&
    (selectedStatus === "all" || r.status === selectedStatus));
  const groupName = r => grouping === "none" ? "" : r[grouping] || "Ungrouped";
  visible.sort((a, b) => groupName(a).localeCompare(groupName(b)) || a.displayName.localeCompare(b.displayName) || a.path.localeCompare(b.path));
  $("visible-count").textContent = `Showing ${visible.length} of ${runners.length} ${runners.length === 1 ? "runner" : "runners"}`;
  // Preserve keyboard focus through periodic refreshes.
  const focusPath = document.activeElement?.dataset.runnerPath;
  $("runners").replaceChildren();
  let previousGroup = null;
  for (const [index, runner] of visible.entries()) {
    if (grouping !== "none" && previousGroup !== groupName(runner)) {
      previousGroup = groupName(runner);
      const groupRow = element("tr", "group-row");
      const cell = element("th", "", `${previousGroup} · ${visible.filter(r => groupName(r) === previousGroup).length}`);
      cell.colSpan = 4; cell.scope = "colgroup";
      groupRow.append(cell); $("runners").append(groupRow);
    }
    const row = element("tr", "runner-row");
    const nameCell = element("td");
    const toggle = element("button", "runner-toggle");
    toggle.type = "button"; toggle.dataset.runnerPath = runner.path;
    const isOpen = expanded.has(runner.path);
    toggle.setAttribute("aria-expanded", String(isOpen));
    toggle.setAttribute("aria-controls", `runner-detail-${index}`);
    const arrow = element("span", "runner-chevron", isOpen ? "▾" : "▸");
    arrow.setAttribute("aria-hidden", "true");
    const title = element("span", "runner-label", runner.displayName || runner.name);
    title.append(element("span", "runner-subtitle", runner.name !== runner.displayName ? runner.name : runner.folder));
    toggle.append(arrow, title);
    toggle.addEventListener("click", () => {
      if (expanded.has(runner.path)) expanded.delete(runner.path); else expanded.add(runner.path);
      render();
    });
    nameCell.append(toggle);
    const target = element("td", "target-column");
    target.append(element("span", "runner-target", runner.repository || runner.organization || "Unknown project"));
    target.append(element("span", "runner-subtitle", [runner.operatingSystem, runner.architecture, runner.version].filter(Boolean).join(" · ") || "Platform unavailable"));
    const activity = element("td");
    activity.append(badge(runner.status, labels[runner.status] || "Unknown"));
    const connection = element("td");
    const connectionBadge = badge(runner.gitHub.status, githubLabels[runner.gitHub.status] || "Unknown");
    connectionBadge.title = runner.gitHub.message || `GitHub checked ${dateTime(runner.gitHub.checkedAt)}`;
    connection.append(connectionBadge);
    row.append(nameCell, target, activity, connection);
    $("runners").append(row);
    const detailRow = element("tr", "detail-row");
    detailRow.id = `runner-detail-${index}`; detailRow.hidden = !isOpen;
    const detailCell = element("td"); detailCell.colSpan = 4;
    if (isOpen) detailCell.append(runnerDetails(runner));
    detailRow.append(detailCell); $("runners").append(detailRow);
    if (focusPath === runner.path) toggle.focus({ preventScroll: true });
  }
  $("empty").hidden = visible.length > 0;
  $("empty-title").textContent = runners.length ? "No matching runners" : snapshot.error ? "Waiting for your runner folder" : "No runners found";
  $("empty-description").textContent = runners.length ? "Try another search or choose a different status." : "Check the watched directories and make sure they contain your runner installations.";
  $("updated").textContent = new Date(snapshot.checkedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}
async function refresh() {
  if (loading) return;
  loading = true;
  $("refresh").disabled = true;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 12000);
  try {
    const response = await fetch("/api/runners", { cache: "no-store", signal: controller.signal });
    if (!response.ok) throw new Error("Request failed");
    const data = await response.json();
    if (!Array.isArray(data.runners) || !Number.isFinite(Date.parse(data.checkedAt))) throw new Error("Invalid response");
    // A failed scan must not replace the previous runner list with an empty list.
    if (data.error && snapshot && !snapshot.error) throw new Error(data.error);
    snapshot = data;
    render();
    $("message").textContent = data.error || data.warning || "";
    $("message").hidden = !$("message").textContent;
    $("connection").textContent = data.error ? "Folder needs attention" : "Updates every 15 seconds";
    $("connection-dot").className = `dot ${data.error ? "unknown" : "on"}`;
  } catch {
    $("message").textContent = snapshot ? "Could not refresh. Showing the last available status; it may be out of date. Retrying automatically." : "Cannot reach the dashboard. Check that the application is running. Retrying automatically.";
    $("message").hidden = false;
    $("connection").textContent = "Updates unavailable";
    $("connection-dot").className = "dot unknown";
  } finally {
    clearTimeout(timeout);
    $("refresh").disabled = false;
    loading = false;
  }
}
$("search").addEventListener("input", () => { if (snapshot) render(); });
$("status-filter").addEventListener("change", () => { if (snapshot) render(); });
$("group-by").addEventListener("change", () => { if (snapshot) render(); });
$("refresh").addEventListener("click", refresh);
refresh();
setInterval(refresh, 15000);
