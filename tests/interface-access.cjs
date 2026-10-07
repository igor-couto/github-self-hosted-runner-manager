// Against an isolated demo with local reader/admin accounts and test-only-password-42.
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
  await page.locator('#access-logout').click(); await page.waitForURL(base+'/login.html');
  assert.equal((await context.request.get(base+'/api/runners')).status(),401);
  await page.locator('#login-username').fill('admin'); await page.locator('#login-password').fill('test-only-password-42'); await page.locator('#login-submit').click();
  await page.waitForURL(base+'/'); assert.equal((await context.request.get(base+'/api/quotas')).status(),200);
  const cached=await page.evaluate(async()=> (await Promise.all((await caches.keys()).map(async k => (await (await caches.open(k)).keys()).map(r=>new URL(r.url).pathname)))).flat());
  assert.ok(!cached.some(p=>p.startsWith('/api/')||p==='/index.html'||p==='/'));
  console.log('PASS: quota permissions, viewer details, log denial, logout, and no sensitive service-worker caching.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
