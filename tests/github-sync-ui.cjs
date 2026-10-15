// Isolated browser checks: real dashboard markup, rendering helpers, access wrapper and sync UI.
// Only the GitHub API is simulated; no credential or external service is needed.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const webroot = path.resolve(__dirname, '../src/RunnerRoom/wwwroot');
const read = name => fs.readFileSync(path.join(webroot, name), 'utf8');
const index = read('index.html');
const panel = index.slice(index.indexOf('    <section id="workspace-integration"'), index.indexOf('    <section id="workspace-management"')).replace('aria-labelledby="workspace-tab-integration" hidden', 'aria-labelledby="workspace-tab-integration"');
const app = read('app.js');
const helpers = 'const $=id=>document.getElementById(id);' + app.slice(app.indexOf('function element('), app.indexOf('function duration(')) + '\nfunction dateTime(value){return value?new Date(value).toLocaleString():"Not yet";}\n' + read('monitoring.js').split('function renderCpuMetrics()')[0];
const html = `<!doctype html><html data-theme="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/github-sync.css"></head><body><main><button id="refresh">Refresh</button><span id="connection"></span><div id="access-session" hidden><span id="access-identity"></span><button id="access-logout">Sign out</button></div>${panel}</main><script src="/helpers.js"></script><script src="/access.js"></script><script src="/github-sync.js"></script></body></html>`;
const now = '2026-10-11T10:00:00Z';
const repository = {fullName:'sample/project', selected:true, isPrivate:true, lastSyncedAt:now};
const run = {id:123, attempt:2, repository:repository.fullName, name:'Build <img src=x onerror=alert(1)>', status:'completed', conclusion:'success', branch:'main', actor:'maintainer', commit:'a'.repeat(40), event:'push', createdAt:now, updatedAt:now, startedAt:now, url:'javascript:alert(1)', jobs:[{id:456, name:'Linux build', status:'completed', conclusion:'success', runnerName:'pi5', labels:['self-hosted','ARM64'], startedAt:now, completedAt:now, steps:[{number:1, name:'Build app', status:'completed', conclusion:'success', startedAt:now, completedAt:now}]}]};
const initial = () => ({enabled:true, configured:true, demo:false, repositories:[structuredClone(repository)], runs:[structuredClone(run)], progress:{running:false}, history:[], api:{remaining:4950,limit:5000,resetsAt:now}, historyDays:30,maxRunsPerRepository:100,intervalMinutes:5,nextSyncAt:now});
let state = initial(), role = 'admin', fail = false, calls = [];
const server = http.createServer(async (req,res) => {
  const send = (status, body) => {res.writeHead(status, {'Content-Type':'application/json'});res.end(JSON.stringify(body));};
  if(req.url.startsWith('/api/')) {
    if(req.url === '/api/github/events') {res.writeHead(200,{'Content-Type':'text/event-stream'});res.write(': connected\n\n');return;}
    if(req.url === '/api/access/session') return send(200,{enabled:true,authenticated:true,name:'Test user',role,canAdmin:role==='admin',canOperate:role!=='viewer',csrfToken:'test-csrf'});
    if(req.method !== 'GET') {
      let body='';for await(const chunk of req) body+=chunk;
      calls.push({url:req.url,body:JSON.parse(body||'{}'),csrf:req.headers['x-rr-csrf']});
      if(req.headers['x-rr-csrf'] !== 'test-csrf') return send(400,{message:'Missing CSRF token.'});
      if(req.url === '/api/github/discover') return send(200,{repositories:[repository,{fullName:'sample/second',selected:false,isPrivate:false}],partial:false});
      if(req.url === '/api/github/selection') {const config=JSON.parse(body);state.repositories=config.repositories.map(fullName=>({...repository,fullName}));state.historyDays=config.historyDays;state.maxRunsPerRepository=config.maxRunsPerRepository;return send(200,{});}
      if(req.url === '/api/github/sync') {state.progress={running:true,repository:repository.fullName,completedRepositories:0,totalRepositories:state.repositories.length};return send(202,{});}
      if(req.url === '/api/github/cancel') {state.progress={running:false};return send(200,{});}
    }
    if(req.url === '/api/github/sync') return send(fail?503:200,fail?{message:'GitHub is temporarily unavailable.'}:state);
    if(req.url.startsWith('/api/github/workflows')) return send(200,{workflows:[{file:'.github/workflows/build.yml',name:'Build',jobs:[{id:'build',name:'Build',labels:['self-hosted','ARM64'],dynamic:false,selfHosted:true}]}],warnings:['Expression-based labels need a manual check.']});
    return send(404,{});
  }
  const files = {'/':html,'/helpers.js':helpers,'/app.css':read('app.css'),'/github-sync.css':read('github-sync.css'),'/access.js':read('access.js'),'/github-sync.js':read('github-sync.js')};
  if(!Object.hasOwn(files,req.url)) {res.writeHead(404);return res.end();}
  res.writeHead(200,{'Content-Type':req.url.endsWith('.css')?'text/css':req.url.endsWith('.js')?'text/javascript':'text/html'});res.end(files[req.url]);
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const base=`http://127.0.0.1:${server.address().port}`;
  const browser=await chromium.launch({channel:'chrome',headless:true});
  try {
    const context=await browser.newContext({viewport:{width:1360,height:1000}}),page=await context.newPage(),errors=[];
    page.on('pageerror',error=>{errors.push(error.message);console.error(error.message);});
    await page.goto(base);await page.locator('#sync-runs-list button').waitFor();
    assert.equal(await page.locator('#sync-runs-list img').count(),0);
    assert.match(await page.locator('#sync-runs-list').textContent(),/Build <img/);
    await page.getByRole('button',{name:'View jobs',exact:true}).click();
    assert.match(await page.locator('#sync-run-body').textContent(),/Linux build/);
    await page.locator('#sync-run-body summary').click();assert.match(await page.locator('#sync-run-body').textContent(),/Build app/);
    assert.equal(await page.locator('#sync-run-body a').first().getAttribute('href'),'https://github.com/sample/project/actions/runs/123');
    await page.locator('#sync-run-close').click();
    await page.locator('#sync-choose').click();await page.locator('#sync-discover').click();
    await page.locator('.sync-choice').filter({hasText:'sample/second'}).locator('input').waitFor();await page.locator('.sync-choice').filter({hasText:'sample/second'}).locator('input').check();
    await page.locator('#sync-refresh').click();await page.waitForFunction(()=>!document.getElementById('sync-refresh').disabled);
    assert.equal(await page.locator('.sync-choice').filter({hasText:'sample/second'}).locator('input').isChecked(),true,'refresh preserves unsaved selection');
    await page.locator('#sync-repo-search').fill('second');assert.equal(await page.locator('.sync-choice').count(),1);
    await page.locator('#sync-picker summary').click();await page.locator('#sync-days').selectOption('7');await page.locator('#sync-limit').fill('25');
    await page.locator('#sync-save').click();await page.locator('#sync-picker').waitFor({state:'hidden'});
    assert.deepEqual(calls.find(c=>c.url.endsWith('/selection')).body,{repositories:['sample/project','sample/second'],historyDays:7,maxRunsPerRepository:25});
    await page.locator('#sync-now').click();await page.locator('#sync-cancel').waitFor();assert.match(await page.locator('#sync-progress').textContent(),/0 of 2/);
    await page.locator('#sync-cancel').click();await page.locator('#sync-cancel').waitFor({state:'hidden'});
    assert.ok(calls.every(c=>c.csrf==='test-csrf'));
    await page.getByRole('button',{name:'Runner requirements',exact:true}).first().click();await page.waitForFunction(()=>document.getElementById('sync-workflow-body').textContent.includes('Self-hosted runner required'));
    assert.match(await page.locator('#sync-workflow-body').textContent(),/ARM64/);await page.locator('#sync-workflow-close').click();
    await page.locator('#sync-run-result').selectOption('failure');assert.match(await page.locator('#sync-runs-list').textContent(),/No workflow runs match/);await page.locator('#sync-run-result').selectOption('');
    fail=true;await page.locator('#sync-refresh').click();await page.waitForFunction(()=>document.getElementById('sync-message').textContent.includes('temporarily unavailable'));assert.equal(await page.locator('#sync-runs-list button').count(),1);fail=false;
    await page.locator('#sync-refresh').click();await page.locator('#sync-message').waitFor({state:'hidden'});
    fs.mkdirSync('artifacts',{recursive:true});await page.screenshot({path:'artifacts/github-sync-desktop.png',fullPage:true});
    await page.setViewportSize({width:390,height:844});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);await page.screenshot({path:'artifacts/github-sync-mobile.png',fullPage:true});
    role='viewer';await page.reload();await page.locator('#sync-runs-list button').waitFor();assert.equal(await page.locator('#sync-choose').isVisible(),false);assert.equal(await page.locator('#sync-now').isVisible(),false);assert.equal(await page.getByRole('button',{name:'Runner requirements',exact:true}).count(),0);
    role='operator';await page.reload();await page.locator('#sync-now').waitFor();assert.equal(await page.locator('#sync-choose').isVisible(),false);assert.equal(await page.locator('#sync-now').isEnabled(),true);
    role='admin';state={...initial(),configured:false,repositories:[],runs:[]};await page.reload();await page.locator('#sync-setup').waitFor();assert.equal(await page.locator('#sync-choose').isEnabled(),false);assert.equal(await page.locator('input[type=password]').count(),0);
    await page.locator('#sync-setup-admin summary').click();assert.match(await page.locator('#sync-setup-admin').textContent(),/GitHub.App.PrivateKeyFile/);
    assert.deepEqual(errors,[]);console.log('PASS: GitHub sync selection, filtering, job steps, safe links/text, CSRF, progress/cancellation, errors/recovery, role visibility, setup and mobile layout.');
  } finally {await browser.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;server.closeAllConnections();server.close();});




