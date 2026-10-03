"use strict";
const $ = id => document.getElementById(id);
let snapshot = null;
let loading = false;
const labels = { on: "On", off: "Off", unknown: "Unknown" };

function render() {
  const runners = snapshot.runners;
  $("demo").hidden = !snapshot.demo;
  $("total").textContent = runners.length;
  $("on").textContent = runners.filter(r => r.status === "on").length;
  $("off").textContent = runners.filter(r => r.status === "off").length;
  $("count").textContent = runners.length;
  $("host").textContent = snapshot.host;
  $("root").textContent = snapshot.root || "Not configured";
  const query = $("search").value.trim().toLowerCase();
  const visible = runners.filter(r => `${r.name} ${r.folder}`.toLowerCase().includes(query));
  $("runners").replaceChildren();
  for (const runner of visible) {
    const row = document.createElement("tr");
    const nameCell = document.createElement("td");
    const name = document.createElement("span");
    name.className = "runner-name";
    const icon = document.createElement("span");
    icon.className = "runner-icon";
    icon.setAttribute("aria-hidden", "true");
    icon.textContent = ">_";
    const title = document.createElement("span");
    title.textContent = runner.name;
    name.append(icon, title);
    nameCell.append(name);
    const folder = document.createElement("td");
    folder.className = "runner-folder";
    folder.textContent = runner.folder;
    const status = Object.hasOwn(labels, runner.status) ? runner.status : "unknown";
    const state = document.createElement("td");
    const badge = document.createElement("span");
    badge.className = `badge ${status}`;
    const dot = document.createElement("i");
    dot.className = `dot ${status}`;
    dot.setAttribute("aria-hidden", "true");
    badge.append(dot, document.createTextNode(labels[status]));
    state.append(badge);
    row.append(nameCell, folder, state);
    $("runners").append(row);
  }
  $("empty").hidden = visible.length > 0;
  $("empty-title").textContent = runners.length ? "No matching runners" : snapshot.error ? "Waiting for your runner folder" : "No runners found";
  $("empty-description").textContent = runners.length ? "Try a different name or folder." : "Point RunnersRoot at the parent folder containing your runner installations. Each runner should have a .runner file or bin/Runner.Listener.";
  $("updated").textContent = `Checked ${new Date(snapshot.checkedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
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
$("refresh").addEventListener("click", refresh);
refresh();
setInterval(refresh, 5000);
