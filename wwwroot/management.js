"use strict";
(() => {
  let data, editing = null, loading = false;
  const selected = new Set();
  const field = name => document.getElementById("mg-" + name);
  const view = name => document.getElementById("management-" + name);
  function message(text) { view("message").textContent = text; view("message").hidden = !text; }
  function button(text, action) { const b = element("button", "secondary-button", text); b.type = "button"; b.addEventListener("click", action); return b; }
  async function send(path, body, confirmText) {
    if (confirmText && !window.confirm(confirmText)) return;
    try {
      const response = await fetch("/api/management/" + path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || "The operation could not be queued.");
      message("Operation queued. Follow its progress below; completed changes are retained if a batch stops partway.");
      await refresh(false);
    } catch (error) { message(error.message || "Management is unavailable."); }
  }
  function settings() {
    return { name: field("name").value.trim(), scope: field("scope").value, labels: field("labels").value.split(",").map(v => v.trim()).filter(Boolean),
      mode: field("mode").value, workFolder: field("work").value.trim(), pool: field("pool").value.trim(), gitHubGroup: field("group").value.trim() || null,
      ephemeral: field("ephemeral").checked, disableUpdate: field("no-update").checked, restoreOnRestart: field("restore").checked,
      retryLimit: Number(field("retries").value), retryDelaySeconds: Number(field("delay").value) };
  }
  function edit(runner) {
    editing = runner?.id || null;
    const s = runner?.settings || { name: "", scope: data?.scopes[0] || "", labels: [], mode: "process", workFolder: "_work", pool: "default", restoreOnRestart: true, retryLimit: 3, retryDelaySeconds: 30 };
    for (const [id, value] of Object.entries({ name: s.name, scope: s.scope, labels: s.labels.join(", "), mode: s.mode, work: s.workFolder, pool: s.pool, group: s.gitHubGroup || "", retries: s.retryLimit, delay: s.retryDelaySeconds })) field(id).value = value;
    field("restore").checked = s.restoreOnRestart; field("ephemeral").checked = !!s.ephemeral; field("no-update").checked = !!s.disableUpdate;
    for (const id of ["name", "scope", "count", "start"]) field(id).disabled = !!editing;
    field("mode").disabled = !!runner?.imported;
    field("save").textContent = editing ? "Save settings" : "Create runners";
    view("editor-title").textContent = editing ? "Settings · " + s.name : "Create runners / pool template";
    view("editor").open = true;
    view("editor").scrollIntoView({ block: "nearest", behavior: "smooth" });
  }
  function render() {
    view("content").hidden = !data.enabled;
    if (!data.enabled) return;
    view("summary").replaceChildren(...[
      ["MANAGED RUNNERS", data.runners.length, "Registered or imported on this server"],
      ["RUNNING", data.runners.filter(r => r.state === "running").length, "Local lifecycle state"],
      ["OPERATIONS", data.operations.filter(o => ["running", "queued"].includes(o.state)).length, "Queued or in progress"]
    ].map(([title, value, hint]) => { const card = element("div", "monitor-card"); card.append(element("span", "field-label", title), element("div", "monitor-value", String(value)), element("p", "detail-hint", hint)); return card; }));
    const priorScope = field("scope").value;
    field("scope").replaceChildren(...data.scopes.map(s => { const o = document.createElement("option"); o.value = s; o.textContent = s; return o; }));
    if (data.scopes.includes(priorScope)) field("scope").value = priorScope;
    view("latest").textContent = data.latest ? "Latest checked: " + data.latest.version + " · " + data.latest.architecture : "Version not checked";
    for (const id of selected) if (!data.runners.some(r => r.id === id)) selected.delete(id);
    const rows = data.runners.map(r => {
      const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.checked = selected.has(r.id); checkbox.setAttribute("aria-label", "Select " + r.settings.name);
      checkbox.addEventListener("change", () => { if (checkbox.checked) selected.add(r.id); else selected.delete(r.id); updateSelection(); });
      const name = element("div", "management-runner-name"); name.append(element("strong", "", r.settings.name), element("small", "detail-hint", r.settings.scope), element("small", "detail-hint", r.settings.mode + (r.imported ? " · imported" : "")));
      const state = element("div", ""); state.append(element("span", "management-state " + r.state, r.state.replaceAll("_", " ")));
      if (r.error) state.append(element("p", "management-error", r.error));
      if (r.nextRetry) state.append(element("small", "detail-hint", "Retry: " + dateTime(r.nextRetry)));
      return [checkbox, name, state, r.settings.pool, (r.version || "Unknown") + (data.latest && r.version && r.version !== data.latest.version ? " · check update" : ""), r.retries + " / " + r.settings.retryLimit, button("Settings", () => edit(r))];
    });
    view("runners").replaceChildren(rows.length ? monitorTable(["Select", "Runner", "Lifecycle", "Pool", "Version", "Retries", "Settings"], rows) : element("p", "detail-hint", "No managed runners yet. Create a runner or import an existing installation."));
    updateSelection();
    view("pools").replaceChildren(...data.pools.map(p => {
      const card = element("div", "management-pool"); const label = element("label", "", p.name + " · " + data.runners.filter(r => r.settings.pool === p.name).length + " runners");
      const count = document.createElement("input"); count.type = "number"; count.min = "0"; count.max = "500"; count.value = data.runners.filter(r => r.settings.pool === p.name).length; count.setAttribute("aria-label", "Desired runner count for " + p.name); label.append(count);
      card.append(label, element("span", "detail-hint", p.template.scope), button("Set count", () => send("scale", { name: p.name, count: Number(count.value) }, "Set pool " + p.name + " to " + count.value + " runners? Scaling down drains, unregisters and removes members."))); return card;
    }));
    if (!data.pools.length) view("pools").append(element("p", "detail-hint", "No pool templates saved."));
    view("workflows").replaceChildren(monitorTable(["Repository / run", "Attempt", "Status", "Result", "Last checked"], data.workflows.map(w => {
      const link = element("a", "", w.repository + " #" + w.runId); link.href = "https://github.com/" + w.repository + "/actions/runs/" + w.runId; link.target = "_blank"; link.rel = "noopener noreferrer";
      return [link, w.attempt, w.status, w.conclusion || "Pending", dateTime(w.checkedAt)];
    }), "No workflow runs tracked yet."));
    view("operations").replaceChildren(monitorTable(["Requested", "Operation", "State", "Details", "Control"], [...data.operations].reverse().slice(0, 100).map(o => [dateTime(o.createdAt), o.action.replaceAll("_", " "), o.state, o.message || o.target,
      ["queued", "running"].includes(o.state) ? button("Cancel operation", () => send("cancel", { id: o.id }, "Interrupt this operation? Completed changes will be kept. Running jobs will not be cancelled by this button.")) : "—"]), "No operations requested yet."));
  }
  function updateSelection() { field("apply").disabled = selected.size === 0; field("select-all").checked = !!data?.runners.length && selected.size === data.runners.length; field("select-all").indeterminate = selected.size > 0 && selected.size < (data?.runners.length || 0); }
  async function refresh(resetMessage = true) {
    if (loading) return; loading = true;
    try {
      const access = await window.accessReady;
      if (!access.canAdmin) { message("Runner management is available to administrators only."); view("content").hidden = true; return; }
      const response = await fetch("/api/management/", { cache: "no-store" });
      if (!response.ok) throw new Error("Could not load management state.");
      data = await response.json();
      if (resetMessage) message(data.warning || (!data.enabled ? "Management is disabled. Enable Access and configure Management on the server to create or control runners." : data.demo ? "Demo mode · Operations are simulated. No runners, files or GitHub resources are changed." : ""));
      render();
      const result = await fetch("/api/management/groups/result", { cache: "no-store" });
      if (result.ok) { const {result: groups} = await result.json(); if (groups?.runner_groups) view("groups").replaceChildren(monitorTable(["ID", "Name", "Visibility", "Public repositories"], groups.runner_groups.map(g => [g.id, g.name, g.visibility, g.allows_public_repositories ? "Allowed" : "Blocked"])), element("p", "detail-hint", "Showing up to 100 groups.")); else if (groups) view("groups").textContent = groups.message || (groups.id ? "Group " + groups.id + " · " + groups.name : "Group action completed."); }
    } catch (error) { message(error.message + " Previously displayed information may be stale."); }
    finally { loading = false; }
  }
  view("refresh").addEventListener("click", () => refresh());
  view("new").addEventListener("click", () => edit(null));
  field("reset").addEventListener("click", () => edit(null));
  view("version").addEventListener("click", () => send("version", {}));
  view("form").addEventListener("submit", event => { event.preventDefault(); const s = settings(); if (editing) send("configure", { id: editing, settings: s }, "Apply these settings? Registration changes drain and re-register the runner, leaving it stopped."); else send("create", { settings: s, count: Number(field("count").value), start: field("start").checked }); });
  field("save-pool").addEventListener("click", () => { if (view("form").reportValidity()) { const s = settings(); send("pool", { name: s.pool, template: s }); } });
  field("select-all").addEventListener("change", () => { selected.clear(); if (field("select-all").checked) for (const r of data.runners) selected.add(r.id); render(); });
  field("apply").addEventListener("click", () => { const action = field("action").value; send("action", { ids: [...selected], action }, action === "start" ? null : "Apply " + action + " to " + selected.size + " runner(s)? Maintenance actions wait for idle. Unregister/remove also delete GitHub registrations."); });
  view("import-open").addEventListener("click", async () => {
    view("import").open = true;
    try { const response = await fetch("/api/management/imports"); if (!response.ok) throw new Error(); const runners = await response.json(); field("import-runner").replaceChildren(...runners.filter(r => !data.runners.some(m => m.path === r.path)).map(r => { const o = document.createElement("option"); o.value = r.id; o.textContent = r.displayName + " · " + r.path; return o; })); if (!field("import-runner").options.length) message("No unmanaged installations were found in the discovery roots."); }
    catch { message("Could not load discovered installations."); }
  });
  view("import-form").addEventListener("submit", event => { event.preventDefault(); send("import", { runnerId: field("import-runner").value, mode: field("import-mode").value }); });
  view("workflow-form").addEventListener("submit", event => { event.preventDefault(); const action = field("workflow-action").value; send("workflow", { repository: field("workflow-repo").value.trim(), runId: Number(field("run-id").value), action }, action === "track" ? null : "Request " + action + " for this GitHub workflow run? This affects the real workflow when management is enabled."); });
  view("group-form").addEventListener("submit", event => {
    event.preventDefault(); const action = field("group-action").value; const ids = id => field(id).value.split(",").map(v => v.trim()).filter(Boolean).map(Number);
    send("group", { organization: field("org").value.trim(), action, id: Number(field("group-id").value) || null, name: field("group-name").value.trim(), visibility: field("visibility").value, allowsPublicRepositories: field("public").checked, repositoryIds: ids("repository-ids"), runnerIds: ids("runner-ids") }, action === "list" ? null : "Apply " + action + " to this GitHub organization group? Membership and repository access changes affect scheduling.");
  });
  refresh(); setInterval(() => { if (!document.getElementById("workspace-management").hidden) refresh(); }, 15000);
})();
