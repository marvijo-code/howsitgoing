// The browser half of feed push notifications, imported from C# via JSHost.ImportAsync.
//
// Every export returns a JSON string or a plain value, because the [JSImport] marshaller only carries
// primitives and Task<string>; richer shapes get serialised here and parsed on the C# side.

const SERVICE_WORKER_URL = '/howsitgoing-push-sw.js';

// A narrow scope on purpose. The Uno bootstrapper already owns a worker at '/', and registering a
// second one there would fight it. Push does not need page control, only a registration.
const SERVICE_WORKER_SCOPE = '/howsitgoing-push/';

let registrationPromise = null;

export function isSupported() {
    return typeof navigator !== 'undefined'
        && 'serviceWorker' in navigator
        && typeof window !== 'undefined'
        && 'PushManager' in window
        && 'Notification' in window
        && window.isSecureContext === true;
}

export function permission() {
    if (typeof Notification === 'undefined') {
        return 'unsupported';
    }

    return Notification.permission;
}

export async function currentSubscription() {
    if (!isSupported()) {
        return '';
    }

    const registration = await ensureRegistration();
    const subscription = await registration.pushManager.getSubscription();
    return subscription ? serialize(subscription) : '';
}

export async function subscribe(vapidPublicKey) {
    if (!isSupported()) {
        throw new Error('This browser cannot receive push notifications (needs a secure context and the Push API).');
    }

    if (!vapidPublicKey) {
        throw new Error('The bridge did not supply a VAPID public key.');
    }

    const granted = await Notification.requestPermission();
    if (granted !== 'granted') {
        throw new Error(`Notification permission was ${granted}.`);
    }

    const registration = await ensureRegistration();
    let subscription = await registration.pushManager.getSubscription();

    // A stale subscription made against a different VAPID key is rejected by the push service at send
    // time, so replace it rather than reusing it.
    if (subscription && !usesKey(subscription, vapidPublicKey)) {
        await subscription.unsubscribe();
        subscription = null;
    }

    if (!subscription) {
        subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: decodeBase64Url(vapidPublicKey)
        });
    }

    return serialize(subscription);
}

/** Returns the endpoint that was removed, or '' when there was nothing subscribed. */
export async function unsubscribe() {
    if (!isSupported()) {
        return '';
    }

    const registration = await ensureRegistration();
    const subscription = await registration.pushManager.getSubscription();
    if (!subscription) {
        return '';
    }

    const endpoint = subscription.endpoint;
    await subscription.unsubscribe();
    return endpoint;
}

async function ensureRegistration() {
    if (!registrationPromise) {
        registrationPromise = navigator.serviceWorker
            .register(SERVICE_WORKER_URL, { scope: SERVICE_WORKER_SCOPE })
            .then(waitForActivation)
            .catch(error => {
                // Do not cache a failure - the next attempt should retry the registration.
                registrationPromise = null;
                throw error;
            });
    }

    return registrationPromise;
}

// pushManager.subscribe needs an active worker. navigator.serviceWorker.ready is no use here because
// this registration never controls the page, so wait on the worker's own state instead.
function waitForActivation(registration) {
    if (registration.active) {
        return registration;
    }

    const worker = registration.installing || registration.waiting;
    if (!worker) {
        return registration;
    }

    return new Promise((resolve, reject) => {
        worker.addEventListener('statechange', () => {
            if (worker.state === 'activated') {
                resolve(registration);
            } else if (worker.state === 'redundant') {
                reject(new Error('The push service worker went redundant before activating.'));
            }
        });
    });
}

function serialize(subscription) {
    const json = subscription.toJSON();
    const keys = json.keys || {};

    return JSON.stringify({
        endpoint: subscription.endpoint,
        p256dh: keys.p256dh || null,
        auth: keys.auth || null,
        expiresAt: subscription.expirationTime ? new Date(subscription.expirationTime).toISOString() : null
    });
}

function usesKey(subscription, vapidPublicKey) {
    const existing = subscription.options && subscription.options.applicationServerKey;
    if (!existing) {
        return true; // Nothing to compare against; assume it is still valid.
    }

    const current = new Uint8Array(existing);
    const expected = decodeBase64Url(vapidPublicKey);
    if (current.length !== expected.length) {
        return false;
    }

    return current.every((value, index) => value === expected[index]);
}

function decodeBase64Url(value) {
    const padded = (value + '='.repeat((4 - (value.length % 4)) % 4))
        .replace(/-/g, '+')
        .replace(/_/g, '/');

    const raw = atob(padded);
    const bytes = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) {
        bytes[i] = raw.charCodeAt(i);
    }

    return bytes;
}
