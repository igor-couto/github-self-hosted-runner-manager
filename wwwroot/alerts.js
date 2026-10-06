"use strict";
let alertsData=null, alertsLoading=false, alertsReachable=false, alertsBusyUntil=0;
const alertKindNames={"runner-offline":"Runner offline","job-duration":"Job duration (minutes)","job-failures":"Failures in 15 minutes","cpu":"CPU usage (%)","memory":"RAM usage (%)","disk":"Disk used (%)","temperature":"Temperature (°C)","monitor-unavailable":"Monitoring unavailable"};
function renderAlerts() {
  const data=alertsData, severity=$("alerts-severity").value;
  const active=data.active.filter(a=>!severity||a.severity===severity), history=data.history.filter(a=>!severity||a.severity===severity);
  $("alerts-summary").replaceChildren(
    metricCard("Active alerts",String(data.active.length),data.active.filter(a=>a.severity==="critical").length+" critical · "+data.active.filter(a=>a.state==="unknown").length+" awaiting fresh readings"),
    metricCard("Scheduled checks",data.enabled?"Every "+data.checkIntervalSeconds+"s":"Disabled",data.lastCheckedAt?"Last check "+dateTime(data.lastCheckedAt):"Waiting for first check"),
    metricCard("Notifications",data.demo?"Demo":data.quietHours?"Quiet hours":data.notificationChannel,data.demo?"Sample alerts; external delivery is disabled":data.repeatMinutes?"Repeat unresolved alerts every "+data.repeatMinutes+" minutes":"No repeated notifications")
  );
  $("alerts-schedule").textContent=(data.demo?"Sample alerts · ":"")+(data.enabled&&data.nextCheckAt?"Next check: "+dateTime(data.nextCheckAt):"Automatic checks disabled")+" · "+(data.quietHoursUtc?"Quiet hours: "+data.quietHoursUtc:"No quiet hours configured");
  $("alerts-active").replaceChildren();
  for(const alert of active) {
    const card=element("article","alert-card "+alert.severity);
    const heading=element("div","alert-heading");
    heading.append(element("h4","",alert.title),element("span","alert-label",alert.state==="unknown"?"Unconfirmed":alert.severity));
    card.append(heading,element("p","",alert.detail),element("p","detail-hint","Since "+dateTime(alert.firedAt)+" · Last observed "+dateTime(alert.lastObservedAt)+" · "+alert.delivery));
    $("alerts-active").append(card);
  }
  if(!active.length)$("alerts-active").append(element("p","detail-hint",data.enabled?"No active alerts match this filter. Check rule readings below for unavailable data.":"Checks are disabled. Previously recorded alerts may be out of date."));
  $("alerts-rules").replaceChildren(monitorTable(["Rule","Condition","Threshold","Sustained for","Severity","Target","Readings"],data.rules.map(r=>[
    r.id,alertKindNames[r.kind]||r.kind,["runner-offline","monitor-unavailable"].includes(r.kind)?"—":r.threshold,
    r.holdSeconds+"s",r.severity,r.target||"All applicable targets",data.demo?"Sample data":!data.enabled?"Disabled":!r.targets?"No matching targets":(r.targets-r.unavailable)+" available / "+r.targets+" targets"
  ])));
  $("alerts-history").replaceChildren(monitorTable(["Alert","Severity","Triggered","Closed","Outcome","Delivery"],history.map(a=>[
    a.title,a.severity,dateTime(a.firedAt),dateTime(a.resolvedAt),a.state==="retired"?"Rule changed":"Recovered",a.delivery
  ])));
  if(!history.length)$("alerts-history").replaceChildren(element("p","detail-hint","No closed alerts match this filter."));
  for(const id of ["alerts-rules","alerts-history"]) {
    const table=$(id).querySelector(".monitor-table-wrap");
    if(table){table.tabIndex=0;table.setAttribute("role","region");table.setAttribute("aria-label",id==="alerts-rules"?"Configured rules, scroll for more columns":"Alert history, scroll for more records");}
  }
  $("alerts-check").disabled=!data.enabled||!alertsReachable||Date.now()<alertsBusyUntil;
  const stale=data.enabled&&data.lastCheckedAt&&Date.now()-Date.parse(data.lastCheckedAt)>Math.max(90,data.checkIntervalSeconds*2)*1000;
  $("alerts-warning").textContent=[...data.warnings,...(stale?["Scheduled checks are overdue; displayed alert states may be out of date."]:[])].join(" ");
  $("alerts-warning").hidden=!$("alerts-warning").textContent;
}
async function refreshAlerts() {
  if(alertsLoading)return;
  alertsLoading=true;
  try {
    const response=await fetch("/api/alerts",{cache:"no-store",signal:AbortSignal.timeout(12000)});
    if(!response.ok)throw new Error();
    alertsData=await response.json();alertsReachable=true;renderAlerts();
  } catch {alertsReachable=false;$("alerts-warning").textContent="Alerts are unavailable. Previously displayed states may be out of date.";$("alerts-warning").hidden=false;$("alerts-check").disabled=true;}
  finally {alertsLoading=false;}
}
$("alerts-severity").addEventListener("change",()=>{if(alertsData)renderAlerts();});
$("alerts-check").addEventListener("click",async()=>{
  $("alerts-check").disabled=true;
  alertsBusyUntil=Date.now()+10000;
  try {
    const response=await fetch("/api/alerts/check",{method:"POST",headers:{"Content-Type":"application/json"},body:"{}",signal:AbortSignal.timeout(12000)});
    if(!response.ok)throw new Error(response.status===429?"A check is already running or was just requested.":"Could not request a check.");
    $("alerts-warning").textContent="Check queued. Results will refresh shortly.";$("alerts-warning").hidden=false;
    setTimeout(refreshAlerts,1500);
  } catch(error){$("alerts-warning").textContent=error.message;$("alerts-warning").hidden=false;}
  finally {setTimeout(()=>{if(alertsData?.enabled&&alertsReachable&&Date.now()>=alertsBusyUntil)$("alerts-check").disabled=false;},10000);}
});
$("refresh").addEventListener("click",refreshAlerts);
refreshAlerts();setInterval(refreshAlerts,15000);
