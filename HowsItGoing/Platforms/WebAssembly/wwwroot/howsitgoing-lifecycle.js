// Page-lifecycle signals for the session feed.
//
// Mobile browsers throttle background timers to roughly once a minute and freeze them outright
// once the tab is hidden or the screen locks, so the app's polling timer on its own leaves the
// feed showing whatever was on screen when you locked the phone. Every wake-up signal bumps a
// counter here; the app reads the counter on each tick and refreshes immediately when it moves,
// which turns "stale until the next full interval" into "fresh by the time you have looked".
//
// A counter rather than a callback keeps this a plain synchronous read from .NET - no JSExport
// marshalling, and nothing to unsubscribe when the page tears down.
let resumeCount = 0;

export function watch() {
    // The module can be imported more than once across a hot reload; only ever wire up once.
    if (globalThis.__howsitgoingLifecycleWatched) {
        return;
    }
    globalThis.__howsitgoingLifecycleWatched = true;

    const bump = () => { resumeCount++; };

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            bump();
        }
    });

    // focus covers tab switches on desktop, pageshow covers the mobile back/forward cache
    // (Safari restores a frozen page from it rather than reloading), online covers a phone
    // coming back from a dead spot with the tab still open.
    globalThis.addEventListener('focus', bump);
    globalThis.addEventListener('pageshow', bump);
    globalThis.addEventListener('online', bump);
}

export function resumeToken() {
    return resumeCount;
}
