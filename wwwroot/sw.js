"use strict";
// Only the generic offline page and icons are cached. API responses, authentication,
// logs and dashboard HTML always go to the network and are never replayed offline.
const CACHE = "runner-room-offline-v1";
const FILES = ["/offline.html", "/offline.css", "/icons/icon-192.png", "/icons/icon-512.png"];
self.addEventListener("install", event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(FILES)).then(() => self.skipWaiting())));
self.addEventListener("activate", event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k.startsWith("runner-room-offline-") && k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim())));
self.addEventListener("fetch", event => {
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin || event.request.method !== "GET" || url.pathname.startsWith("/api/")) return;
  if (event.request.mode === "navigate") event.respondWith(fetch(event.request).catch(() => caches.match("/offline.html")));
  else if (FILES.includes(url.pathname)) event.respondWith(caches.match(url.pathname).then(cached => cached || fetch(event.request)));
});
