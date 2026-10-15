// Runs an isolated authenticated demo: no real tokens, runners, or GitHub requests.
const {request,chromium} = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const crypto = require('node:crypto');
const {spawn,spawnSync} = require('node:child_process');

(async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'runner-room-setup-http-'));
  let child,browser;
  const contexts = [];
  try {
    const socket = net.createServer(); await new Promise(resolve => socket.listen(0,'127.0.0.1',resolve));
    const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
    const base = `http://127.0.0.1:${port}`;
    const password = 'test-only-password-42', salt = crypto.randomBytes(16), header = Buffer.alloc(13);
    header[0]=1; header.writeUInt32BE(2,1); header.writeUInt32BE(600000,5); header.writeUInt32BE(16,9);
    const hash = Buffer.concat([header,salt,crypto.pbkdf2Sync(password,salt,600000,32,'sha512')]).toString('base64');
    const file = path.join(directory,'settings.json');
    await fs.writeFile(file,JSON.stringify({Demo:true,Monitoring:{StateDirectory:directory},Access:{Enabled:true,RequireHttps:false,PublicOrigin:base,LocalUsers:['admin','operator','viewer'].map(role=>({Username:role,Role:role,PasswordHash:hash}))}}));
    const cwd = path.resolve(__dirname,'../src/RunnerRoom');
    const cliFile=path.join(directory,'cli-settings.json'), cliState=path.join(directory,'cli-state');
    await fs.writeFile(cliFile,JSON.stringify({Demo:false,Monitoring:{StateDirectory:cliState}}));
    const generated=spawnSync('dotnet',['bin/Release/net9.0/RunnerRoom.dll','--SettingsFile',cliFile,'--setup-code'],{cwd,encoding:'utf8',windowsHide:true});
    assert.equal(generated.status,0,'Setup code command must run before starting host services');
    const code=generated.stdout.trim();assert.match(code,/^[a-f0-9]{48}$/);
    const codeRecord=await fs.readFile(path.join(cliState,'setup-code.json'),'utf8');assert.ok(!codeRecord.includes(code),'Only a hash of the ownership code is stored');
    child = spawn('dotnet',['bin/Release/net9.0/RunnerRoom.dll','--SettingsFile',file,'--urls',base],{cwd,stdio:['ignore','pipe','pipe'],windowsHide:true});
    let output=''; for(const stream of [child.stdout,child.stderr]) stream.on('data',data=>{output=(output+data).slice(-8000);});
    let ready=false;
    for(let i=0;i<150;i++) { try {if((await fetch(base+'/healthz')).ok){ready=true;break;}}catch{} await new Promise(resolve=>setTimeout(resolve,100)); }
    assert.ok(ready,'Demo server did not start: '+output);
    const anonymous = await request.newContext({baseURL:base}); contexts.push(anonymous);
    const status = await (await anonymous.get('/api/setup/status')).json();
    assert.equal(status.accessEnabled,true); assert.equal(status.demo,true);
    assert.equal((await anonymous.get('/api/setup/config')).status(),401);
    const payload={token:'never-a-real-token',scopes:['repo:example/project'],origin:base,allowHttp:true};
    for(const role of ['viewer','operator','admin']) {
      const api = await request.newContext({baseURL:base}); contexts.push(api);
      let session = await (await api.get('/api/access/session')).json();
      assert.equal((await api.post('/api/access/login',{headers:{'X-RR-CSRF':session.csrfToken},data:{username:role,password}})).status(),200);
      session = await (await api.get('/api/access/session')).json();
      const headers={'X-RR-CSRF':session.csrfToken,Origin:base};
      assert.equal((await api.get('/api/setup/config')).status(),role==='admin'?200:403);
      if(role!=='admin') {assert.equal((await api.post('/api/setup/save',{headers,data:payload})).status(),403);continue;}
      assert.equal((await api.post('/api/setup/save',{data:payload})).status(),403,'CSRF required');
      assert.equal((await api.post('/api/setup/save',{headers:{...headers,Origin:'https://other.example'},data:payload})).status(),403,'Cross-origin rejected');
      assert.equal((await api.post('/api/setup/save',{headers,form:payload})).status(),403,'Forms rejected');
      assert.equal((await api.post('/api/setup/save',{headers,data:{...payload,token:'x'.repeat(33000)}})).status(),413,'Oversized body rejected');
      const discovery=await api.post('/api/setup/discover',{headers,data:{}});assert.equal(discovery.status(),200);
      assert.ok((await discovery.json()).repositories.length>0);
      const save=await api.post('/api/setup/save',{headers,data:payload});assert.equal(save.status(),200);
      assert.equal((await save.json()).restartRequired,false,'Demo never needs a real restart');
      await assert.rejects(fs.stat(path.join(directory,'ui-settings.json')),{code:'ENOENT'});
      await assert.rejects(fs.stat(path.join(directory,'management-setup.token')),{code:'ENOENT'});
    }
    assert.ok(!output.includes('never-a-real-token')&&!output.includes(password),'Secrets must not enter logs');
    browser=await chromium.launch({channel:'chrome',headless:true});
    const page=await browser.newPage({viewport:{width:1200,height:950}}), errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.goto(base+'/login.html');await page.locator('#login-username').fill('admin');await page.locator('#login-password').fill(password);await page.locator('#login-submit').click();
    await page.waitForURL(base+'/');await page.goto(base+'/#management');
    await page.getByRole('button',{name:'Preview setup',exact:true}).click();
    await page.getByRole('button',{name:'Find repositories',exact:true}).click();
    await page.getByRole('checkbox',{name:'example/activities-api · Private'}).check();
    const artifacts=path.resolve(__dirname,'../artifacts');await fs.mkdir(artifacts,{recursive:true});
    await page.screenshot({path:path.join(artifacts,'management-setup-desktop.png')});
    await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(artifacts,'management-setup-mobile.png')});
    assert.equal(await page.locator('#setup-dialog').evaluate(d=>d.scrollWidth<=d.clientWidth),true);
    await page.locator('#setup-next').click();await page.locator('#setup-next').click();await page.locator('#setup-done').waitFor();
    assert.equal(await page.locator('#setup-restart').isVisible(),false);
    await page.locator('#setup-next').click();await page.getByRole('button',{name:'Preview setup',exact:true}).click();await page.locator('#setup-github').waitFor();
    assert.equal(await page.locator('#setup-restart').isVisible(),false);assert.deepEqual(errors,[]);
    console.log('PASS: live setup HTTP authentication, roles, CSRF, origin, body limits, no secret persistence, full dashboard preview, mobile sizing and repeat preview.');
  } finally {
    if(browser) await browser.close();
    for(const context of contexts) await context.dispose();
    if(child && child.exitCode===null) {const stopped=new Promise(resolve=>child.once('exit',resolve));child.kill();await stopped;}
    await fs.rm(directory,{recursive:true,force:true});
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
