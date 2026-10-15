// Isolated UI tests using the real setup markup, CSS, and access wrapper; no GitHub calls or server changes.
const {chromium} = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), http = require('node:http');
const root = path.resolve(__dirname, '../src/RunnerRoom/wwwroot');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const index = read('index.html');
const callout = index.slice(index.indexOf('        <div id="management-setup"'), index.indexOf('        <div id="management-content"'));
const html = `<!doctype html><html data-theme="dark"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/setup.css"><body><span id="connection"></span><div id="access-session"><span id="access-identity"></span><button id="access-logout">Sign out</button></div>${callout}<script src="/access.js"></script><script src="/setup.js"></script></body></html>`;
let mode = 'bootstrap', badDiscovery = false, expire = false, calls = [];
const server = http.createServer(async (req,res) => {
  const send = (status, body) => {res.writeHead(status, {'Content-Type':'application/json'});res.end(JSON.stringify(body));};
  if(req.url.startsWith('/api/')) {
    const demo = mode === 'demo', accessEnabled = ['admin','viewer'].includes(mode);
    if(req.url === '/api/access/session') return send(200,{enabled:accessEnabled,authenticated:true,canAdmin:mode === 'admin',name:'Tester',role:mode,csrfToken:'test-csrf'});
    if(req.url === '/api/setup/status') return send(200,{available:!['pending','blocked'].includes(mode),demo,accessEnabled,managementEnabled:demo,pendingRestart:mode === 'pending',requiresCode:!accessEnabled&&!demo,unavailableReason:mode === 'blocked'?'This deployment uses an explicit settings override.':null});
    if(!demo&&!accessEnabled&&req.headers['x-rr-setup-code'] !== 'valid-code') return send(403,{message:'The setup code is invalid or expired.'});
    if(req.url === '/api/setup/config') return send(200,{demo,accessEnabled,origin:`http://127.0.0.1:${server.address().port}`,rootDirectory:'/var/lib/runner-room/runners',scopes:[],issues:[]});
    let body = '';for await(const chunk of req) body+=chunk;
    calls.push({path:req.url,headers:req.headers,body:JSON.parse(body||'{}')});
    assert.equal(req.headers['x-rr-csrf'],'test-csrf');
    if(req.url === '/api/setup/discover') return badDiscovery ? send(503,{message:'GitHub could not be reached. Try again.'}) : send(200,{repositories:[{fullName:'owner/project',isPrivate:true},{fullName:'owner/<img src=x>',isPrivate:false}],partial:false});
    if(req.url === '/api/setup/save') return expire ? send(403,{message:'The setup code expired. Generate another code.'}) : send(200,{saved:true,restartRequired:!demo,message:demo?'Preview complete. No settings were changed.':'Settings saved. Restart to apply.'});
    return send(404,{});
  }
  const files = {'/':html,'/app.css':read('app.css'),'/setup.css':read('setup.css'),'/setup.js':read('setup.js'),'/access.js':read('access.js')};
  if(!Object.hasOwn(files,req.url)){res.writeHead(404);return res.end();}
  res.writeHead(200,{'Content-Type':req.url.endsWith('.js')?'text/javascript':req.url.endsWith('.css')?'text/css':'text/html'});res.end(files[req.url]);
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({channel:'chrome',headless:true});
  try {
    const context=await browser.newContext(), page=await context.newPage(), errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    const base=`http://127.0.0.1:${server.address().port}`;
    const visible=id=>page.locator('#setup-'+id).isVisible();
    await page.goto(base); await page.getByRole('button',{name:'Set up management',exact:true}).click();
    await page.locator('#setup-verify').waitFor();
    await page.locator('#setup-code').fill('wrong-code'); await page.locator('#setup-next').click();
    await page.getByRole('alert').filter({hasText:'invalid or expired'}).waitFor();
    await page.locator('#setup-code').fill('valid-code'); await page.locator('#setup-next').click();
    await page.locator('#setup-account').waitFor();
    await page.locator('#setup-username').fill('igor');await page.locator('#setup-password').fill('test-password-only');await page.locator('#setup-confirm').fill('mismatch');
    await page.locator('#setup-next').click(); assert.match(await page.locator('#setup-error').textContent(),/do not match/);
    await page.locator('#setup-confirm').fill('test-password-only');await page.locator('#setup-next').click();assert.match(await page.locator('#setup-error').textContent(),/acknowledge/);
    await page.locator('#setup-allow-http').check();await page.locator('#setup-next').click();
    await page.locator('#setup-token').fill('fixture-token-only');badDiscovery=true;await page.locator('#setup-discover').click();
    await page.getByRole('alert').filter({hasText:'could not be reached'}).waitFor();assert.equal(await page.locator('#setup-token').inputValue(),'fixture-token-only');
    badDiscovery=false;await page.locator('#setup-discover').click();await page.getByRole('checkbox',{name:'owner/project · Private'}).check();
    assert.equal(await page.locator('#setup-repositories img').count(),0);
    await page.locator('#setup-filter').fill('project');assert.equal(await page.locator('#setup-repositories label:visible').count(),1);
    await page.locator('#setup-next').click();assert.equal(await visible('review-step'),true);
    assert.ok(!(await page.locator('#setup-review').textContent()).includes('fixture-token-only'));
    expire=true;await page.locator('#setup-next').click();await page.locator('#setup-verify').waitFor();
    await page.locator('#setup-code').fill('valid-code');await page.locator('#setup-next').click();await page.locator('#setup-review-step').waitFor();
    expire=false;await page.locator('#setup-next').click();await page.locator('#setup-done').waitFor();
    const save=calls.filter(c=>c.path.endsWith('/save')).at(-1);assert.deepEqual(save.body.scopes,['repo:owner/project']);assert.equal(save.body.password,'test-password-only');assert.equal(save.headers['x-rr-setup-code'],'valid-code');
    assert.equal(await visible('restart'),true);
    for(const id of ['token','password','confirm','code'])assert.equal(await page.locator('#setup-'+id).inputValue(),'');
    assert.deepEqual(await page.evaluate(()=>[Object.keys(localStorage),Object.keys(sessionStorage)]),[[],[]]);
    await page.locator('#setup-next').click();assert.equal(await page.locator('#setup-dialog').isVisible(),false);
    mode='pending';await page.reload();await page.getByRole('button',{name:'Finish setup',exact:true}).click();await page.locator('#setup-done').waitFor();assert.equal(await visible('verify'),false);
    mode='admin';await page.reload();await page.locator('#setup-open').click();await page.locator('#setup-github').waitFor();assert.equal(await visible('account'),false);
    await page.locator('#setup-token').fill('admin-token');await page.locator('#setup-discover').click();assert.match(await page.locator('#setup-error').textContent(),/acknowledge/);
    await page.locator('#setup-close').click();assert.equal(await page.locator('#setup-token').inputValue(),'');
    mode='demo';await page.setViewportSize({width:390,height:844});await page.reload();await page.getByRole('button',{name:'Preview setup'}).click();
    await page.locator('#setup-github').waitFor();assert.equal(await visible('token-label'),false);await page.locator('#setup-discover').click();await page.getByRole('checkbox',{name:'owner/project · Private'}).check();
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    assert.equal(await page.locator('#setup-dialog').evaluate(d=>d.scrollWidth<=d.clientWidth),true);
    await page.locator('#setup-next').click();await page.locator('#setup-next').click();await page.locator('#setup-done').waitFor();assert.equal(await visible('restart'),false);
    mode='viewer';await page.reload();await page.waitForTimeout(100);assert.equal(await page.locator('#management-setup').isVisible(),false);
    mode='blocked';await page.reload();await page.getByText('A server change is needed first').waitFor();assert.equal(await page.locator('#setup-open').isVisible(),false);assert.match(await page.locator('#setup-callout-hint').textContent(),/explicit settings override/);
    assert.deepEqual(errors,[]);
    console.log('PASS: ownership verification, login validation, HTTP consent, repository discovery recovery, safe rendering, expired-code recovery, pending restart, administrator/demo/viewer flows, mobile sizing, CSRF headers and credential clearing.');
  } finally {await browser.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
