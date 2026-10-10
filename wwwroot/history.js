"use strict";
let historyPage=Number.isInteger(roomPrefs.historyPage) && roomPrefs.historyPage > 0 ? roomPrefs.historyPage : 1, historyData=null, historyRequest=null, historyViewKey=null;
function historyDates(days) {
  const now=new Date(), first=new Date(Date.UTC(now.getUTCFullYear(),now.getUTCMonth(),now.getUTCDate()-days+1));
  $("history-from").value=first.toISOString().slice(0,10);$("history-to").value=now.toISOString().slice(0,10);
  $("history-from").max=$("history-to").max=now.toISOString().slice(0,10);
}
function historyQuery() {
  const query=new URLSearchParams({from:$("history-from").value,to:$("history-to").value,page:String(historyPage)});
  if($("history-runner").value || roomPrefs.historyRunner)query.set("runner",$("history-runner").value || roomPrefs.historyRunner);
  if($("history-result").value)query.set("result",$("history-result").value);
  return query;
}
function historyTime(seconds) {
  if(!Number.isFinite(seconds))return "Unavailable";
  if(seconds<60)return `${Math.round(seconds)}s`;
  if(seconds<3600)return `${(seconds/60).toFixed(1)}m`;
  return `${(seconds/3600).toFixed(1)}h`;
}
function historyBar(label,value,max,text) {
  const row=element("div","history-bar");
  const meter=element("progress");meter.max=max||1;meter.value=value||0;meter.setAttribute("aria-label",`${label}: ${text}`);
  row.append(element("span","",label),meter,element("span","",text));return row;
}
function renderHistory() {
  const data=historyData,summary=data.summary;
  const selected=$("history-runner").value || roomPrefs.historyRunner || "";
  const viewKey=JSON.stringify([data.from.slice(0,10),data.to.slice(0,10),selected,$("history-result").value,data.page]);
  const oldTable=$("history-jobs").querySelector(".monitor-table-wrap");
  const scroll=viewKey===historyViewKey&&oldTable?{top:oldTable.scrollTop,left:oldTable.scrollLeft,focused:document.activeElement===oldTable}:null;
  $("history-runner").replaceChildren(new Option("All runners",""),...data.runnerOptions.map(r=>new Option(`${r.name}${r.repository?` · ${r.repository}`:""}`,r.id)));
  if(selected&&!data.runnerOptions.some(r=>r.id===selected))$("history-runner").append(new Option("Runner no longer retained",selected));
  $("history-runner").value=selected;
  const totals=data.runners.reduce((t,r)=>({busy:t.busy+r.busySeconds,idle:t.idle+r.idleSeconds,offline:t.offline+r.offlineSeconds,unknown:t.unknown+r.unknownSeconds}),{busy:0,idle:0,offline:0,unknown:0});
  const known=totals.busy+totals.idle+totals.offline,covered=known+totals.unknown;
  const possible=Math.max(0,(Date.parse(data.to)-Date.parse(data.from))/1000)*data.runners.length;
  $("history-coverage").textContent=`${data.demo?"Sample history · ":""}${historyTime(covered)} observed across ${data.runners.length} runners · ${possible?Math.min(100,covered/possible*100).toFixed(1):"0.0"}% coverage of selected runner-time · ${historyTime(totals.unknown)} unknown · ${summary.incomplete} starts without an observed completion. Collection gaps are not treated as downtime.`;
  $("history-summary").replaceChildren(
    metricCard("Completed jobs",String(summary.completed),`${summary.failed} failed / abandoned · ${summary.canceled} canceled · ${summary.skipped} skipped`),
    metricCard("Success rate",percentText(summary.successRate),"Includes success with issues; excludes skipped and incomplete jobs"),
    metricCard("Average duration",historyTime(summary.averageDurationSeconds),`Median ${historyTime(summary.medianDurationSeconds)} · paired job records only`),
    metricCard("95th percentile",historyTime(summary.p95DurationSeconds),"95% of measured durations are at or below this value"),
    metricCard("Utilization",percentText(known?totals.busy/known*100:null),"Busy / known observed runner-time"),
    metricCard("Local availability",percentText(known?(totals.busy+totals.idle)/known*100:null),"Busy + idle / known observed runner-time")
  );
  $("history-job-chart").replaceChildren();$("history-utilization-chart").replaceChildren();
  const max=Math.max(1,...data.days.map(d=>d.completed));
  for(const day of data.days) {
    const row=historyBar(day.date,day.completed,max,String(day.completed));row.title=`${day.succeeded} succeeded · ${day.failed} failed / abandoned · ${day.canceled} canceled`;
    $("history-job-chart").append(row);
    const utilization=day.knownSeconds?day.busySeconds/day.knownSeconds*100:null;
    $("history-utilization-chart").append(historyBar(day.date,utilization,100,Number.isFinite(utilization)?`${utilization.toFixed(1)}%`:"No coverage"));
  }
  $("history-runners").replaceChildren(monitorTable(["Runner","Completed","Busy","Idle","Offline","Unknown","Utilization","Availability"],data.runners.map(r=>[r.name,r.completedJobs,historyTime(r.busySeconds),historyTime(r.idleSeconds),historyTime(r.offlineSeconds),historyTime(r.unknownSeconds),percentText(r.utilization),percentText(r.availability)])));
  const names=new Map(data.runnerOptions.map(r=>[r.id,r.name]));
  $("history-jobs").replaceChildren(monitorTable(["Runner","Job","Started","Completed","Outcome","Duration"],data.jobs.map(j=>[names.get(j.runnerId)||"Unknown runner",detailLink("job",j.id,j.name),j.startedAt?dateTime(j.startedAt):"Not observed",j.completedAt?dateTime(j.completedAt):"Not observed",j.result||"Completion not observed",historyTime(j.durationSeconds)])));
  const jobTable=$("history-jobs").querySelector(".monitor-table-wrap");
  jobTable.tabIndex=0;jobTable.setAttribute("role","region");jobTable.setAttribute("aria-label","Job history, scroll for more records");
  if(scroll){jobTable.scrollTop=scroll.top;jobTable.scrollLeft=scroll.left;if(scroll.focused)jobTable.focus({preventScroll:true});}
  historyViewKey=viewKey;
  if(!data.totalJobs)$("history-jobs").append(element("p","detail-hint","No recorded jobs match these filters. History begins with the listener summaries available locally."));
  historyPage=data.page; roomPrefs.historyPage=historyPage; roomPrefs.historyRunner=selected; saveRoomPrefs();
  $("history-page").textContent=`Page ${data.page} of ${Math.max(1,Math.ceil(data.totalJobs/data.pageSize))} · ${data.totalJobs} job records`;
  $("history-previous").disabled=data.page<=1;$("history-next").disabled=data.page*data.pageSize>=data.totalJobs;
  $("history-export").disabled=!data.totalJobs;
  $("history-message").textContent=[...data.warnings,...(!data.demo&&(!data.checkedAt||Date.now()-Date.parse(data.checkedAt)>45000)?["History collection has not completed a recent scan; these records may be out of date."]:[])].join(" ");
  $("history-message").hidden=!$("history-message").textContent;
}
async function refreshHistory() {
  historyRequest?.abort();const controller=new AbortController();historyRequest=controller;
  const timeout=setTimeout(()=>controller.abort(),12000);$("history-export").disabled=true;
  try {
    const response=await fetch(`/api/history?${historyQuery()}`,{cache:"no-store",signal:controller.signal});
    if(!response.ok)throw new Error(response.status===400?"Choose valid UTC dates covering at most 90 days, ending no later than today.":"History is unavailable. Previously displayed records may be out of date.");
    const data=await response.json();if(historyRequest!==controller)return;historyData=data;renderHistory();
  } catch(error) {
    if(historyRequest!==controller)return;
    $("history-message").textContent=error.message||"History is unavailable.";$("history-message").hidden=false;
  } finally {clearTimeout(timeout);if(historyRequest===controller)historyRequest=null;}
}
$("history-controls").addEventListener("submit",event=>{event.preventDefault();historyPage=1;refreshHistory();});
$("history-period").addEventListener("change",()=>{const days=Number($("history-period").value);if(days){historyDates(days);historyPage=1;refreshHistory();}});
for(const id of ["history-from","history-to"])$(id).addEventListener("input",()=>{$("history-period").value="custom";$("history-export").disabled=true;});
for(const id of ["history-runner","history-result"])$(id).addEventListener("change",()=>{roomPrefs.historyRunner=$("history-runner").value;historyPage=1;refreshHistory();});
$("history-previous").addEventListener("click",()=>{historyPage--;refreshHistory();});
$("history-next").addEventListener("click",()=>{historyPage++;refreshHistory();});
$("history-export").addEventListener("click",async()=>{
  const button=$("history-export");button.disabled=true;
  try {
    const query=historyQuery();query.delete("page");
    const response=await fetch(`/api/history/export?${query}`,{signal:AbortSignal.timeout(12000)});if(!response.ok)throw new Error();
    const url=URL.createObjectURL(await response.blob());const link=element("a");link.href=url;link.download="runner-room-job-history.csv";document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
  } catch {$("history-message").textContent="Could not export job history. Please retry.";$("history-message").hidden=false;}
  finally {button.disabled=!historyData?.totalJobs;}
});
$("refresh").addEventListener("click",refreshHistory);
historyDates(7);
window.addEventListener("preferences-ready", () => { if ($("history-period").value !== "custom") historyDates(Number($("history-period").value) || 7); refreshHistory(); });
setInterval(()=>{if(!historyRequest)refreshHistory();},15000);
