// Requires Docker + Compose 2.24.4+. Runs only disposable local containers/volumes.
const {execFileSync} = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const project = 'rr-deployment-' + Date.now();
const work = path.join(root, 'artifacts', project);
fs.mkdirSync(work, {recursive:true});
function docker(args, options={}) {
  return execFileSync('docker', args, {cwd:root, encoding:'utf8', stdio:['ignore','pipe','pipe'], ...options});
}
const chart = 'deploy/helm/runner-room';
function helm(args) { return docker(['run','--rm','-v',root+':/work','-w','/work','alpine/helm:3.17.3',...args]); }
function rejected(args) { assert.throws(() => helm(['template','checks',chart,...args])); }
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
(async () => {
  console.log(helm(['lint',chart,'--strict']).trim());
  const demo = helm(['template','checks',chart]);
  assert.ok(!demo.includes('hostPID: true'));
  assert.ok(demo.includes('helm.sh/resource-policy: keep'));
  const real = helm(['template','checks',chart,'-f',chart+'/values-linux.example.yaml']);
  assert.ok(real.includes('hostPID: true') && real.includes('type: Recreate'));
  const custom = helm(['template','checks',chart,'--set','existingSettingsSecret=settings,credentialsSecret=tokens,credentialKeys[0]=github.token,persistence.existingClaim=existing,ingress.enabled=true']);
  assert.ok(!custom.includes('kind: ConfigMap') && !custom.includes('kind: PersistentVolumeClaim'));
  assert.ok(custom.includes('kind: Ingress') && custom.includes('subPath: "github.token"'));
  const ephemeral = helm(['template','checks',chart,'--set','persistence.enabled=false']);
  assert.ok(!ephemeral.includes('kind: PersistentVolumeClaim') && ephemeral.includes('emptyDir: {}'));
  rejected(['--set','demo=false']);
  rejected(['--set','hostMonitoring.enabled=true']);
  rejected(['--set','runAsUser=0']);
  rejected(['--set','credentialKeys[0]=../token']);
  rejected(['-f',chart+'/values-linux.example.yaml','--set','hostMonitoring.runnerPaths[0]=/etc']);
  console.log('PASS: Helm lint, deployment variants, persistence, secrets and invalid configuration checks.');

  docker(['build','-t','runner-room:deployment-check','.']);
  docker(['build','-f','tests/management.Dockerfile','-t','runner-room:deployment-fixture','.']);
  const runners = '/srv/' + project;
  const fixtureVolume = project+'-runners';
  const fixtureContainer = project+'-listener';
  const namespaceContainer = project+'-processes';
  fs.writeFileSync(path.join(work,'settings.json'), JSON.stringify({Access:{Enabled:false},Alerts:{Enabled:false}}));
  // Share a disposable PID namespace instead of the CI host's processes. Its own
  // GitHub runner may use another UID, correctly making process status unknown.
  // Keep the namespace alive when the listener stops so we can check offline status.
  // Service identity, health, security and state initialization settings stay real.
  fs.writeFileSync(path.join(work,'override.yaml'), `services:
  dashboard:
    pid: "container:${namespaceContainer}"
    ports: !override ["127.0.0.1::8080"]
    volumes: !override
      - type: volume
        source: fixture
        target: ${runners}
        read_only: true
      - type: bind
        source: ${JSON.stringify(work)}
        target: /etc/runner-room
        read_only: true
      - state:/var/lib/runner-room
volumes:
  fixture:
    external: true
    name: ${fixtureVolume}
`);
  const env = {...process.env,RUNNER_UID:'1001',RUNNER_GID:'1001',RUNNERS_ROOT:runners,RUNNER_ROOM_IMAGE:'runner-room:deployment-check'};
  const composeArgs = ['compose','-p',project,'-f','deploy/compose/compose.yaml','-f',path.join(work,'override.yaml')];
  const compose = args => docker([...composeArgs,...args], {env});
  // The production deployment must still monitor host processes, not just itself.
  const production = JSON.parse(docker(['compose','-f','deploy/compose/compose.yaml','config','--format','json'], {env}));
  assert.equal(production.services.dashboard.pid,'host');
  try {
    docker(['volume','create',fixtureVolume]);
    docker(['run','--rm','--user','0','-v',fixtureVolume+':'+runners,'--entrypoint','sh','runner-room:deployment-fixture','-ec',
      `mkdir -p ${runners}/test/bin; cp /checks/runner-stub ${runners}/test/bin/Runner.Listener; printf '{"agentName":"compose-fixture"}' > ${runners}/test/.runner; chown -R 1001:1001 ${runners}`]);
    docker(['run','-d','--name',namespaceContainer,'--user','1001:1001',
      '--entrypoint','sleep','runner-room:deployment-fixture','infinity']);
    docker(['run','-d','--name',fixtureContainer,'--pid=container:'+namespaceContainer,'--user','1001:1001','-v',fixtureVolume+':'+runners,
      '--entrypoint',runners+'/test/bin/Runner.Listener','runner-room:deployment-fixture','run']);
    compose(['config','--quiet']);
    compose(['up','-d','--no-build','--wait','--wait-timeout','90']);
    const address = compose(['port','dashboard','8080']).trim();
    const base = 'http://' + address;
    assert.equal((await (await fetch(base+'/healthz')).json()).status,'ok');
    const inventory = await (await fetch(base+'/api/runners')).json();
    assert.equal(inventory.runners.length,1);
    assert.equal(inventory.runners[0].name,'compose-fixture');
    assert.equal(inventory.runners[0].processStatus,'running');
    assert.equal(inventory.runners[0].status,'idle',
      `Expected an idle fixture; process=${inventory.runners[0].processStatus}, warning=${inventory.warning}`);
    assert.match(await (await fetch(base)).text(),/Runner Room/);
    assert.throws(() => compose(['exec','-T','dashboard','touch',runners+'/should-not-write']));
    compose(['exec','-T','dashboard','sh','-ec','test -s /var/lib/runner-room/monitoring.json; echo preserved > /var/lib/runner-room/deployment-check']);
    compose(['up','-d','--no-build','--force-recreate','--wait','--wait-timeout','90','dashboard']);
    assert.equal(compose(['exec','-T','dashboard','cat','/var/lib/runner-room/deployment-check']).trim(),'preserved');
    docker(['stop',fixtureContainer]);
    await sleep(4000);
    const after = await (await fetch('http://'+compose(['port','dashboard','8080']).trim()+'/api/runners')).json();
    assert.equal(after.runners[0].status,'offline',
      `Expected an offline fixture; process=${after.runners[0].processStatus}, warning=${after.warning}`);
    console.log('PASS: Compose host PID configuration, healthy startup, shared-process runner idle/offline detection, static assets, read-only runner mount and persistent state after recreation.');
  } finally {
    try { compose(['down','--volumes','--remove-orphans']); } catch {}
    try { docker(['rm','-f',fixtureContainer]); } catch {}
    try { docker(['rm','-f',namespaceContainer]); } catch {}
    try { docker(['volume','rm',fixtureVolume]); } catch {}
  }
})().catch(error => { console.error(error.stack || error.message, error.stdout?.toString() || '', error.stderr?.toString() || ''); process.exitCode=1; });
