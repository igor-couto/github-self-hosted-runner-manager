// Isolated browser checks use real setup markup/styles and the access wrapper. No GitHub or runner changes.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), http = require('node:http'), os = require('node:os');
const root = path.resolve(__dirname, '../src/RunnerRoom/wwwroot');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const index = read('index.html');
const callout = index.slice(index.indexOf('        <div id="management-setup"'), index.indexOf('        <div id="management-content"'));
const html = `<!doctype html><html data-theme="dark"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/setup.css"><body><span id="connection"></span><div id="access-session"><span id="access-identity"></span><button id="access-logout">Sign out</button></div><main><section class="panel management-panel"><div class="panel-heading"><h2>Runner management</h2><button id="management-refresh">Refresh management</button></div><div class="monitor-content"><p id="management-message" class="monitor-warning">Runner monitoring is active. Set up management below to create or control runners.</p>${callout}</div></section></main><script src="/access.js"></script><script src="/setup.js"></script></body></html>`;
let mode = 'lan', badDiscovery = false, badSave = false, configFailure = false, calls = [];
const server = http.createServer(async (req, res) => {
  const send = (status, body) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  if (req.url.startsWith('/api/')) {
    const demo = mode === 'demo', accessEnabled = ['admin', 'viewer', 'operator'].includes(mode);
    if (req.url === '/api/access/session') return send(200, { enabled: accessEnabled, authenticated: true, canAdmin: !accessEnabled || mode === 'admin', canOperate: mode !== 'viewer', name: 'Tester', role: mode, csrfToken: 'test-csrf' });
    if (req.url === '/api/setup/status') return send(200, { available: !['pending', 'blocked'].includes(mode), demo, accessEnabled, managementEnabled: demo, pendingRestart: mode === 'pending', unavailableReason: mode === 'blocked' ? 'This deployment uses an explicit settings override.' : null });
    if (accessEnabled && mode !== 'admin') return send(403, { message: 'Administrator access is required.' });
    if (req.url === '/api/setup/config') return configFailure ? send(503, { message: 'Setup configuration could not be loaded.' }) : send(200, { demo, accessEnabled, origin: `http://127.0.0.1:${server.address().port}`, rootDirectory: '/var/lib/runner-room/runners', scopes: [], issues: [] });
    let body = ''; for await (const chunk of req) body += chunk;
    calls.push({ path: req.url, headers: req.headers, body: JSON.parse(body || '{}') });
    assert.equal(req.headers['x-rr-csrf'], 'test-csrf');
    assert.equal(req.headers['x-rr-setup-code'], undefined);
    if (req.url === '/api/setup/discover') return badDiscovery ? send(503, { message: 'GitHub could not be reached. Try again.' }) : send(200, { repositories: [{ fullName: 'owner/project', isPrivate: true }, { fullName: 'owner/<img src=x>', isPrivate: false }], partial: false });
    if (req.url === '/api/setup/save') return badSave ? send(400, { message: 'This token needs Administration write access.' }) : send(200, { saved: !demo, restartRequired: false, message: demo ? 'Preview complete. No settings were changed.' : 'Runner management is ready.' });
    return send(404, {});
  }
  const files = { '/': html, '/app.css': read('app.css'), '/setup.css': read('setup.css'), '/setup.js': read('setup.js'), '/access.js': read('access.js') };
  if (!Object.hasOwn(files, req.url)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': req.url.endsWith('.js') ? 'text/javascript' : req.url.endsWith('.css') ? 'text/css' : 'text/html' }); res.end(files[req.url]);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } }), page = await context.newPage(), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const base = `http://127.0.0.1:${server.address().port}`;
    const visible = id => page.locator('#setup-' + id).isVisible();
    const screenshotDir = path.join(os.tmpdir(), 'runner-room-setup-ui'); fs.mkdirSync(screenshotDir, { recursive: true });
    const checkLayout = async size => {
      await page.locator('#setup-open').waitFor();
      const warning = await page.locator('#management-message').boundingBox(), setup = await page.locator('#management-setup').boundingBox();
      assert.ok(setup.y - warning.y - warning.height >= 16, `${size}: warning and setup need breathing room`);
      assert.ok(Math.abs(setup.x - warning.x) <= 1, `${size}: warning and setup align`);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      await page.screenshot({ path: path.join(screenshotDir, `management-spacing-${size}.png`), fullPage: true });
    };
    await page.goto(base); await checkLayout('desktop');
    await page.getByRole('button', { name: 'Set up management', exact: true }).click();
    await page.locator('#setup-github').waitFor();
    assert.equal(await page.locator('#setup-verify, #setup-code, #setup-account, #setup-username, #setup-password, #setup-confirm, #setup-allow-http').count(), 0);
    assert.match(await page.locator('#setup-progress').textContent(), /Step 1 of 2/);
    await page.locator('#setup-next').click(); assert.match(await page.locator('#setup-error').textContent(), /management token/);
    await page.locator('#setup-token').fill('fixture-token-only'); badDiscovery = true; await page.locator('#setup-discover').click();
    await page.getByRole('alert').filter({ hasText: 'could not be reached' }).waitFor(); assert.equal(await page.locator('#setup-token').inputValue(), 'fixture-token-only');
    badDiscovery = false; await page.locator('#setup-discover').click(); await page.getByRole('checkbox', { name: 'owner/project · Private' }).check();
    assert.equal(await page.locator('#setup-repositories img').count(), 0);
    await page.locator('#setup-filter').fill('project'); assert.equal(await page.locator('#setup-repositories label:visible').count(), 1);
    await page.locator('#setup-next').click(); assert.equal(await visible('review-step'), true);
    assert.ok(!(await page.locator('#setup-review').textContent()).includes('fixture-token-only'));
    badSave = true; await page.locator('#setup-next').click(); await page.getByRole('alert').filter({ hasText: 'Administration write' }).waitFor();
    assert.equal(await visible('review-step'), true); assert.equal(await page.locator('#setup-token').inputValue(), 'fixture-token-only');
    await page.locator('#setup-back').click(); assert.equal(await page.getByRole('checkbox', { name: 'owner/project · Private' }).isChecked(), true);
    await page.locator('#setup-next').click();
    await page.evaluate(() => { window.refreshes = 0; document.getElementById('management-refresh').addEventListener('click', () => window.refreshes++); });
    badSave = false; await page.locator('#setup-next').click(); await page.locator('#setup-done').waitFor();
    const save = calls.filter(c => c.path.endsWith('/save')).at(-1); assert.deepEqual(save.body, { token: 'fixture-token-only', scopes: ['repo:owner/project'] });
    assert.equal(await visible('restart'), false); assert.equal(await page.locator('#setup-token').inputValue(), '');
    assert.equal(await page.evaluate(() => window.refreshes), 1); assert.equal(await page.locator('#management-setup').isVisible(), false);
    assert.deepEqual(await page.evaluate(() => [Object.keys(localStorage), Object.keys(sessionStorage)]), [[], []]);
    await page.locator('#setup-next').click(); assert.equal(await page.locator('#setup-dialog').isVisible(), false);
    mode = 'pending'; await page.reload(); await page.getByRole('button', { name: 'Finish setup', exact: true }).click(); await page.locator('#setup-done').waitFor(); assert.equal(await visible('restart'), true);
    mode = 'admin'; await page.reload(); await page.locator('#setup-open').click(); await page.locator('#setup-github').waitFor();
    await page.locator('#setup-token').fill('admin-token'); await page.locator('#setup-discover').click(); await page.getByRole('checkbox', { name: 'owner/project · Private' }).waitFor();
    await page.locator('#setup-close').click(); assert.equal(await page.locator('#setup-token').inputValue(), '');
    mode = 'lan'; await page.setViewportSize({ width: 390, height: 844 }); await page.reload(); await checkLayout('mobile');
    await page.locator('#setup-open').click(); await page.locator('#setup-github').waitFor();
    assert.equal(await page.locator('#setup-dialog').evaluate(d => d.scrollWidth <= d.clientWidth), true);
    await page.screenshot({ path: path.join(screenshotDir, 'management-setup-mobile.png'), fullPage: true });
    await page.locator('#setup-close').click(); configFailure = true; await page.locator('#setup-open').click();
    await page.getByRole('alert').filter({ hasText: 'could not be loaded' }).waitFor(); assert.equal(await page.locator('#setup-next').isDisabled(), true); configFailure = false;
    mode = 'demo'; await page.reload(); await page.getByRole('button', { name: 'Preview setup' }).click(); await page.locator('#setup-github').waitFor();
    assert.equal(await visible('token-label'), false); await page.locator('#setup-discover').click(); await page.getByRole('checkbox', { name: 'owner/project · Private' }).check();
    await page.locator('#setup-next').click(); await page.locator('#setup-next').click(); await page.locator('#setup-done').waitFor(); assert.equal(await visible('restart'), false);
    for (mode of ['viewer', 'operator']) { await page.reload(); await page.waitForFunction(() => window.dashboardAccess.role === document.getElementById('access-identity').textContent.split(' · ')[1]); await page.waitForTimeout(100); assert.equal(await page.locator('#management-setup').isVisible(), false); }
    mode = 'blocked'; await page.reload(); await page.getByText('A server change is needed first').waitFor(); assert.equal(await page.locator('#setup-open').isVisible(), false); assert.match(await page.locator('#setup-callout-hint').textContent(), /explicit settings override/);
    assert.deepEqual(errors, []);
    console.log('PASS: direct LAN setup, no ownership/login/HTTP gate, safe repository selection, retry preserves progress, live activation refresh, restart fallback, credential clearing, CSRF, admin/demo/viewer/operator access, desktop/mobile spacing and sizing.');
    console.log(`Screenshots: ${screenshotDir}`);
  } finally { await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
