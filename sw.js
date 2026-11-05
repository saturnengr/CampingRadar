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
  e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(list => {
    for (const c of list) if ('focus' in c) return c.focus();
    return self.clients.openWindow('./');
  }));
});
