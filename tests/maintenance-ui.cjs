// Isolated browser checks use the real Preferences markup, styles, and access wrapper.
// Every update endpoint is a fixture: this test never installs or downloads software.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), http = require('node:http');
const root = path.resolve(__dirname, '../src/RunnerRoom/wwwroot');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const index = read('index.html');
const preferences = index.slice(index.indexOf('  <dialog id="preferences-dialog"'), index.indexOf('  <dialog id="log-dialog"'));
const info = index.match(/<section class="application-info"[\s\S]*?<\/section>/)[0];
const html = `<!doctype html><html data-theme="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="/app.css"></head><body><header class="topbar"><h1>Runner Room</h1><a id="app-update-indicator" hidden></a><button id="preferences-open">Preferences</button><button id="refresh">Refresh</button></header><span id="connection"></span><div id="access-session"><span id="access-identity"></span><button id="access-logout">Sign out</button></div><main>${info}</main>${preferences}<script>window.$ = id => document.getElementById(id); window.dateTime = value => value; $('preferences-open').onclick = () => $('preferences-dialog').showModal(); $('preferences-close').onclick = () => $('preferences-dialog').close();</script><script src="/access.js"></script><script src="/application-updates.js"></script></body></html>`;
const initialMetadata = { enabled: true, demo: false, currentVersion: '0.9.0', latestVersion: '0.10.0', status: 'update_available', updateAvailable: true, releaseUrl: 'https://github.com/igor-couto/github-self-hosted-runner-manager/releases/tag/v0.10.0', checkedAt: '2026-10-10T12:00:00Z', stale: false, message: 'A newer release is available.' };
const initialInstallation = { supported: true, demo: false, state: 'idle', message: '', requestId: null, updatedAt: null, targetVersion: null };
let metadata, installation, role, calls, failMetadata, installationDisconnect, installMode, resolveInstall;
function reset() { metadata = { ...initialMetadata }; installation = { ...initialInstallation }; role = 'lan'; calls = []; failMetadata = false; installationDisconnect = false; installMode = 'queued'; resolveInstall = null; }
reset();
const server = http.createServer(async (req, res) => {
  const send = (status, body) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  if (req.url === '/api/access/session') return send(200, { enabled: role !== 'lan', authenticated: true, name: 'Tester', role, canAdmin: ['admin', 'lan'].includes(role), canOperate: role !== 'viewer', csrfToken: 'fixture-csrf' });
  if (req.url === '/api/updates') return failMetadata ? send(503, { message: 'Unavailable' }) : send(200, metadata);
  if (req.url === '/api/updates/installation') { if (installationDisconnect) return res.destroy(); return send(200, installation); }
  if (req.url.startsWith('/api/updates/')) {
    let body = ''; for await (const chunk of req) body += chunk;
    calls.push({ path: req.url, method: req.method, body, csrf: req.headers['x-rr-csrf'] });
    if (req.url === '/api/updates/check') return send(200, metadata);
    if (req.url === '/api/updates/install') {
      if (['rejected', 'conflict'].includes(installMode)) return send(installMode === 'conflict' ? 409 : 400, { message: 'A runner operation is in progress. Wait for it to finish.' });
      if (installMode === 'hold') await new Promise(resolve => { resolveInstall = resolve; });
      installation = { ...installation, state: 'queued', message: 'The update has been queued.', requestId: 'request-1', targetVersion: '0.10.0' };
      if (['disconnect', 'unconfirmed'].includes(installMode)) { if (installMode === 'unconfirmed') installation = { ...initialInstallation }; res.writeHead(202, { 'Content-Type': 'application/json' }); res.flushHeaders(); res.write('{'); return setImmediate(() => res.destroy()); }
      return send(202, installation);
    }
  }
  const files = { '/': html, '/app.css': read('app.css'), '/application-updates.js': read('application-updates.js'), '/access.js': read('access.js') };
  if (!Object.hasOwn(files, req.url)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': req.url.endsWith('.js') ? 'text/javascript' : req.url.endsWith('.css') ? 'text/css' : 'text/html' }); res.end(files[req.url]);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } }), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const base = `http://127.0.0.1:${server.address().port}`;
    const waitStatus = text => page.waitForFunction(value => document.getElementById('preferences-update-status').textContent === value, text);
    const waitCard = text => page.waitForFunction(value => document.getElementById('app-update-status').textContent === value, text);
    const load = async () => { await page.goto(base); await waitCard('Update available'); await page.locator('#preferences-open').click(); };
    await load(); await waitStatus('Update available');
    assert.equal(await page.locator('#preferences-installed-version').textContent(), '0.9.0');
    assert.equal(await page.locator('#preferences-latest-version').textContent(), '0.10.0');
    assert.equal(await page.locator('#app-release-link').getAttribute('href'), metadata.releaseUrl);
    assert.equal(await page.locator('#preferences-update-install').isEnabled(), true);
    assert.equal(await page.locator('#preferences-update-notice').isVisible(), true);
    await page.locator('#preferences-update-check').click(); await waitStatus('Update available');
    assert.deepEqual(calls[0], { path: '/api/updates/check', method: 'POST', body: '{}', csrf: 'fixture-csrf' });
    const screenshotDir = path.resolve(__dirname, '../artifacts'); fs.mkdirSync(screenshotDir, { recursive: true });
    await page.screenshot({ path: path.join(screenshotDir, 'application-updates-desktop.png') });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    assert.equal(await page.locator('#preferences-dialog').evaluate(el => el.scrollWidth <= el.clientWidth), true);
    await page.screenshot({ path: path.join(screenshotDir, 'application-updates-mobile.png') });
    await page.setViewportSize({ width: 1440, height: 1000 });

    // Duplicate clicks and closing Preferences cannot submit an update twice or lose its progress.
    installMode = 'hold';
    await page.evaluate(() => { document.getElementById('preferences-update-install').click(); document.getElementById('preferences-update-install').click(); });
    await waitStatus('Requesting update…');
    while (!resolveInstall) await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(calls.filter(c => c.path.endsWith('/install')).length, 1);
    await page.locator('#preferences-close').click(); resolveInstall(); await waitStatus('Update queued…');
    await page.locator('#preferences-open').click(); assert.equal(await page.locator('#preferences-update-install').isEnabled(), false);
    for (const [state, title] of [['downloading', 'Downloading update…'], ['installing', 'Installing update…'], ['restarting', 'Restarting dashboard…']]) {
      installation = { ...installation, state }; await waitStatus(title);
    }
    installationDisconnect = true; await waitStatus('Reconnecting…');
    assert.equal(await page.locator('#preferences-update-install').isEnabled(), false);
    installationDisconnect = false;
    installation = { ...installation, state: 'succeeded', message: 'Runner Room was updated successfully.' };
    metadata = { ...metadata, currentVersion: '0.10.0', status: 'up_to_date', updateAvailable: false };
    await waitStatus('Update complete'); await waitCard('Up to date');
    assert.equal(await page.locator('#preferences-installed-version').textContent(), '0.10.0');
    assert.equal(await page.locator('#preferences-update-install').isEnabled(), false);
    assert.equal(calls.filter(c => c.path.endsWith('/install')).length, 1);
    assert.equal(await page.locator('#preferences-update-reload').isVisible(), true);
    await page.locator('#preferences-close').click(); await page.locator('#preferences-open').click(); await waitStatus('Update complete');
    await page.locator('#preferences-update-reload').click(); await waitCard('Up to date');
    await page.locator('#preferences-open').click(); await waitStatus('Update complete');
    assert.equal(await page.locator('#preferences-update-reload').isVisible(), false);

    // Failed updates retain an actionable result; an HTTP rejection does not invent a completed update.
    reset(); await load(); installMode = 'rejected';
    await page.locator('#preferences-update-install').click();
    await page.waitForFunction(() => document.getElementById('preferences-update-message').textContent.includes('runner operation'));
    assert.equal(await page.locator('#preferences-update-install').isEnabled(), true);
    installMode = 'conflict'; await page.locator('#preferences-update-install').click(); await waitStatus('Update not started');
    assert.match(await page.locator('#preferences-update-message').textContent(), /runner operation/);
    assert.equal(await page.locator('#preferences-update-install').isEnabled(), true);
    installMode = 'queued'; await page.locator('#preferences-update-install').click(); await waitStatus('Update queued…');
    installation = { ...installation, state: 'rolled_back', message: 'The new release did not start. The previous version was restored.' };
    await waitStatus('Previous version restored');
    assert.match(await page.locator('#preferences-update-message').textContent(), /previous version was restored/);
    assert.equal(await page.locator('#preferences-installed-version').textContent(), '0.9.0');

    // An uncertain POST response is polled, never repeated. A deadline reports unknown status, not success.
    reset(); await load(); installMode = 'disconnect'; await page.locator('#preferences-update-install').click();
    await waitStatus('Reconnecting…'); await waitStatus('Update queued…');
    await page.evaluate(() => { const originalNow = Date.now; Date.now = () => originalNow() + 21 * 60000; });
    await waitStatus('Update status unavailable');
    assert.match(await page.locator('#preferences-update-message').textContent(), /not reported a result/);
    assert.equal(await page.locator('#preferences-update-install').isEnabled(), false);
    assert.equal(await page.locator('#preferences-update-check').isEnabled(), true);
    installation = { ...installation, state: 'failed', message: 'Download failed. Check the server connection and try again.' };
    await page.locator('#preferences-update-check').click(); await waitStatus('Update failed');
    assert.equal(calls.filter(c => c.path.endsWith('/install')).length, 1);
    reset(); await load(); installMode = 'unconfirmed'; await page.locator('#preferences-update-install').click();
    await waitStatus('Update not started');
    assert.match(await page.locator('#preferences-update-message').textContent(), /not confirmed a new update/);
    assert.equal(await page.locator('#preferences-update-install').isEnabled(), true);
    assert.equal(calls.filter(c => c.path.endsWith('/install')).length, 1);

    // Older installations and deployment-managed containers have clear guidance and no install action.
    for (const message of ['Run the installer once to enable updates from Preferences.', 'Docker and Helm deployments are updated through their deployment tools.']) {
      reset(); installation = { ...installation, supported: false, state: 'unsupported', message }; await load();
      await page.waitForFunction(value => document.getElementById('preferences-update-support').textContent === value, message);
      assert.equal(await page.locator('#preferences-update-install').isVisible(), false);
    }
    for (const currentRole of ['viewer', 'operator']) {
      reset(); role = currentRole; await load();
      assert.equal(await page.locator('#preferences-update-install').isVisible(), false);
      assert.equal(await page.locator('#preferences-update-check').isVisible(), false);
      assert.match(await page.locator('#preferences-update-support').textContent(), /Administrator/);
      assert.equal(calls.length, 0);
    }
    reset(); role = 'admin'; await load(); await page.locator('#preferences-update-check').click();
    await waitStatus('Update available'); assert.equal(calls[0].csrf, 'fixture-csrf');

    // Demo uses the same progress flow while labeling its unchanged result as a preview.
    reset(); installation.demo = true; metadata.demo = true; await load();
    assert.equal(await page.locator('#preferences-update-install').textContent(), 'Preview update');
    assert.match(await page.locator('#preferences-update-support').textContent(), /No software/);
    await page.locator('#preferences-update-install').click(); await waitStatus('Update queued…');
    installation = { ...installation, state: 'succeeded', message: 'Preview complete. No software was changed.' };
    await waitStatus('Preview complete'); assert.equal(await page.locator('#preferences-installed-version').textContent(), '0.9.0');

    // Existing version indicators still handle stale/errors and never link outside this project's releases.
    reset(); await load(); await page.locator('#preferences-close').click();
    async function refresh(text) { await page.locator('#refresh').click(); await waitCard(text); }
    metadata = { ...metadata, status: 'up_to_date', updateAvailable: false }; await refresh('Up to date');
    assert.equal(await page.locator('#app-update-indicator').isVisible(), false);
    metadata = { ...metadata, status: 'unavailable', updateAvailable: true, stale: true }; await refresh('Update check unavailable · last result is stale');
    assert.equal(await page.locator('#app-update-indicator').isVisible(), false);
    await page.locator('#preferences-open').click(); assert.equal(await page.locator('#preferences-update-install').isEnabled(), false); await page.locator('#preferences-close').click();
    metadata = { ...metadata, stale: false, status: 'disabled', latestVersion: null, releaseUrl: null, checkedAt: null }; await refresh('Update checks disabled');
    assert.equal(await page.locator('#app-release-link').isVisible(), false);
    metadata = { ...metadata, status: 'development' }; await refresh('Development build');
    metadata = { ...metadata, status: 'update_available', releaseUrl: 'https://github.com/attacker/repo/releases/tag/v1.0.0' }; await refresh('Update available');
    assert.equal(await page.locator('#app-release-link').isVisible(), false);
    failMetadata = true; await refresh('Update status unavailable');
    assert.match(await page.locator('#app-update-message').textContent(), /out of date/);
    assert.deepEqual(errors, []);
    console.log('PASS: Preferences updates, check/install CSRF, duplicate prevention, close/reopen, all progress stages, restart reconnect, rollback/failure, uncertain POST, bounded polling, demo, roles, unsupported deployments, metadata and mobile layout.');
    console.log(`Screenshots: ${screenshotDir}`);
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; server.close(); });
