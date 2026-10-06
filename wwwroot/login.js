"use strict";
let loginSession;
const loginError=document.getElementById("login-error");
function showLoginError(text){loginError.textContent=text;loginError.hidden=false;}
async function loadLogin() {
  try {
    const response=await fetch("/api/access/session",{cache:"no-store"});
    if(!response.ok)throw new Error();
    loginSession=await response.json();
    if(!loginSession.enabled||loginSession.authenticated){location.replace("/");return;}
    document.getElementById("login-form").hidden=!loginSession.localLogin;
    document.getElementById("github-login").hidden=!loginSession.githubLogin;
    if(new URLSearchParams(location.search).has("error"))showLoginError("GitHub sign-in failed or this account is not allowed. Try again or contact your administrator.");
  }catch{showLoginError("Sign-in is unavailable. Use the configured dashboard address and HTTPS, or contact your administrator.");}
}
document.getElementById("login-form").addEventListener("submit",async event=>{
  event.preventDefault();const button=document.getElementById("login-submit");button.disabled=true;
  try {
    const response=await fetch("/api/access/login",{method:"POST",headers:{"Content-Type":"application/json","X-RR-CSRF":loginSession.csrfToken},
      body:JSON.stringify({username:document.getElementById("login-username").value,password:document.getElementById("login-password").value})});
    document.getElementById("login-password").value="";
    if(response.ok){location.replace("/");return;}
    showLoginError(response.status===429?"Too many attempts. Wait a minute and try again.":response.status===403?"Your sign-in form expired. Reload the page and try again.":"Sign-in failed. Check your username and password.");
  }catch{showLoginError("Could not reach the server. Please try again.");}
  finally{button.disabled=false;}
});
loadLogin();
