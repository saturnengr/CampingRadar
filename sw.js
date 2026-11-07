/* Camp Radar service worker: shows push alerts from the backend. */
self.addEventListener('install', e => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

self.addEventListener('push', e => {
  let d = {}; try { d = e.data ? e.data.json() : {}; } catch (err) { d = { title: 'Camp Radar', body: e.data && e.data.text() }; }
  const critical = (d.tag || '').startsWith('warning') || d.level === 'imminent' || d.level === 'atyou';
  const opts = { body: d.body || '', tag: d.tag || 'campradar', renotify: true, requireInteraction: critical, data: d, icon: './icon-192.png', badge: './icon-192.png', vibrate: critical ? [300, 100, 300, 100, 600] : [150, 80, 150] };
  if (d.tag === 'msg' && d.replyToken) opts.actions = (d.lang === 'es' ? ['OK', '5 min', 'No puedo'] : ['OK', '5 min', "Can't"]).map(t => ({ action: 'reply:' + t, title: t }));
  e.waitUntil(self.registration.showNotification(d.title || 'Camp Radar', opts));
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
      if (be && d.toEmail) { try { await fetch(be + '/group/reply', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ replyToken: d.replyToken, fromEmail: d.toEmail, text }) }); return; } catch (err) {} }
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
