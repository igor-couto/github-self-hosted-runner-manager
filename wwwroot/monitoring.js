"use strict";
let detailedSystem = null;
let monitoringLoading = false;
const percentText = n => Number.isFinite(n) ? `${n.toFixed(1)}%` : "Unavailable / sampling";
const rateText = n => Number.isFinite(n) ? `${formatBytes(n)}/s` : "Unavailable / sampling";
const boolText = n => n === true ? "Yes" : n === false ? "No" : "Unavailable";
function metricCard(title, value, caption) {
  const card = element("div", "monitor-card");
  card.append(element("span", "detail-label", title), element("strong", "monitor-value", value));
  if (caption) card.append(element("p", "detail-hint", caption));
  return card;
}
function monitorTable(headers, rows, emptyText = "No readable measurements available.") {
  const wrap = element("div", "monitor-table-wrap");
  const table = element("table", "monitor-table"); const head = element("thead"); const title = element("tr");
  for (const label of headers) { const th = element("th", "", label); th.scope = "col"; title.append(th); }
  head.append(title); table.append(head); const body = element("tbody");
  for (const row of rows) { const tr = element("tr"); for (const value of row) { const cell = element("td"); if (value instanceof Node) cell.append(value); else cell.textContent = value ?? "Unavailable"; tr.append(cell); } body.append(tr); }
  table.append(body); wrap.append(table);
  if (!rows.length) wrap.append(element("p", "detail-hint", emptyText));
  return wrap;
}
function renderCpuMetrics() {
  const data = detailedSystem; const section = $("monitor-cpu"); section.replaceChildren();
  const summary = element("div", "monitor-grid");
  for (const [key, title] of [["one","Load · 1 minute"],["five","Load · 5 minutes"],["fifteen","Load · 15 minutes"]])
    summary.append(metricCard(title, data.load ? data.load[key].toFixed(2) : "Unavailable", "Runnable or waiting tasks"));
  summary.append(metricCard("Swap", data.swap ? data.swap.totalBytes ? percentText(data.swap.usedPercent) : "Not configured" : "Unavailable", data.swap ? `${formatBytes(data.swap.usedBytes)} / ${formatBytes(data.swap.totalBytes)} used` : ""));
  section.append(summary, element("h3", "monitor-subheading", "Per-core CPU usage"));
  const cores = element("div", "core-grid");
  for (const core of data.cores) {
    const item = element("div", "core-reading"); const heading = element("div", "resource-heading");
    heading.append(element("span", "", core.name.toUpperCase()), element("strong", "", percentText(core.percent)));
    const meter = element("progress"); meter.max = 100; meter.value = core.percent || 0; meter.hidden = !Number.isFinite(core.percent); meter.setAttribute("aria-label", `${core.name} usage`);
    item.append(heading, meter); cores.append(item);
  }
  section.append(cores, element("p", "detail-hint", "Per-core usage is 0–100%. Load averages are task counts, not percentages. New cores need a second sample."));
}
function renderNetworkMetrics() {
  const section = $("monitor-network"); section.replaceChildren();
  section.append(monitorTable(["Interface", "Download", "Upload", "Today received", "Today sent", "Observed"], detailedSystem.network.map(n => [
    `${n.name} · ${n.state}`, rateText(n.downloadBytesPerSecond), rateText(n.uploadBytesPerSecond), formatBytes(n.today?.receivedBytes), formatBytes(n.today?.sentBytes), duration(n.today?.observedSeconds)
  ])));
  section.append(element("p", "detail-hint", "Today means observed traffic since UTC midnight. Downtime, resets and the interval crossing midnight are excluded. Interfaces are shown separately: adding bridges, virtual adapters and physical interfaces can double-count traffic."));
  section.append(element("h3", "monitor-subheading", "Daily observed totals · UTC"));
  section.append(monitorTable(["Date", "Interface", "Received", "Sent", "Coverage"], detailedSystem.trafficHistory.map(d => [d.date, d.interface, formatBytes(d.receivedBytes), formatBytes(d.sentBytes), duration(d.observedSeconds)])));
}
function storageChart(points, current, checkedAt) {
  const data = [...points, {at:checkedAt,usedBytes:current}].filter(p=>Number.isFinite(p.usedBytes));
  if (points.length < 2) return element("p", "detail-hint", "Collecting history… Storage samples are saved hourly.");
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 600 90"); svg.setAttribute("class", "storage-chart"); svg.setAttribute("role", "img");
  svg.setAttribute("aria-label", `Storage usage from ${formatBytes(data[0].usedBytes)} to ${formatBytes(data.at(-1).usedBytes)}`);
  const times=data.map(p=>Date.parse(p.at)), values=data.map(p=>p.usedBytes);
  const low=Math.min(...values), span=Math.max(1,Math.max(...values)-low), timeSpan=Math.max(1,times.at(-1)-times[0]);
  const line=document.createElementNS(svg.namespaceURI,"polyline");
  line.setAttribute("points",data.map((p,i)=>`${5+(times[i]-times[0])/timeSpan*590},${80-(p.usedBytes-low)/span*70}`).join(" "));
  svg.append(line); return svg;
}
function renderStorageMetrics() {
  const section=$("monitor-storage"); section.replaceChildren();
  section.append(element("h3","monitor-subheading","Disk activity"),monitorTable(["Device","Read","Write","Busy"],detailedSystem.diskActivity.map(d=>[d.device,rateText(d.readBytesPerSecond),rateText(d.writeBytesPerSecond),percentText(d.busyPercent)])));
  section.append(element("p","detail-hint","Whole-device activity. Layered devices (such as LVM and physical disks) are not added together. Busy time is not a throughput limit."));
  const grid=element("div","filesystem-grid");
  for(const disk of detailedSystem.fileSystems) {
    const card=metricCard(disk.mount,disk.usage ? `${formatBytes(disk.usage.usedBytes)} used` : "Unavailable",`${disk.device} · ${disk.type}`);
    if(disk.usage) {
      const meter=element("progress");meter.max=100;meter.value=disk.usage.usedPercent;meter.setAttribute("aria-label",`${disk.mount} storage used`);
      card.append(meter,element("p","detail-hint",`${formatBytes(disk.usage.availableBytes)} available / ${formatBytes(disk.usage.totalBytes)} total`));
      card.append(storageChart(disk.history,disk.usage.usedBytes,detailedSystem.checkedAt));
      if(disk.history.length) {
        const delta=disk.usage.usedBytes-disk.history[0].usedBytes;
        card.append(element("p","storage-growth",`${delta>=0?"+":"−"}${formatBytes(Math.abs(delta))} since ${dateTime(disk.history[0].at)}`));
      }
    }
    grid.append(card);
  }
  section.append(element("h3","monitor-subheading","Filesystems & storage growth"),grid);
}
function renderHardwareMetrics() {
  const data=detailedSystem.hardware;const section=$("monitor-hardware");section.replaceChildren();
  const grid=element("div","monitor-grid");
  for(const temp of data.temperatures) grid.append(metricCard(temp.sensor,`${temp.celsius.toFixed(1)} °C`,"Hardware temperature sensor"));
  if(!data.temperatures.length) grid.append(metricCard("Temperature","Unavailable","No readable temperature sensors."));
  section.append(grid,element("h3","monitor-subheading","Thermal throttling"));
  section.append(monitorTable(["Source","Active now","Recorded","Events"],data.throttling.map(t=>[t.source,boolText(t.active),boolText(t.occurredSinceBoot),t.totalEvents??"Unavailable"])));
  section.append(element("p","detail-hint","Event counters are cumulative; firmware history flags may be reset. Neither proves throttling is active now."));
  const pi=data.raspberryPi;
  section.append(element("h3","monitor-subheading","Raspberry Pi power health"));
  if(pi) section.append(monitorTable(["Indicator","Reading"],[["Undervoltage now",boolText(pi.underVoltage)],["Undervoltage latched",boolText(pi.underVoltageSinceBoot)],["Recent kernel voltage alarm",boolText(pi.recentVoltageAlarm)],["Throttled now",boolText(pi.throttled)],["Throttling latched",boolText(pi.throttledSinceBoot)],["Frequency capped now",boolText(pi.frequencyCapped)],["Soft temperature limit now",boolText(pi.softTemperatureLimit)]]),element("p","detail-hint","Firmware latches record events since boot or their last reset. The kernel voltage alarm describes its recent polling interval; it does not prove voltage is low at this instant."));
  else section.append(element("p","detail-hint","Unavailable. Requires supported Raspberry Pi firmware or a readable voltage sensor."));
  section.append(element("h3","monitor-subheading","Battery"));
  section.append(monitorTable(["Battery","Status","Charge","Energy","Power"],data.batteries.map(b=>[b.name,b.status,percentText(b.percent),Number.isFinite(b.energyWh)?`${b.energyWh.toFixed(1)} / ${b.fullEnergyWh?.toFixed(1)??"—"} Wh`:"Unavailable",Number.isFinite(b.powerWatts)?`${b.powerWatts.toFixed(1)} W`:"Unavailable"])));
}
function renderProcessMetrics() {
  if(!detailedSystem)return;
  const app=$("process-view").value==="applications",cpu=$("process-sort").value==="cpu";
  const rows=app?[...detailedSystem.applications].sort((a,b)=>cpu?(b.cpuPercent??-1)-(a.cpuPercent??-1):b.memoryBytes-a.memoryBytes).slice(0,20):cpu?detailedSystem.topCpu:detailedSystem.topMemory;
  $("process-readings").replaceChildren(monitorTable([app?"Application":"Process",app?"Processes":"PID","CPU","RAM (RSS)"],rows.map(p=>[`${p.name}${p.partial?" · partial":""}`,app?p.processes:p.pid,percentText(p.cpuPercent),formatBytes(p.memoryBytes)])),element("p","detail-hint","CPU: one fully used core = 100%; multithreaded applications can exceed 100%. Applications group by executable name. RSS sums can count shared memory more than once. Only visible processes are included; command lines and environment variables are never collected."));
}
function runnerResourcePanel(runner) {
  const section=element("section","runner-resource-summary");
  const usage=detailedSystem?.runners.find(r=>r.id===runner.id);
  section.append(element("h3","monitor-subheading","Runner resources"));
  if(!usage) {section.append(element("p","detail-hint","Waiting for resource readings…"));return section;}
  const grid=element("div","monitor-grid compact");
  grid.append(metricCard("CPU",percentText(usage.cpuPercent)),metricCard("RAM (RSS)",formatBytes(usage.memoryBytes)),metricCard("Processes",String(usage.processes)),metricCard("Workspace",usage.workspace?`${usage.workspace.partial?"≥ ":""}${formatBytes(usage.workspace.bytes)}`:"Unavailable"));
  section.append(grid,element("p","detail-hint",`Listener, worker and visible descendants${usage.partial?" · partial reading":""}. CPU: one core = 100%.`));
  return section;
}
function renderRunnerMetrics() {
  const section=$("monitor-runner-resources");section.replaceChildren();
  section.append(monitorTable(["Runner","CPU","RAM (RSS)","Processes","Workspace"],detailedSystem.runners.map(r=>[
    (snapshot?.runners.find(p=>p.id===r.id)?.displayName||r.folder)+(r.partial?" · partial":""),percentText(r.cpuPercent),formatBytes(r.memoryBytes),r.processes,r.workspace?`${r.workspace.partial?"≥ ":""}${formatBytes(r.workspace.bytes)}`:"Unavailable"
  ])));
  section.append(element("p","detail-hint","Includes each runner’s listener, worker and visible child processes. Detached/reparented processes and work launched through a container daemon may not be attributable. RAM is summed RSS, not unique memory."));
  for(const r of detailedSystem.runners) if(r.workspace) section.append(element("p","workspace-note",`${r.workspace.path} · ${r.workspace.files.toLocaleString()} files · Scanned ${dateTime(r.workspace.checkedAt)}${r.workspace.message?` · ${r.workspace.message}`:""}`));
  section.append(element("p","detail-hint","Workspace sizes are logical file sizes. Scans run every 5 minutes by default and skip nested symlinks and mounts. A ≥ value is a lower bound from a partial scan; hard-linked files may be counted more than once."));
}
async function refreshMonitoring() {
  if(monitoringLoading)return;monitoringLoading=true;
  const controller=new AbortController();const timeout=setTimeout(()=>controller.abort(),12000);
  try {
    const response=await fetch("/api/system",{cache:"no-store",signal:controller.signal});
    if(!response.ok)throw new Error();
    detailedSystem=await response.json();
    renderCpuMetrics();renderNetworkMetrics();renderStorageMetrics();renderHardwareMetrics();renderProcessMetrics();renderRunnerMetrics();
    const age=Date.now()-Date.parse(detailedSystem.checkedAt);
    $("monitoring-checked").textContent=`${detailedSystem.demo?"Sample data · ":""}${dateTime(detailedSystem.checkedAt)}`;
    $("monitoring-warning").textContent=[...detailedSystem.warnings,...(age>45000?["System readings are stale; the collector has not completed a recent sample."]:[])].join(" ");
    $("monitoring-warning").hidden=!$("monitoring-warning").textContent;
    if(snapshot&&expanded.size)render();
  } catch {
    $("monitoring-warning").textContent=detailedSystem?"System refresh unavailable. Displayed readings may be out of date.":"Waiting for the first system sample. Retrying automatically.";
    $("monitoring-warning").hidden=false;
  } finally {clearTimeout(timeout);monitoringLoading=false;}
}
const monitorTabs=[...document.querySelectorAll("[data-monitor-tab]")];
function selectMonitorTab(tab) {
  for(const button of monitorTabs) {const active=button===tab;button.setAttribute("aria-selected",String(active));button.tabIndex=active?0:-1;$("monitor-"+button.dataset.monitorTab).hidden=!active;}
}
for(const tab of monitorTabs) {
  tab.addEventListener("click",()=>selectMonitorTab(tab));
  tab.addEventListener("keydown",event=>{let next;if(event.key==="ArrowRight")next=(monitorTabs.indexOf(tab)+1)%monitorTabs.length;else if(event.key==="ArrowLeft")next=(monitorTabs.indexOf(tab)+monitorTabs.length-1)%monitorTabs.length;else if(event.key==="Home")next=0;else if(event.key==="End")next=monitorTabs.length-1;else return;event.preventDefault();selectMonitorTab(monitorTabs[next]);monitorTabs[next].focus();});
}
$("process-view").addEventListener("change",renderProcessMetrics);$("process-sort").addEventListener("change",renderProcessMetrics);
$("refresh").addEventListener("click",refreshMonitoring);
refreshMonitoring();setInterval(refreshMonitoring,15000);
