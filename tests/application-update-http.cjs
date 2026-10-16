// Isolated demo servers exercise real endpoint access checks without downloading or installing releases.
const { request } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

(async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'runner-room-update-http-'));
  const contexts = [];
  let child;
  async function stop() {
    if (child && child.exitCode === null) {
      const exited = new Promise(resolve => child.once('exit', resolve));
      child.kill(); await exited;
    }
  }
  try {
    const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${socket.address().port}`; await new Promise(resolve => socket.close(resolve));
    const password = 'test-only-update-password', salt = crypto.randomBytes(16), header = Buffer.alloc(13);
    header[0] = 1; header.writeUInt32BE(2, 1); header.writeUInt32BE(600000, 5); header.writeUInt32BE(16, 9);
    const hash = Buffer.concat([header, salt, crypto.pbkdf2Sync(password, salt, 600000, 32, 'sha512')]).toString('base64');
    async function start(accessEnabled) {
      const settings = path.join(directory, 'settings.json');
      await fs.writeFile(settings, JSON.stringify({ Demo: true, Monitoring: { StateDirectory: directory }, Access: {
        Enabled: accessEnabled, RequireHttps: false, PublicOrigin: base,
        LocalUsers: ['admin', 'operator', 'viewer'].map(role => ({ Username: role, Role: role, PasswordHash: hash }))
      } }));
      child = spawn('dotnet', ['bin/Release/net9.0/RunnerRoom.dll', '--SettingsFile', settings, '--urls', base], {
        cwd: path.resolve(__dirname, '../src/RunnerRoom'), stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true
      });
      let output = ''; for (const stream of [child.stdout, child.stderr]) stream.on('data', data => { output = (output + data).slice(-6000); });
      for (let i = 0; i < 150; i++) {
        try { if ((await fetch(base + '/healthz')).ok) return; } catch {}
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      throw new Error('Demo did not start: ' + output);
    }
    async function api() { const context = await request.newContext({ baseURL: base }); contexts.push(context); return context; }
    await start(true);
    const anonymous = await api();
    assert.equal((await anonymous.get('/api/updates/installation')).status(), 401);
    for (const role of ['viewer', 'operator', 'admin']) {
      const context = await api();
      let session = await (await context.get('/api/access/session')).json();
      assert.equal((await context.post('/api/access/login', { headers: { 'X-RR-CSRF': session.csrfToken }, data: { username: role, password } })).status(), 200);
      session = await (await context.get('/api/access/session')).json();
      const headers = { 'X-RR-CSRF': session.csrfToken, Origin: base };
      assert.equal((await context.get('/api/updates')).status(), 200, 'Version information remains available to every signed-in role');
      assert.equal((await context.get('/api/updates/installation')).status(), role === 'admin' ? 200 : 403);
      for (const endpoint of ['check', 'install']) {
        if (role !== 'admin') {
          assert.equal((await context.post('/api/updates/' + endpoint, { headers, data: {} })).status(), 403); continue;
        }
        assert.equal((await context.post('/api/updates/' + endpoint, { data: {} })).status(), 403, 'Existing sign-in requires CSRF');
        assert.equal((await context.post('/api/updates/' + endpoint, { headers: { ...headers, Origin: 'https://other.example' }, data: {} })).status(), 403);
        assert.equal((await context.post('/api/updates/' + endpoint, { headers, form: {} })).status(), 403);
        assert.equal((await context.post('/api/updates/' + endpoint, { headers, data: { filler: 'x'.repeat(33000) } })).status(), 413);
      }
      if (role === 'admin') {
        const check = await context.post('/api/updates/check', { headers, data: {} }); assert.ok(check.ok());
        const install = await context.post('/api/updates/install', { headers, data: {} }); assert.equal(install.status(), 202);
        assert.equal((await context.post('/api/updates/install', { headers, data: {} })).status(), 409, 'Duplicate clicks cannot enqueue a second installation');
      }
    }
    await stop(); await start(false);
    const lan = await api();
    const availability = await (await lan.get('/api/updates/installation')).json();
    assert.equal(availability.demo, true); assert.equal(availability.supported, true);
    assert.equal((await lan.post('/api/updates/install', { headers: { Origin: 'https://other.example' }, data: {} })).status(), 403);
    assert.equal((await lan.post('/api/updates/install', { headers: { Origin: base }, data: {} })).status(), 202, 'Open-LAN dashboards can start a preview without login');
    let result;
    for (let i = 0; i < 80; i++) {
      result = await (await lan.get('/api/updates/installation')).json();
      if (result.state === 'succeeded') break;
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    assert.equal(result.state, 'succeeded', 'Demo reports eventual completion');
    assert.equal(result.demo, true);
    assert.equal((await fs.readdir(directory)).some(name => name.includes('updater') || name === 'request'), false, 'Demo creates no update requests');
    console.log('PASS: update API roles, CSRF, same-origin and body limits; duplicate prevention; open-LAN preview and completion.');
  } finally {
    await stop();
    for (const context of contexts) await context.dispose();
    await fs.rm(directory, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
