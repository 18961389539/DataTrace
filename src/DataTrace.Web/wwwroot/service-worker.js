// This app requires its live Blazor Server connection. Do not cache navigations,
// framework files, API responses, or SignalR traffic as if they were offline data.
self.addEventListener("install", event => {
    event.waitUntil(self.skipWaiting());
});

self.addEventListener("activate", event => {
    event.waitUntil(self.clients.claim());
});
