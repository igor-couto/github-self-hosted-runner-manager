// Against an isolated demo with local reader/operator/admin accounts and test-only-password-42.
const {chromium}=require('playwright'); const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true});
 try {
  const context=await browser.newContext(),page=await context.newPage();
  const base=process.env.RUNNER_ROOM_AUTH_TEST_URL||'http://127.0.0.1:8081';
  assert.equal((await context.request.get(base+'/api/quotas')).status(),401);
  await page.goto(base+'/login.html');
  await page.locator('#login-username').fill('reader'); await page.locator('#login-password').fill('test-only-password-42'); await page.locator('#login-submit').click();
  await page.waitForURL(base+'/'); await page.locator('.runner-row').first().waitFor();
  for (const path of ['/api/quotas','/API/QUOTAS/']) assert.equal((await context.request.get(base+path)).status(),403);
  const data=await (await context.request.get(base+'/api/runners')).json();
  await page.goto(base+'/#/runner/'+data.runners[0].id); await page.locator('#detail-content .runner-details').waitFor();
  assert.equal(await page.locator('#detail-content').getByRole('button',{name:'View logs',exact:true}).isVisible(),false);
  const readerSession=await (await context.request.get(base+'/api/access/session')).json();
  assert.equal(readerSession.canOperate,false); assert.equal(readerSession.canAdmin,false);
  assert.equal((await context.request.get(base+'/api/management/')).status(),403);
  assert.equal((await context.request.post(base+'/api/management/action',{headers:{'X-RR-CSRF':readerSession.csrfToken},data:{ids:['test'],action:'start'}})).status(),403);
  await page.locator('#access-logout').click(); await page.waitForURL(base+'/login.html');
  assert.equal((await context.request.get(base+'/api/runners')).status(),401);
  await page.locator('#login-username').fill('operator'); await page.locator('#login-password').fill('test-only-password-42'); await page.locator('#login-submit').click();
  await page.waitForURL(base+'/');
  const operatorSession=await (await context.request.get(base+'/api/access/session')).json();
  assert.equal(operatorSession.canOperate,true); assert.equal(operatorSession.canAdmin,false);
  const stateResponse=await context.request.get(base+'/api/management/'); assert.equal(stateResponse.status(),200);
  const state=await stateResponse.json(); assert.equal(state.demo,true,'Only run operator mutations against the isolated demo');
  const operation=async(path,data,headers={})=>context.request.post(base+'/api/management/'+path,{headers:{'X-RR-CSRF':operatorSession.csrfToken,...headers},data});
  for(const path of ['imports','groups/result']) assert.equal((await context.request.get(base+'/api/management/'+path)).status(),403);
  for(const path of ['create','import','configure','pool','scale','group','version','cancel']) assert.equal((await operation(path,{})).status(),403,path);
  for(const action of ['register','unregister','remove','update']) assert.equal((await operation('action',{ids:[state.runners[0].id],action})).status(),403,action);
  assert.equal((await context.request.post(base+'/api/management/action',{data:{ids:[state.runners[0].id],action:'start'}})).status(),403,'Operators still require CSRF');
  assert.equal((await operation('action',{ids:[state.runners[0].id],action:'start'},{Origin:'https://untrusted.example'})).status(),403,'Operator mutations still check origin');
  assert.equal((await operation('action',{ids:[state.runners[0].id],action:'start'})).status(),202);
  assert.equal((await operation('workflow',{repository:'owner/project',runId:42,action:'track'})).status(),202);
  assert.equal((await context.request.get(base+'/api/access/audit')).status(),403);
  assert.equal((await context.request.get(base+'/api/quotas')).status(),403);
  await page.goto(base+'/#management'); await page.locator('#management-runners table').waitFor();
  for(const id of ['management-new','management-editor','management-import','management-import-open','management-version']) assert.equal(await page.locator('#'+id).isVisible(),false,id);
  assert.deepEqual(await page.locator('#mg-action option:not([disabled])').evaluateAll(options=>options.map(o=>o.value)),['start','stop','restart','drain']);
  assert.equal(await page.locator('#management-workflow-form').isVisible(),true);
  await page.locator('#access-logout').click(); await page.waitForURL(base+'/login.html');
  await page.locator('#login-username').fill('admin'); await page.locator('#login-password').fill('test-only-password-42'); await page.locator('#login-submit').click();
  await page.waitForURL(base+'/'); assert.equal((await context.request.get(base+'/api/quotas')).status(),200);
  const cached=await page.evaluate(async()=> (await Promise.all((await caches.keys()).map(async k => (await (await caches.open(k)).keys()).map(r=>new URL(r.url).pathname)))).flat());
  assert.ok(!cached.some(p=>p.startsWith('/api/')||p==='/index.html'||p==='/'));
  console.log('PASS: viewer/operator/admin permissions, operation allowlist, operator UI, CSRF/origin enforcement, logout, and no sensitive service-worker caching.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
