"use strict";
window.dashboardAccess = {canAdmin:false};
(() => {
  const nativeFetch = window.fetch.bind(window);
  window.accessReady = nativeFetch("/api/access/session", {cache:"no-store"}).then(async response => {
    if(!response.ok)throw new Error("Session unavailable");
    const session = await response.json();
    window.dashboardAccess = session;
    if(session.enabled&&!session.authenticated){location.replace("/login.html");return session;}
    document.getElementById("access-session").hidden=!session.enabled;
    document.getElementById("access-identity").textContent="Signed in as "+session.name+" · "+session.role;
    window.dispatchEvent(new Event("access-ready"));
    return session;
  }).catch(()=>{document.getElementById("connection").textContent="Session unavailable";return window.dashboardAccess;});
  // Session cookies remain HttpOnly; only the antiforgery request token is available to scripts.
  window.fetch=async (input, init={})=>{
    const url=new URL(typeof input==="string"?input:input.url,location.href);
    const method=(init.method||input.method||"GET").toUpperCase();
    if(url.origin===location.origin&&url.pathname.startsWith("/api/")&&!["GET","HEAD"].includes(method)){
      const session=await window.accessReady;
      if(session.csrfToken){const headers=new Headers(init.headers);headers.set("X-RR-CSRF",session.csrfToken);init={...init,headers};}
    }
    const response=await nativeFetch(input,init);
    if(url.origin===location.origin&&url.pathname.startsWith("/api/")&&response.status===401)location.replace("/login.html");
    return response;
  };
  document.getElementById("access-logout").addEventListener("click",async()=>{
    const response=await fetch("/api/access/logout",{method:"POST"});
    if(response.ok)location.replace("/login.html");
  });
})();
