"use strict";
let integrationLoading=false;
async function refreshIntegration() {
  if(integrationLoading)return;
  integrationLoading=true;
  try{
    const session=await window.accessReady;
    $("integration-summary").replaceChildren(metricCard("Dashboard access",session.enabled?"Sign-in required":"Open LAN access",session.enabled?"Your role: "+session.role:"Anyone who can reach this dashboard has administrator access."));
    $("integration-permissions").textContent=session.canAdmin?"Administrator access: diagnostics, manual checks and integration settings are available.":"Viewer access: you can read runner status, metrics and job history. Diagnostic logs and administrative actions require an admin.";
    $("integration-admin").hidden=!session.canAdmin;
    if(!session.canAdmin)return;
    const [response,auditResponse]=await Promise.all([fetch("/api/integration",{cache:"no-store"}),fetch("/api/access/audit",{cache:"no-store"})]);
    if(!response.ok||!auditResponse.ok)throw new Error();
    const data=await response.json(),audit=await auditResponse.json();
    $("integration-summary").append(metricCard("GitHub runner API",data.credentialConfigured?"Token configured":"Not configured","Read-only repository / organization runner checks"),metricCard("GitHub sign-in",data.githubSignIn?"Enabled":"Not configured","Explicit GitHub ID allowlist"));
    $("integration-runners").replaceChildren(monitorTable(["Runner","GitHub scope","Registration","Status","Checked","Details"],data.runners.map(r=>[
      r.displayName,r.repository||r.organization||"Unavailable",r.agentId||"Unavailable",r.gitHub.status,dateTime(r.gitHub.checkedAt),r.gitHub.message||(r.gitHub.status==="online"?"Registration verified":r.gitHub.status==="offline"?"Registration verified; runner offline":"Not verified")
    ])));
    $("integration-users").replaceChildren(monitorTable(["Identity","Provider","Role"],[
      ...data.localUsers.map(u=>[u.name,"Local",u.role]),...data.githubUsers.map(u=>[u.id,"GitHub ID",u.role])
    ]));
    if(!data.localUsers.length&&!data.githubUsers.length)$("integration-users").replaceChildren(element("p","detail-hint","No login accounts configured. Set Access.Enabled and add an administrator to require sign-in."));
    $("integration-audit").replaceChildren(monitorTable(["When","Event","Account","Address"],audit.entries.map(e=>[dateTime(e.at),e.event.replaceAll("_"," "),e.actor,e.address||"Unavailable"])));
    if(!audit.entries.length)$("integration-audit").replaceChildren(element("p","detail-hint","No recorded access events yet."));
    $("integration-warning").textContent=audit.warning||"";$("integration-warning").hidden=!audit.warning;
  }catch{$("integration-warning").textContent="Integration details are unavailable. Previously displayed information may be out of date.";$("integration-warning").hidden=false;}
  finally{integrationLoading=false;}
}
$("refresh").addEventListener("click",refreshIntegration);
refreshIntegration();setInterval(refreshIntegration,15000);
