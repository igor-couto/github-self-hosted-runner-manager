"use strict";
(() => {
  const byId = id => document.getElementById("setup-" + id);
  const dialog = document.createElement("dialog");
  dialog.id = "setup-dialog";
  dialog.setAttribute("aria-labelledby", "setup-title");
  // Static markup only. Repository names and server responses are rendered as text below.
  dialog.innerHTML = `<div class="panel-heading"><h2 id="setup-title">Set up runner management</h2><button id="setup-close" type="button" aria-label="Close management setup">Close ✕</button></div>
    <div class="setup-body"><p id="setup-progress" class="setup-progress"></p><p id="setup-error" role="alert" hidden></p>
    <section id="setup-verify" hidden><h3>Verify this is your server</h3><p>Before enabling controls, confirm that you own this server. Run this command once on the server, then paste the code below.</p>
    <pre id="setup-command">sudo -u "$(systemctl show runner-room.service --property=User --value)" /opt/runner-room/current/RunnerRoom --setup-code</pre>
    <p class="setup-hint">For an installer deployment with systemd. The code is temporary and works only for setup. Other deployments: run <code>RunnerRoom --setup-code</code> as the dashboard account with the same settings and state directory.</p>
    <label>Setup code<input id="setup-code" type="password" autocomplete="off" spellcheck="false" required></label></section>
    <section id="setup-account" hidden><h3>Your dashboard login</h3><p>Use this account to sign in and manage runners. Other people on your network will need to sign in too.</p>
    <label>Username<input id="setup-username" autocomplete="username" minlength="3" maxlength="64" pattern="[a-zA-Z0-9_.\\-]{3,64}" title="Use 3–64 letters, numbers, dots, underscores or hyphens." required></label>
    <label>Password<input id="setup-password" type="password" autocomplete="new-password" minlength="12" maxlength="1024" required></label><p class="setup-hint">Use at least 12 characters.</p>
    <label>Confirm password<input id="setup-confirm" type="password" autocomplete="new-password" required></label>
    <p>Dashboard address: <strong id="setup-origin"></strong></p>
    <div id="setup-http" hidden><p>You're using HTTP. Passwords and tokens sent here are not encrypted. If you have an HTTPS address, open it before continuing.</p><label class="setup-check"><input id="setup-allow-http" type="checkbox">I understand and want to continue over HTTP on my trusted network.</label></div></section>
    <section id="setup-github" hidden><h3>Connect GitHub</h3><p id="setup-token-hint">Create a fine-grained token for the repositories you want to manage. Give it <strong>Administration: read and write</strong> to register runners, and <strong>Actions: read and write</strong> for workflow controls.</p>
    <p id="setup-token-link"><a href="https://github.com/settings/personal-access-tokens/new" target="_blank" rel="noopener noreferrer">Create a GitHub token ↗</a></p>
    <label id="setup-token-label">GitHub management token<input id="setup-token" type="password" autocomplete="off" spellcheck="false"></label>
    <p id="setup-secret-hint" class="setup-hint">Stored securely on your server. The token is never saved in browser storage. This is separate from the read-only synchronization connection.</p>
    <button id="setup-discover" type="button">Find repositories</button><p id="setup-discovery-status" class="setup-hint" role="status"></p>
    <label id="setup-filter-label" hidden>Find a repository<input id="setup-filter" type="search" placeholder="Search repositories…"></label>
    <div id="setup-repositories"></div>
    <details><summary>Add a repository or organization manually</summary><p class="setup-hint">Enter one scope per line: <code>repo:owner/repository</code> or <code>org:organization</code>. Organization runners need organization-level self-hosted runner write permission. Only the listed scopes can be managed.</p><label>Additional scopes<textarea id="setup-scopes" rows="3" placeholder="repo:owner/repository" spellcheck="false"></textarea></label></details>
    <p id="setup-selected" class="setup-hint" role="status"></p></section>
    <section id="setup-review-step" hidden><h3>Ready to enable management</h3><dl id="setup-review"></dl><p>Existing runners keep running. After setup, you can create new runners or choose which existing installations to import.</p><p class="setup-hint">Existing system services may need a one-time permissions change on the server before they can be imported. This setup does not change or stop those services.</p></section>
    <section id="setup-done" hidden><h3 id="setup-done-title">Settings saved</h3><p id="setup-done-message"></p><div id="setup-restart"><p>Run this command on the server to apply the settings:</p><pre>sudo systemctl restart runner-room.service</pre><p>Then reload this page and sign in. For Docker or Kubernetes, restart the dashboard container or deployment instead.</p></div></section>
    <div class="setup-actions"><button id="setup-back" type="button" hidden>Back</button><button id="setup-next" class="primary-button" type="button">Continue</button></div></div>`;
  document.body.append(dialog);
  byId("verify").before(byId("http"));
  let status, config, step, returnAfterVerify = null, busy = false, generation = 0, setupCode = "", selection = new Set();
  const steps = ["verify", "account", "github", "review-step", "done"];
  const error = message => { byId("error").textContent = message || ""; byId("error").hidden = !message; };
  function show(next) {
    step = next;
    for (const name of steps) byId(name).hidden = name !== next;
    byId("http").hidden = status.demo || !["account", "github"].includes(next) || !(config?.origin || location.origin).startsWith("http:");
    error("");
    byId("progress").textContent = status.demo ? "Preview · No settings or runners will be changed" : next === "verify" ? "First, confirm server ownership" : next === "account" ? "Step 1 of 3 · Secure your dashboard" : next === "github" ? "Step 2 of 3 · Choose GitHub access" : next === "review-step" ? "Step 3 of 3 · Review" : "Setup complete";
    byId("back").hidden = !["github", "review-step"].includes(next) || (next === "github" && (status.accessEnabled || status.demo));
    byId("next").textContent = next === "verify" ? "Verify code" : next === "review-step" ? status.demo ? "Finish preview" : "Save setup" : next === "done" ? "Done" : "Continue";
    const heading = byId(next).querySelector("h3"); heading.tabIndex = -1; heading.focus();
    dialog.scrollTop = 0;
  }
  function lock(value) {
    busy = value;
    dialog.setAttribute("aria-busy", String(value));
    for (const id of ["next", "back", "discover"]) byId(id).disabled = value;
    byId("close").disabled = value && step === "review-step";
    if (step === "review-step") byId("next").textContent = value ? "Checking and saving…" : status.demo ? "Finish preview" : "Save setup";
  }
  async function request(path, body) {
    const headers = {"Content-Type":"application/json"};
    if (setupCode) headers["X-RR-Setup-Code"] = setupCode;
    const response = await fetch("/api/setup/" + path, {method:body === undefined ? "GET" : "POST", headers, cache:"no-store", ...(body === undefined ? {} : {body:JSON.stringify(body)})});
    const result = await response.json().catch(() => ({}));
    if (!response.ok) { const failure = new Error(result.message || (response.status === 403 ? "The setup code is invalid or expired. Generate a new code on the server and try again." : "Setup is unavailable. Try again in a moment.")); failure.status = response.status; throw failure; }
    return result;
  }
  function clearSecrets() { setupCode = ""; for (const id of ["code", "password", "confirm", "token"]) byId(id).value = ""; }
  function scopes() { return [...new Set([...selection, ...byId("scopes").value.split(/[\n,]/).map(s => s.trim()).filter(Boolean)])]; }
  function count() { byId("selected").textContent = scopes().length + " permitted " + (scopes().length === 1 ? "scope" : "scopes"); }
  async function loadConfig(preserve = false) {
    const active = generation;
    const result = await request("config");
    if (generation !== active) return false;
    config = result;
    if (config.pendingRestart) { finish({restartRequired:true,message:"Setup has already been saved. Restart the dashboard to apply it."}); return false; }
    if (config.issues?.length) throw new Error(config.issues.join(" "));
    byId("origin").textContent = config.origin || location.origin;
    byId("http").hidden = !(config.origin || location.origin).startsWith("http:");
    if (!preserve) byId("scopes").value = (config.scopes || []).join("\n");
    count();
    return true;
  }
  function finish(result) {
    clearSecrets();
    byId("done-title").textContent = status.demo ? "Preview complete" : "Settings saved";
    byId("done-message").textContent = result.message || "Your management settings are ready.";
    byId("restart").hidden = !result.restartRequired;
    show("done");
  }
  async function open() {
    generation++; clearSecrets(); selection.clear(); byId("repositories").replaceChildren(); byId("filter-label").hidden = true;
    const active = generation;
    byId("username").value = ""; byId("allow-http").checked = false; byId("discovery-status").textContent = "";
    byId("scopes").value = ""; byId("filter").value = ""; config = null; returnAfterVerify = null;
    dialog.showModal(); lock(true);
    try {
      status = await request("status");
      if (active !== generation) return;
      byId("token-label").hidden = status.demo; byId("token-link").hidden = status.demo; byId("secret-hint").hidden = status.demo;
      byId("token-hint").textContent = status.demo ? "Try choosing repositories using sample data. No GitHub token is needed and no server settings will change." : "Create a fine-grained token for the repositories you want to manage. Give it Administration: read and write to register runners, and Actions: read and write for workflow controls.";
      if (status.pendingRestart) finish({restartRequired:true,message:"Setup has already been saved. Restart the dashboard to apply it."});
      else if (status.requiresCode && !status.demo) show("verify");
      else if (await loadConfig()) show(status.accessEnabled || status.demo ? "github" : "account");
    } catch (e) { if (active === generation) { show("verify"); error(e.message); } }
    finally { if (active === generation) lock(false); }
  }
  byId("open").addEventListener("click", open);
  byId("close").addEventListener("click", () => { clearSecrets(); dialog.close(); });
  dialog.addEventListener("cancel", event => { if (busy && step === "review-step") event.preventDefault(); else clearSecrets(); });
  dialog.addEventListener("close", () => { generation++; clearSecrets(); lock(false); });
  byId("next").addEventListener("click", async () => {
    if (busy) return;
    if (step === "done") { dialog.close(); return; }
    const active = generation;
    lock(true); error("");
    try {
      if (step === "verify") {
        if (!byId("code").reportValidity()) return;
        setupCode = byId("code").value.trim();
        if (await loadConfig(!!returnAfterVerify)) { const next = returnAfterVerify || (status.accessEnabled ? "github" : "account"); returnAfterVerify = null; show(next); }
      } else if (step === "account") {
        if (!["username", "password", "confirm"].every(id => byId(id).reportValidity())) return;
        if (byId("password").value !== byId("confirm").value) throw new Error("The passwords do not match.");
        if (!byId("http").hidden && !byId("allow-http").checked) throw new Error("Open the dashboard over HTTPS, or acknowledge the HTTP connection to continue.");
        show("github");
      } else if (step === "github") {
        if (!byId("http").hidden && !byId("allow-http").checked) throw new Error("Open the dashboard over HTTPS, or acknowledge the HTTP connection to continue.");
        if (!status.demo && !byId("token").value.trim()) throw new Error("Enter a GitHub management token to continue.");
        if (!scopes().length) throw new Error("Choose at least one repository, or add a scope manually.");
        if (scopes().length > 25) throw new Error("Choose up to 25 repository or organization scopes.");
        if (scopes().some(scope => !/^(repo:[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+|org:[A-Za-z0-9_.-]+)$/.test(scope))) throw new Error("Use repo:owner/repository or org:organization for each additional scope.");
        const rows = [["Account", status.accessEnabled ? "Keep existing sign-in" : status.demo ? "Sample administrator" : byId("username").value.trim()], ["Address", config.origin || location.origin], ["GitHub access", scopes().join(", ")], ["Runner folder", config.rootDirectory || "Server default"]];
        byId("review").replaceChildren(...rows.flatMap(([label, value]) => { const dt = document.createElement("dt"), dd = document.createElement("dd"); dt.textContent = label; dd.textContent = value; return [dt,dd]; }));
        show("review-step");
      } else if (step === "review-step") {
        const result = await request("save", {username:byId("username").value.trim(), password:byId("password").value, token:byId("token").value.trim(), scopes:scopes(), origin:config.origin || location.origin, allowHttp:byId("allow-http").checked});
        if (active === generation) finish(result);
      }
    } catch (e) { if (active === generation) { if (e.status === 403 && status.requiresCode && step !== "verify") { returnAfterVerify = step; show("verify"); } error(e.message); } }
    finally { if (active === generation) lock(false); }
  });
  byId("back").addEventListener("click", () => show(step === "review-step" ? "github" : "account"));
  byId("discover").addEventListener("click", async () => {
    const active = generation; lock(true); error(""); byId("discovery-status").textContent = "Checking GitHub access…";
    try {
      if (!status.demo && !byId("token").value.trim()) throw new Error("Enter your GitHub management token first.");
      if (!byId("http").hidden && !byId("allow-http").checked) throw new Error("Open the dashboard over HTTPS, or acknowledge the HTTP connection before sending your token.");
      const result = await request("discover", {token:byId("token").value.trim()});
      if (active !== generation) return;
      byId("repositories").replaceChildren(...(result.repositories || []).map(repo => {
        const label = document.createElement("label"), box = document.createElement("input"), text = document.createElement("span");
        label.className = "setup-check"; box.type = "checkbox"; const scope = "repo:" + repo.fullName; box.checked = selection.has(scope);
        box.addEventListener("change", () => { if (box.checked) selection.add(scope); else selection.delete(scope); count(); });
        text.textContent = repo.fullName + (repo.isPrivate ? " · Private" : ""); label.append(box,text); return label;
      }));
      byId("filter-label").hidden = !(result.repositories || []).length;
      byId("discovery-status").textContent = (result.message || ((result.repositories || []).length ? "Choose the repositories this dashboard may manage." : "No repositories found. Check the token's repository access, or add a scope manually below.")) + (result.partial ? " This list is incomplete. Add any missing repositories manually below." : "");
      count();
    } catch (e) { if (active === generation) { if (e.status === 403 && status.requiresCode) { returnAfterVerify = "github"; show("verify"); } error(e.message); byId("discovery-status").textContent = "You can retry or add a repository or organization manually."; } }
    finally { if (active === generation) lock(false); }
  });
  byId("filter").addEventListener("input", () => { const query = byId("filter").value.toLowerCase(); for (const label of byId("repositories").children) label.hidden = !label.textContent.toLowerCase().includes(query); });
  byId("scopes").addEventListener("input", count);
  async function availability() {
    try {
      await window.accessReady;
      status = await request("status");
      const permitted = !status.accessEnabled || window.dashboardAccess?.canAdmin;
      const unavailable = !status.available && !status.pendingRestart && !!status.unavailableReason;
      document.getElementById("management-setup").hidden = (!status.available && !status.pendingRestart && !unavailable) || !permitted || (status.managementEnabled && !status.demo && !status.pendingRestart);
      byId("open").hidden = unavailable;
      byId("open").textContent = status.demo ? "Preview setup" : status.pendingRestart ? "Finish setup" : "Set up management";
      if (unavailable) { byId("callout-title").textContent = "A server change is needed first"; byId("callout-hint").textContent = status.unavailableReason; }
      else if (status.demo) { byId("callout-title").textContent = "Try the management setup"; byId("callout-hint").textContent = "Preview how you would connect GitHub and choose repositories on your server."; }
    } catch { /* Management's own status remains visible if setup cannot load. */ }
  }
  availability();
})();
