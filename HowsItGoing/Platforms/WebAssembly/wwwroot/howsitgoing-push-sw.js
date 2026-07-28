// Push service worker for the How's It Going feed.
//
// Deliberately separate from the service worker the Uno bootstrapper generates for offline caching:
// that file is regenerated on every build, and only one worker may control a given scope. Registering
// this one under a narrow scope ("howsitgoing-push/") means it never controls the page - which is
// fine, a push subscription only needs a registration, not control of any client.

const DEFAULT_TITLE = "How's It Going";

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
    let payload = {};
    if (event.data) {
        try {
            payload = event.data.json();
        } catch {
            // A push service can wake a worker with no (or non-JSON) payload; still show something,
            // because a 'push' handler that shows no notification gets the subscription penalised.
            payload = { body: event.data.text() };
        }
    }

    const title = payload.title || DEFAULT_TITLE;
    const options = {
        body: payload.body || 'New activity in the feed.',
        // Tagging by notification id lets a re-sent push replace its predecessor instead of stacking.
        tag: payload.id || 'howsitgoing-feed',
        timestamp: payload.occurredAt ? Date.parse(payload.occurredAt) : Date.now(),
        icon: '/favicon.ico',
        badge: '/favicon.ico',
        data: {
            url: payload.url || null,
            kind: payload.kindName || null,
            sessionId: payload.sessionId || null
        }
    };

    event.waitUntil((async () => {
        await self.registration.showNotification(title, options);

        // Tell any open copy of the app too, so a feed the user is looking at can update straight
        // away instead of waiting out the rest of its 30s refresh interval.
        const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of clients) {
            client.postMessage({ type: 'howsitgoing-push', payload });
        }
    })());
});

self.addEventListener('notificationclick', event => {
    event.notification.close();

    const target = event.notification.data && event.notification.data.url;

    event.waitUntil((async () => {
        const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });

        // A notification with a link (a GitHub commit, say) opens it; otherwise just surface the app.
        if (target) {
            await self.clients.openWindow(target);
            return;
        }

        for (const client of clients) {
            if ('focus' in client) {
                await client.focus();
                return;
            }
        }

        await self.clients.openWindow('/');
    })());
});
