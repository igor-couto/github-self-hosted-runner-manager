// Run against a local demo server. Release results are deterministic fixtures, not live GitHub calls.
const {chromium}=require('playwright');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1100}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  let data={enabled:true,demo:false,currentVersion:'0.9.0',latestVersion:'0.10.0',status:'update_available',updateAvailable:true,releaseUrl:'https://github.com/igor-couto/github-self-hosted-runner-manager/releases/tag/v0.10.0',checkedAt:'2026-10-10T12:00:00Z',lastAttemptAt:'2026-10-10T12:00:00Z',stale:false,message:'A newer release is available.'},fail=false;
  await page.route('**/api/updates',route=>fail?route.fulfill({status:503,body:'Unavailable'}):route.fulfill({json:data}));
  await page.goto(process.env.RUNNER_ROOM_TEST_URL||'http://127.0.0.1:8080');
  await page.locator('#app-update-indicator').waitFor();
  assert.equal(await page.locator('#app-installed-version').textContent(),'0.9.0');
  assert.equal(await page.locator('#app-release-link').getAttribute('href'),data.releaseUrl);
  async function refresh(text){await page.locator('#refresh').click();await page.waitForFunction(t=>document.getElementById('app-update-status').textContent===t,text);}
  data={...data,status:'up_to_date',updateAvailable:false,latestVersion:'0.9.0'};await refresh('Up to date');assert.equal(await page.locator('#app-update-indicator').isVisible(),false);
  data={...data,status:'unavailable',updateAvailable:true,stale:true,message:'Rate limited.'};await refresh('Update check unavailable · last result is stale');assert.equal(await page.locator('#app-update-indicator').isVisible(),false);assert.match(await page.locator('#app-update-checked').textContent(),/Last successful/);
  data={...data,stale:false,status:'disabled',updateAvailable:null,latestVersion:null,releaseUrl:null,checkedAt:null};await refresh('Update checks disabled');assert.equal(await page.locator('#app-release-link').isVisible(),false);
  data={...data,status:'development',currentVersion:'0.0.0-dev',message:'Development build'};await refresh('Development build');
  data={...data,status:'update_available',updateAvailable:true,releaseUrl:'https://github.com/attacker/repo/releases/tag/v1.0.0'};await refresh('Update available');assert.equal(await page.locator('#app-release-link').isVisible(),false);
  fail=true;await refresh('Update status unavailable');assert.match(await page.locator('#app-update-message').textContent(),/out of date/);
  fail=false;data={...data,status:'update_available',releaseUrl:'https://github.com/igor-couto/github-self-hosted-runner-manager/releases/tag/v0.10.0'};await refresh('Update available');
  await page.setViewportSize({width:390,height:844});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
  assert.deepEqual(errors,[]);console.log('PASS: update indicator, current/latest versions, stale/disabled/development/errors, trusted release links and mobile layout.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
