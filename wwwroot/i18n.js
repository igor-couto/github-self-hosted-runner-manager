"use strict";
(() => {
  const originals = new WeakMap(); let dictionary = {}, observer;
  const ignore = node => node.parentElement?.closest("code,pre,input,textarea,script,style,dd,.job-title strong,.step-name,.quota-card h3,.runner-label,.runner-subtitle,.runner-target,.project-link,.log-output,[data-no-translate]");
  function translate() {
    observer?.disconnect(); document.documentElement.lang = window.roomPrefs?.language || "en";
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node = walker.currentNode; if (ignore(node)) continue;
      const old = originals.get(node), source = old && (node.nodeValue === old.last || node.nodeValue === old.source) ? old.source : node.nodeValue;
      const text = source.trim(); if (!dictionary[text]) continue;
      const result = document.documentElement.lang === "pt-PT" ? source.replace(text, dictionary[text]) : source;
      originals.set(node, { source, last: result }); if (node.nodeValue !== result) node.nodeValue = result;
    }
    observer?.observe(document.body, { subtree: true, childList: true, characterData: true });
  }
  window.translateRoom = translate;
  document.addEventListener("DOMContentLoaded", async () => {
    try { dictionary = await (await fetch("/locales/pt-PT.json")).json(); } catch { }
    let pending = false; observer = new MutationObserver(() => { if (!pending) { pending = true; requestAnimationFrame(() => { pending = false; translate(); }); } }); translate();
  });
})();
