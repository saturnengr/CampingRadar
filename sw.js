/* Camp Radar service worker: shows push alerts from the backend. */
self.addEventListener('install', e => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

self.addEventListener('push', e => {
  let d = {};
  try { d = e.data ? e.data.json() : {}; } catch (err) { d = { title: 'Camp Radar', body: e.data && e.data.text() }; }
  const urgent = d.tag === 'warning' || d.tag === 'lightning' || d.level === 'imminent' || d.level === 'atyou';
  e.waitUntil(self.registration.showNotification(d.title || 'Camp Radar', {
    body: d.body || '', tag: d.tag || 'campradar', renotify: true, requireInteraction: urgent,
    icon: 'icon-192.png', badge: 'icon-192.png', vibrate: urgent ? [300, 100, 300, 100, 600] : [200, 80, 200], data: d
  }));
});

self.addEventListener('notificationclick', e => {
  e.notification.close();
  const d = e.notification.data || {};
  const isBrief = d.tag === 'brief' && d.brief;
  e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(async list => {
    for (const c of list) if ('focus' in c) { await c.focus(); if (isBrief) c.postMessage({ type: 'brief', text: d.brief, lang: d.lang }); return; }
    const w = await self.clients.openWindow(isBrief ? './?brief=1' : './');
    if (w && isBrief) setTimeout(() => { try { w.postMessage({ type: 'brief', text: d.brief, lang: d.lang }); } catch (err) {} }, 3500);
  }));
});
