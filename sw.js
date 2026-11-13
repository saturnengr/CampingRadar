/* VantageWX service worker: shows push alerts from the backend. */
self.addEventListener('install', e => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

// Radar tiles for past scans never change: keep them (cache-first) so a sliding loop only fetches the newest frame.
// The page marks such requests with keep=1; the cache is trimmed to ~400 tiles.
const TILES = 'campradar-tiles';
self.addEventListener('fetch', e => {
  const url = e.request.url;
  if (e.request.method !== 'GET' || !/[?&]keep=1\b/.test(url) || !/\/mrms\/tile\/|lockRasterIds/.test(url)) return;
  e.respondWith((async () => {
    // the cache is a convenience, never a dependency: any storage trouble (quota, a private window, a dying battery's storage limits)
    // falls straight through to the network, and a network failure is reported as such rather than swallowed
    let c = null, hit = null;
    try { c = await caches.open(TILES); hit = await c.match(e.request, { ignoreVary: true }); } catch (err) { c = null; }
    if (hit) return hit;
    const res = await fetch(e.request);
    if (c && res.ok) { try { await c.put(e.request, res.clone()); trimTiles(c); } catch (err) {} }   // only real (CORS) responses: opaque ones cost megabytes of quota each
    return res;
  })().catch(() => fetch(e.request)));
});
let trimming = false;
async function trimTiles(c) {
  if (trimming) return; trimming = true;
  try { const keys = await c.keys(); if (keys.length > 400) for (const k of keys.slice(0, keys.length - 320)) await c.delete(k); } catch (err) {} finally { trimming = false; }
}

self.addEventListener('push', e => {
  let d = {}; try { d = e.data ? e.data.json() : {}; } catch (err) { d = { title: 'VantageWX', body: e.data && e.data.text() }; }
  const critical = (d.tag || '').startsWith('warning') || d.level === 'imminent' || d.level === 'atyou';
  const opts = { body: d.body || '', tag: d.tag || 'campradar', renotify: true, requireInteraction: critical, data: d, icon: './icon-192.png', badge: './icon-192.png', vibrate: critical ? [300, 100, 300, 100, 600] : [150, 80, 150] };
  if (d.tag === 'msg' && d.replyToken) opts.actions = (d.lang === 'es' ? ['OK', '5 min', 'No puedo'] : ['OK', '5 min', "Can't"]).map(t => ({ action: 'reply:' + t, title: t }));
  e.waitUntil(self.registration.showNotification(d.title || 'VantageWX', opts));
});
self.addEventListener('notificationclick', e => {
  e.notification.close();
  const d = e.notification.data || {};
  // Reply chip on a group message: post the reply straight from the service worker (the message's own token authorises it).
  if (e.action && e.action.startsWith('reply:') && d.replyToken) {
    const text = e.action.slice(6);
    e.waitUntil((async () => {
      // the page stores the backend address for us under caches 'campradar-meta' / 'backend'; the payload names the recipient
      let be = null; try { const c = await caches.open('campradar-meta'); const r = await c.match('backend'); be = r ? await r.text() : null; } catch (err) {}
      if (be && d.toEmail) { try { await fetch(be + '/group/reply', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ replyToken: d.replyToken, fromEmail: d.toEmail, text, msgId: d.msgId }) }); return; } catch (err) {} }
      const list = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
      if (list.length) list[0].postMessage({ type: 'reply', replyToken: d.replyToken, text });
    })());
    return;
  }
  const isBrief = d.tag === 'brief' && d.brief;
  const isMsg = d.tag === 'msg';
  e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(async list => {
    for (const c of list) if ('focus' in c) { await c.focus(); if (isBrief) c.postMessage({ type: 'brief', text: d.brief, lang: d.lang }); if (isMsg) c.postMessage({ type: 'msg', title: d.title, body: d.body, msgId: d.msgId }); return; }
    const w = await self.clients.openWindow(isBrief ? './?brief=1' : './');
    if (w && isBrief) setTimeout(() => { try { w.postMessage({ type: 'brief', text: d.brief, lang: d.lang }); } catch (err) {} }, 3500);
  }));
});

// The browser replaced our push subscription: ask the app to register the new one (it does so on its next open if it isn't running).
self.addEventListener('pushsubscriptionchange', e => {
  e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(list => { for (const c of list) c.postMessage({ type: 'resubscribe' }); }));
});
