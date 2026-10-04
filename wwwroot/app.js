"use strict";
const $ = id => document.getElementById(id);
let snapshot = null;
let loading = false;
const labels = { on: "On", off: "Off", unknown: "Unknown" };

function render() {
  const runners = snapshot.runners;
  const onCount = runners.filter(r => r.status === "on").length;
  const offCount = runners.filter(r => r.status === "off").length;
  const unknownCount = runners.length - onCount - offCount;
  const onShare = runners.length ? onCount / runners.length * 100 : 0;
  const unknownShare = runners.length ? unknownCount / runners.length * 100 : 0;
  $("demo").hidden = !snapshot.demo;
  $("total").textContent = runners.length;
  $("on").textContent = onCount;
  $("off").textContent = offCount;
  $("legend-on").textContent = onCount;
  $("legend-off").textContent = offCount;
  $("legend-unknown").textContent = unknownCount;
  $("activity-percent").textContent = runners.length ? `${Math.round(onShare)}%` : "—";
  $("activity-caption").textContent = snapshot.error ? "Waiting for runner status" : runners.length ? `${onCount} of ${runners.length} runner processes active` : "No runners detected";
  $("activity-ring").setAttribute("aria-label", snapshot.error ? "Runner status unavailable" : `${onCount} runners on, ${offCount} off, ${unknownCount} unknown`);
  $("ring-on").setAttribute("stroke-dasharray", `${onShare} ${100 - onShare}`);
  $("ring-unknown").setAttribute("stroke-dasharray", `${unknownShare} ${100 - unknownShare}`);
  $("ring-unknown").setAttribute("stroke-dashoffset", -onShare);
  $("count").textContent = runners.length;
  $("host").textContent = snapshot.host;
  $("root").textContent = snapshot.root || "Not configured";
  const query = $("search").value.trim().toLowerCase();
  const selectedStatus = $("status-filter").value;
  const visible = runners.filter(r => `${r.name} ${r.folder}`.toLowerCase().includes(query) &&
    (selectedStatus === "all" || (Object.hasOwn(labels, r.status) ? r.status : "unknown") === selectedStatus));
  $("visible-count").textContent = `Showing ${visible.length} of ${runners.length} ${runners.length === 1 ? "runner" : "runners"}`;
  $("runners").replaceChildren();
  for (const runner of visible) {
    const row = document.createElement("tr");
    const nameCell = document.createElement("td");
    const name = document.createElement("span");
    name.className = "runner-name";
    const icon = document.createElement("span");
    icon.className = "runner-icon";
    icon.setAttribute("aria-hidden", "true");
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.classList.add("icon");
    const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", "#icon-terminal");
    svg.append(use);
    icon.append(svg);
    const title = document.createElement("span");
    title.className = "runner-label";
    title.textContent = runner.name;
    // Keep directories visible on small screens when registered names repeat.
    const mobileFolder = document.createElement("span");
    mobileFolder.className = "runner-folder-mobile";
    mobileFolder.textContent = runner.folder;
    title.append(mobileFolder);
    name.append(icon, title);
    nameCell.append(name);
    const folder = document.createElement("td");
    folder.className = "runner-folder";
    folder.textContent = runner.folder;
    const pid = document.createElement("td");
    pid.className = "pid-column";
    pid.textContent = Number.isInteger(runner.pid) && runner.pid > 0 ? runner.pid : "—";
    const status = Object.hasOwn(labels, runner.status) ? runner.status : "unknown";
    const state = document.createElement("td");
    const badge = document.createElement("span");
    badge.className = `badge ${status}`;
    const dot = document.createElement("i");
    dot.className = `dot ${status}`;
    dot.setAttribute("aria-hidden", "true");
    badge.append(dot, document.createTextNode(labels[status]));
    state.append(badge);
    row.append(nameCell, folder, pid, state);
    $("runners").append(row);
  }
  $("empty").hidden = visible.length > 0;
  $("empty-title").textContent = runners.length ? "No matching runners" : snapshot.error ? "Waiting for your runner folder" : "No runners found";
  $("empty-description").textContent = runners.length ? "Try another search or choose a different status." : "Check the watched directory and make sure it contains your runner installations.";
  $("updated").textContent = new Date(snapshot.checkedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

async function refresh() {
  if (loading) return;
  loading = true;
  $("refresh").disabled = true;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 8000);
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
    $("connection").textContent = data.error ? "Folder needs attention" : "Updates every 5 seconds";
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
$("refresh").addEventListener("click", refresh);
refresh();
setInterval(refresh, 5000);
