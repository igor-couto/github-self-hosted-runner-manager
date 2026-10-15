"use strict";
(() => {
  const view = name => document.getElementById("sync-" + name);
  let data, access, loading = false, acting = false, discovering = false, page = 0, detailKey = null;
  let candidates = [], selection = new Set(), lastRuns = "", lastRepositories = "", selectionDirty = false;
  const pageSize = 15;
  const canOperate = () => !!(access?.canAdmin || access?.canOperate);
  const followed = () => (data?.repositories || []).filter(r => r.selected);
  const key = run => `${run.repository}/${run.id}/${run.attempt}`;
  const statusNames = { success:"Passed", failure:"Failed", cancelled:"Cancelled", timed_out:"Timed out", action_required:"Action required", in_progress:"In progress", queued:"Queued", waiting:"Waiting", pending:"Pending", requested:"Requested", completed:"Completed", skipped:"Skipped", neutral:"Neutral", stale:"Stale" };
  const date = value => value ? dateTime(value) : "Not yet";
  function message(text = "", error = false) { view("message").textContent = text; view("message").hidden = !text; view("message").classList.toggle("error", error); }
  function button(text, action, disabled = false) { const b = element("button", "", text); b.type = "button"; b.disabled = disabled; b.addEventListener("click", action); return b; }
  function status(run) { const value = run.conclusion || run.status || "unknown"; return element("span", "sync-status " + (Object.hasOwn(statusNames, value) ? value : "unknown"), statusNames[value] || "Unknown"); }
  function link(text, repository, suffix = "") {
    if (!/^[a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.-]+$/.test(repository || "")) return element("span", "", text);
    const a = element("a", "", text); a.href = "https://github.com/" + repository + suffix; a.target = "_blank"; a.rel = "noopener noreferrer"; return a;
  }
  async function request(path, method = "GET", body) {
    const response = await fetch("/api/github/" + path, { method, cache:"no-store", ...(body === undefined ? {} : { headers:{"Content-Type":"application/json"}, body:JSON.stringify(body) }) });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.message || (response.status === 403 ? "Your account does not have permission for this action." : "GitHub synchronization is unavailable. Try again shortly."));
    return result;
  }
  function controls() {
    const ready = data?.enabled && data?.configured;
    view("choose").hidden = !access?.canAdmin; view("choose").disabled = !ready || acting;
    view("now").hidden = !canOperate(); view("now").disabled = !ready || !followed().length || !!data?.progress?.running || acting;
    view("cancel").hidden = !canOperate() || !data?.progress?.running; view("cancel").disabled = acting;
    view("save").disabled = acting || discovering; view("discover").disabled = discovering || acting;
    view("setup-admin").hidden = !access?.canAdmin;
  }
  function renderRepositories(force = false) {
    const signature = JSON.stringify([followed(), canOperate(), acting, data?.progress?.running]);
    if (!force && signature === lastRepositories) return;
    lastRepositories = signature;
    view("repositories").replaceChildren(...followed().map(repo => {
      const row = element("div", "sync-repository"), description = element("div");
      description.append(link(repo.fullName, repo.fullName), element("p", "detail-hint", "Last synced: " + date(repo.lastSyncedAt) + (repo.partial ? " · Partial import" : "")));
      if (repo.error) description.append(element("p", "sync-repository-error", repo.error));
      row.append(description);
      if (canOperate()) { const actions = element("div", "sync-actions"); actions.append(button(repo.error ? "Retry sync" : "Sync", () => synchronize({repository:repo.fullName}), acting || data?.progress?.running || !data?.configured), button("Runner requirements", () => scanWorkflows(repo.fullName), acting || !data?.configured)); row.append(actions); }
      return row;
    }));
    if (!followed().length) view("repositories").append(element("p", "sync-empty", access?.canAdmin ? "No repositories followed yet. Choose repositories to start collecting workflow history." : "No repositories followed yet. Ask an administrator to choose which repositories to synchronize."));
  }
  function render() {
    view("content").hidden = false; view("demo").hidden = !data.demo;
    const connected = data.enabled && data.configured;
    view("setup").hidden = connected;
    view("summary").replaceChildren(
      metricCard("GitHub connection", !data.enabled ? "Sync disabled" : data.configured ? "Credential configured" : "Setup needed", !data.enabled ? "Enable synchronization in server settings." : "Connection is verified when repositories are discovered or synchronized."),
      metricCard("Repositories followed", String(followed().length), "Visible to dashboard users"),
      metricCard("Sync status", data.progress?.running ? "Syncing…" : !data.enabled ? "Disabled" : !data.configured ? "Awaiting connection" : "Ready", data.progress?.running ? data.progress.repository || "Preparing import" : data.nextSyncAt ? "Next check: " + date(data.nextSyncAt) : "Saved history stays available between checks.")
    );
    view("progress").hidden = !data.progress?.running;
    if (data.progress?.running) view("progress").textContent = `Syncing ${data.progress.repository || "repositories"} · ${data.progress.completedRepositories || 0} of ${data.progress.totalRepositories || followed().length} repositories complete. You can keep using the dashboard.`;
    controls(); renderRepositories();
    const repoFilter = view("run-repo").value;
    const names = followed().map(r => r.fullName).sort();
    if (JSON.stringify(names) !== view("run-repo").dataset.names) {
      const all = element("option", "", "All repositories"); all.value = "";
      view("run-repo").replaceChildren(all, ...names.map(name => { const o = element("option", "", name); o.value = name; return o; }));
      if (names.includes(repoFilter)) view("run-repo").value = repoFilter;
      view("run-repo").dataset.names = JSON.stringify(names);
    }
    renderRuns();
    const api = data.api;
    view("budget").textContent = api?.limit != null ? `${api.remaining ?? "Unknown"} of ${api.limit} API requests remaining · resets ${date(api.resetsAt)}${api.retryAt ? " · Requests paused until " + date(api.retryAt) : ""}.` : "API usage will appear after a request to GitHub.";
    view("history").replaceChildren(monitorTable(["When", "Repository", "Result", "Runs", "Details"], [...(data.history || [])].reverse().slice(0, 50).map(h => [date(h.at), h.repository || "All repositories", h.outcome, h.runsImported ?? "—", h.message || "—"]), "No synchronization activity yet."));
    if (detailKey) renderDetails(false);
  }
  async function refresh(quiet = true) {
    if (loading) return; loading = true;
    view("refresh").disabled = true;
    try {
      access = await window.accessReady;
      if (access.enabled && !access.authenticated) return;
      data = await request("sync"); render();
      if (data.warning) message(data.warning, true);
      else if (!quiet || !data.progress?.running && view("message").textContent === "Loading GitHub connection…") message();
    } catch (error) { message(error.message + (data ? " Previously synchronized information is still shown." : " Use Refresh status to try again."), true); }
    finally { loading = false; view("refresh").disabled = false; }
  }
  function renderChoices() {
    const search = view("repo-search").value.trim().toLowerCase();
    view("choices").replaceChildren(...candidates.filter(r => r.fullName.toLowerCase().includes(search)).map(repo => {
      const label = element("label", "sync-choice"), checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.checked = selection.has(repo.fullName);
      checkbox.addEventListener("change", () => { if (checkbox.checked) selection.add(repo.fullName); else selection.delete(repo.fullName); selectionDirty = true; selectionCount(); });
      const name = element("span", "", repo.fullName); name.append(element("small", "", repo.isPrivate ? "Private" : "Public")); label.append(checkbox, name); return label;
    }));
    if (!view("choices").childElementCount) view("choices").append(element("p", "detail-hint", search ? "No matching repositories. Try another name." : "Find accessible repositories to see what this connection can access."));
    selectionCount();
  }
  function selectionCount() { view("selection-count").textContent = selection.size + " selected" + (selectionDirty ? " · Unsaved changes" : ""); }
  function openPicker() {
    if (view("picker").hidden) {
      selection = new Set(followed().map(r => r.fullName)); candidates = [...(data.repositories || [])]; selectionDirty = false;
      const days = String(data.historyDays || 30);
      if (![...view("days").options].some(o => o.value === days)) { const o = element("option", "", days + " days"); o.value = days; view("days").append(o); }
      view("days").value = days; view("limit").value = data.maxRunsPerRepository || 100;
      view("repo-search").value = ""; renderChoices(); view("picker").hidden = false;
    }
    view("repo-search").focus(); view("picker").scrollIntoView({block:"nearest"});
  }
  async function discover() {
    if (discovering) return; discovering = true; controls(); view("discover").textContent = "Finding repositories…";
    view("discovery-message").textContent = "Checking the repositories this server connection can access…";
    try {
      const result = await request("discover", "POST", {});
      const byName = new Map(candidates.map(r => [r.fullName, r])); for (const repo of result.repositories || []) byName.set(repo.fullName, repo);
      candidates = [...byName.values()].sort((a, b) => a.fullName.localeCompare(b.fullName)); renderChoices();
      view("discovery-message").textContent = result.warning || (result.partial ? "Some repositories may not be listed. Discovery reached its request limit; previously followed repositories are kept." : `${(result.repositories || []).length} repositories found. Select the ones to follow, then save.`);
    } catch (error) { view("discovery-message").textContent = error.message + " Your selection has been kept."; }
    finally { discovering = false; controls(); view("discover").textContent = "Find accessible repositories"; }
  }
  async function mutate(path, method, body, success) {
    if (acting) return false; acting = true; controls();
    try { await request(path, method, body); message(success); await refresh(); return true; }
    catch (error) { message(error.message, true); return false; }
    finally { acting = false; controls(); renderRepositories(true); }
  }
  function synchronize(body = {}) { return mutate("sync", "POST", body, "Sync queued. Progress and new workflow results will appear here automatically."); }
  async function saveSelection() {
    if (!view("limit").reportValidity()) return;
    if (await mutate("selection", "PUT", {repositories:[...selection], historyDays:Number(view("days").value), maxRunsPerRepository:Number(view("limit").value)}, "Repository selection saved. Choose Sync now to import workflow history.")) {
      selectionDirty = false; view("picker").hidden = true; view("choose").focus();
    }
  }
  function renderRuns() {
    const search = view("run-search").value.trim().toLowerCase(), repository = view("run-repo").value, result = view("run-result").value;
    const matches = (data?.runs || []).filter(r => (!repository || r.repository === repository) && (!search || [r.name, r.branch, r.actor, r.commit, r.repository, String(r.id)].some(s => String(s || "").toLowerCase().includes(search))) && (!result || (result === "active" ? r.status !== "completed" && !r.conclusion : result === "other" ? r.status === "completed" && !["success", "failure", "cancelled"].includes(r.conclusion) : r.conclusion === result))).sort((a,b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
    const pages = Math.max(1, Math.ceil(matches.length / pageSize)); page = Math.max(0, Math.min(page, pages - 1));
    view("run-count").textContent = matches.length + " runs"; view("page").textContent = `${page + 1} / ${pages}`; view("previous").disabled = !page; view("next").disabled = page >= pages - 1;
    const rows = matches.slice(page * pageSize, (page + 1) * pageSize), signature = JSON.stringify([rows, page, matches.length, result, repository, search]);
    if (signature === lastRuns) return; lastRuns = signature;
    view("runs-list").replaceChildren(monitorTable(["Workflow", "Repository / branch", "Result", "Started", "Details"], rows.map(run => {
      const name = element("div"); name.append(element("strong", "sync-run-name", run.name || "Workflow run"), element("small", "detail-hint", `#${run.id} · attempt ${run.attempt || 1}`));
      const scope = element("div"); scope.append(link(run.repository, run.repository), element("small", "detail-hint", run.branch || "No branch"));
      return [name, scope, status(run), date(run.startedAt || run.createdAt), button("View jobs", () => { detailKey = key(run); renderDetails(true); })];
    }), data?.runs?.length ? "No workflow runs match these filters." : followed().length ? "No workflow runs imported yet. Sync your repositories to bring their history here." : "Workflow history will appear after repositories are selected and synchronized."));
  }
  function renderDetails(focus) {
    const run = data.runs.find(r => key(r) === detailKey);
    if (!run) { view("run-details").hidden = true; detailKey = null; return; }
    const signature = JSON.stringify([run, acting, data.progress?.running]);
    if (focus || signature !== view("run-body").dataset.signature) {
      view("run-title").textContent = (run.name || "Workflow") + " · #" + run.id;
      const summary = element("div", "sync-actions"); summary.append(status(run), link("Open in GitHub ↗", run.repository, "/actions/runs/" + encodeURIComponent(run.id)));
      if (canOperate()) summary.append(button("Refresh this run", () => synchronize({repository:run.repository, runId:run.id}), acting || data.progress?.running));
      view("run-body").replaceChildren(summary, element("p", "detail-hint", `${run.repository} · ${run.branch || "No branch"} · ${run.actor || "Unknown actor"} · ${run.event || "Unknown event"} · attempt ${run.attempt || 1}`));
      if (run.commit) view("run-body").append(link("Commit " + run.commit.slice(0, 8), run.repository, "/commit/" + encodeURIComponent(run.commit)));
      if (run.partial) view("run-body").append(element("p", "sync-message", "Some jobs or steps could not be imported. Refresh this run to try again; previously imported details are kept."));
      if (!(run.jobs || []).length) view("run-body").append(element("p", "detail-hint", "No job details imported yet. Refresh this run to check again."));
      for (const job of run.jobs || []) {
        const section = element("section", "sync-job"), heading = element("div", "sync-heading"); heading.append(element("h4", "", job.name), status(job)); section.append(heading);
        section.append(element("p", "detail-hint", `${job.runnerName || "Runner not assigned"} · Started ${date(job.startedAt)} · Finished ${date(job.completedAt)}`));
        if (job.labels?.length) section.append(element("p", "detail-hint", "Labels: " + job.labels.join(", ")));
        const steps = element("details"), title = element("summary", "", `Steps (${(job.steps || []).length})`); steps.append(title, monitorTable(["Step", "Result", "Started", "Finished"], (job.steps || []).map(step => [step.name, status(step), date(step.startedAt), date(step.completedAt)]), "No step details available yet.")); section.append(steps); view("run-body").append(section);
      }
      view("run-body").dataset.signature = signature;
    }
    view("run-details").hidden = false;
    if (focus) { view("run-details").focus(); view("run-details").scrollIntoView({block:"start", behavior:"smooth"}); }
  }
  async function scanWorkflows(repository) {
    view("workflow-details").hidden = false; view("workflow-title").textContent = "Runner requirements · " + repository;
    view("workflow-body").textContent = "Reading workflow requirements…"; view("workflow-details").focus();
    try {
      const result = await request("workflows?repository=" + encodeURIComponent(repository));
      // The server returns normalized, bounded requirements rather than executable workflow content.
      view("workflow-body").replaceChildren(monitorTable(["Workflow", "Job", "Requested runners", "Notes"], (result.workflows || []).flatMap(w => (w.jobs || []).map(j => [w.name || w.file, j.name || j.id, [...(j.labels || []), ...(j.group ? ["Group: " + j.group] : [])].join(", ") || "Unresolved", j.dynamic ? "Uses an expression; resolve in GitHub." : j.selfHosted ? "Self-hosted runner required" : "—"])), "No runner requirements found in the scanned workflow files."));
      for (const warning of result.warnings || []) view("workflow-body").prepend(element("p", "sync-message", warning));
    } catch (error) { view("workflow-body").textContent = error.message; }
  }
  view("refresh").addEventListener("click", () => refresh(false));
  view("choose").addEventListener("click", openPicker);
  view("picker-close").addEventListener("click", () => { view("picker").hidden = true; view("choose").focus(); });
  view("discover").addEventListener("click", discover); view("repo-search").addEventListener("input", renderChoices);
  for (const name of ["days", "limit"]) view(name).addEventListener("change", () => { selectionDirty = true; selectionCount(); });
  view("save").addEventListener("click", saveSelection); view("now").addEventListener("click", () => synchronize());
  view("cancel").addEventListener("click", () => mutate("cancel", "POST", {}, "Stopping synchronization. Already imported history is kept."));
  for (const name of ["run-search", "run-repo", "run-result"]) view(name).addEventListener(name === "run-search" ? "input" : "change", () => { page = 0; renderRuns(); });
  view("previous").addEventListener("click", () => { page--; renderRuns(); }); view("next").addEventListener("click", () => { page++; renderRuns(); });
  view("run-close").addEventListener("click", () => { detailKey = null; view("run-details").hidden = true; view("run-search").focus(); });
  view("workflow-close").addEventListener("click", () => { view("workflow-details").hidden = true; view("refresh").focus(); });
  $("refresh").addEventListener("click", () => refresh(false));
  refresh(false);
  setInterval(() => { if (!$("workspace-integration").hidden) refresh(); }, 15000);
  // Events invalidate the snapshot; polling remains available through reconnects and proxies.
  window.accessReady.then(session => {
    if (session.enabled && !session.authenticated || typeof EventSource === "undefined") return;
    const events = new EventSource("/api/github/events"); let timer;
    events.addEventListener("sync", () => { clearTimeout(timer); timer = setTimeout(() => refresh(), 400); });
    window.addEventListener("pagehide", () => { clearTimeout(timer); events.close(); }, {once:true});
  });
})();
