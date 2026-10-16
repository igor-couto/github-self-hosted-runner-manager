// Runs isolated authenticated and open-LAN demos: no real tokens, runners, or GitHub requests.
const {request,chromium} = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const crypto = require('node:crypto');
const {spawn} = require('node:child_process');

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
    let output='';
    async function startServer() {
      child = spawn('dotnet',['bin/Release/net9.0/RunnerRoom.dll','--SettingsFile',file,'--urls',base],{cwd,stdio:['ignore','pipe','pipe'],windowsHide:true});
      for(const stream of [child.stdout,child.stderr]) stream.on('data',data=>{output=(output+data).slice(-8000);});
      let ready=false;
      for(let i=0;i<150;i++) { try {if((await fetch(base+'/healthz')).ok){ready=true;break;}}catch{} await new Promise(resolve=>setTimeout(resolve,100)); }
      assert.ok(ready,'Demo server did not start: '+output);
    }
    await startServer();
    const anonymous = await request.newContext({baseURL:base}); contexts.push(anonymous);
    const status = await (await anonymous.get('/api/setup/status')).json();
    assert.equal(status.accessEnabled,true); assert.equal(status.demo,true);
    assert.equal((await anonymous.get('/api/setup/config')).status(),401);
    const payload={token:'never-a-real-token',scopes:['repo:example/project']};
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
    await page.close();
    const stopped=new Promise(resolve=>child.once('exit',resolve));child.kill();await stopped;
    await fs.writeFile(file,JSON.stringify({Demo:true,Monitoring:{StateDirectory:directory},Access:{Enabled:false}}));
    await startServer();
    const lan=await request.newContext({baseURL:base});contexts.push(lan);
    const lanStatus=await (await lan.get('/api/setup/status')).json();
    assert.equal(lanStatus.accessEnabled,false);assert.equal(lanStatus.available,true);
    assert.equal(lanStatus.requiresCode,undefined,'No ownership-code requirement remains');
    assert.equal((await lan.get('/api/setup/config')).status(),200,'LAN setup opens without authentication or a setup code');
    assert.equal((await lan.post('/api/setup/discover',{data:{}})).status(),200);
    assert.equal((await lan.post('/api/setup/save',{headers:{Origin:'https://other.example'},data:payload})).status(),403);
    assert.equal((await lan.post('/api/setup/save',{form:payload})).status(),403);
    const lanSave=await lan.post('/api/setup/save',{headers:{Origin:base},data:payload});
    assert.equal(lanSave.status(),200);assert.equal((await lanSave.json()).restartRequired,false);
    const lanPage=await browser.newPage({viewport:{width:1200,height:950}});
    lanPage.on('pageerror',e=>errors.push(e.message));
    await lanPage.goto(base+'/#management');
    await lanPage.getByRole('button',{name:'Preview setup',exact:true}).click();
    await lanPage.locator('#setup-github').waitFor();
    assert.equal(await lanPage.locator('#setup-verify, #setup-account, #setup-code, #setup-password').count(),0);
    await lanPage.getByRole('button',{name:'Find repositories',exact:true}).click();
    await lanPage.getByRole('checkbox',{name:'example/activities-api · Private'}).check();
    await lanPage.locator('#setup-next').click();await lanPage.locator('#setup-next').click();
    await lanPage.locator('#setup-done').waitFor();
    assert.equal(await lanPage.locator('#setup-restart').isVisible(),false);
    await assert.rejects(fs.stat(path.join(directory,'ui-settings.json')),{code:'ENOENT'});
    assert.deepEqual(errors,[]);
    assert.ok(!output.includes('never-a-real-token')&&!output.includes(password),'Secrets must not enter logs');
    console.log('PASS: open-LAN setup without verification/login; existing authentication, roles and CSRF; origin/body validation; no demo persistence; dashboard preview and mobile sizing.');
  } finally {
    if(browser) await browser.close();
    for(const context of contexts) await context.dispose();
    if(child && child.exitCode===null) {const stopped=new Promise(resolve=>child.once('exit',resolve));child.kill();await stopped;}
    await fs.rm(directory,{recursive:true,force:true});
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
