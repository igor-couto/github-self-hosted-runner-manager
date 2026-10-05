"use strict";
let logRunner = null;
let logData = null;
let logPaused = false;
let logRequest = null;
let logOpener = null;

function openRunnerLogs(runner) {
  logRunner = runner; logData = null; logPaused = false;
  logOpener = document.activeElement;
  $("log-title").textContent = `${runner.displayName || runner.name} · Logs`;
  $("log-file").replaceChildren(new Option("Follow current / latest", ""));
  $("log-search").value = ""; $("log-level").value = "all"; $("log-follow").checked = true;
  $("log-pause").textContent = "Pause updates"; $("log-pause").setAttribute("aria-pressed", "false");
  $("log-output").replaceChildren(); $("log-note").textContent = "";
  $("log-dialog").showModal();
  fetchRunnerLogs();
}
async function fetchRunnerLogs() {
  if (!$("log-dialog").open || !logRunner) return;
  logRequest?.abort();
  const controller = new AbortController(); logRequest = controller;
  const timer = setTimeout(() => controller.abort(), 12000);
  $("log-refresh").disabled = true; $("log-status").textContent = "Reading logs…";
  try {
    const file = $("log-file").value;
    const query = file ? `?file=${encodeURIComponent(file)}` : "";
    const response = await fetch(`/api/runners/${encodeURIComponent(logRunner.id)}/logs${query}`, {cache:"no-store", signal:controller.signal});
    if (!response.ok) throw new Error("Unavailable");
    const data = await response.json();
    if (logRequest !== controller) return;
    logData = data;
    $("log-file").replaceChildren(new Option("Follow current / latest", ""), ...data.files.map(f => new Option(`${f.kind} · ${f.name}`, f.name)));
    // Preserve a rotated-out selection so the viewer doesn't silently switch to a different job.
    if (file && !data.files.some(f => f.name === file)) $("log-file").append(new Option(`${file} (unavailable)`, file));
    $("log-file").value = file;
    renderLogLines();
  } catch {
    if (logRequest !== controller) return;
    logData = null; $("log-output").replaceChildren(); $("log-download").disabled = true;
    $("log-status").textContent = "Could not read logs. The runner may have disappeared or the server is unavailable. Retry with Refresh logs.";
    $("log-note").textContent = "";
  } finally {
    clearTimeout(timer);
    if (logRequest === controller) { logRequest = null; $("log-refresh").disabled = false; }
  }
}
function visibleLogLines() {
  const query = $("log-search").value.toLowerCase();
  const level = $("log-level").value;
  return (logData?.excerpt.lines || []).filter(line => (level === "all" || (level === "warnings" ? ["WARN", "ERR"].includes(line.level) : line.level === level)) &&
    `${line.at} ${line.level} ${line.source} ${line.message}`.toLowerCase().includes(query));
}
function renderLogLines() {
  if (!logData) return;
  const output = $("log-output");
  const scroll = output.scrollTop;
  const lines = visibleLogLines();
  output.replaceChildren();
  for (const line of lines) {
    const row = element("div", `log-line log-${line.level.toLowerCase()}`);
    row.append(element("span", "log-time", new Date(line.at).toLocaleTimeString()), element("span", "log-level", line.level),
      element("span", "log-text", `${line.source}: ${line.message}`));
    output.append(row);
  }
  if (!lines.length) output.append(element("p", "log-empty", logData.excerpt.lines.length ? "No lines match your filters." : "No readable log lines in this excerpt."));
  output.scrollTop = $("log-follow").checked ? output.scrollHeight : scroll;
  $("log-download").disabled = !lines.length;
  $("log-status").textContent = `${logPaused ? "Paused" : "Updates every 15 seconds"} · ${lines.length} of ${logData.excerpt.lines.length} lines · Checked ${dateTime(logData.checkedAt)}${logData.excerpt.truncated ? " · Recent excerpt only" : ""}${logData.excerpt.file ? ` · ${logData.excerpt.file}` : ""}`;
  $("log-note").textContent = logData.excerpt.message || "";
}
$("log-close").addEventListener("click", () => $("log-dialog").close());
$("log-dialog").addEventListener("close", () => {
  logRequest?.abort(); logRequest = null; logRunner = null; logData = null;
  if (logOpener?.isConnected) logOpener.focus();
  else $("refresh").focus();
});
$("log-file").addEventListener("change", fetchRunnerLogs);
$("log-search").addEventListener("input", renderLogLines);
$("log-level").addEventListener("change", renderLogLines);
$("log-refresh").addEventListener("click", fetchRunnerLogs);
$("log-pause").addEventListener("click", () => {
  logPaused = !logPaused;
  $("log-pause").textContent = logPaused ? "Resume updates" : "Pause updates";
  $("log-pause").setAttribute("aria-pressed", String(logPaused));
  if (logPaused) { logRequest?.abort(); logRequest = null; $("log-refresh").disabled = false; renderLogLines(); }
  else fetchRunnerLogs();
});
$("log-download").addEventListener("click", () => {
  const lines = visibleLogLines();
  if (!lines.length) return;
  const text = lines.map(l => `[${l.at} ${l.level} ${l.source}] ${l.message}`).join("\n") + "\n";
  const url = URL.createObjectURL(new Blob([text], {type:"text/plain;charset=utf-8"}));
  const link = element("a"); link.href = url; link.download = `runner-room-${logData.excerpt.file || "diagnostics"}.txt`;
  document.body.append(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});
setInterval(() => { if ($("log-dialog").open && !logPaused) fetchRunnerLogs(); }, 15000);
