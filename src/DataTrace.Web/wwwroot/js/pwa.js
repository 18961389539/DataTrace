(function () {
    let deferredInstallPrompt = null;
    let installButton = null;

    function isInstalled() {
        return window.matchMedia("(display-mode: standalone)").matches
            || window.navigator.standalone === true;
    }

    function notifyInstallButton() {
        if (!installButton) {
            return;
        }

        installButton.invokeMethodAsync(
            "PwaInstallStateChanged",
            !!deferredInstallPrompt && !isInstalled()
        ).catch(() => { });
    }

    window.addEventListener("beforeinstallprompt", event => {
        event.preventDefault();
        deferredInstallPrompt = event;
        notifyInstallButton();
    });

    window.addEventListener("appinstalled", () => {
        deferredInstallPrompt = null;
        notifyInstallButton();
    });

    function registerServiceWorker() {
        if (!("serviceWorker" in navigator)) {
            return;
        }

        navigator.serviceWorker
            .register(new URL("service-worker.js", document.baseURI), { updateViaCache: "none" })
            .catch(error => console.warn("DataTrace PWA worker registration failed.", error));
    }

    if (document.readyState === "complete") {
        registerServiceWorker();
    } else {
        window.addEventListener("load", registerServiceWorker, { once: true });
    }

    window.dtPwa = {
        initialize: function (dotNetReference) {
            installButton = dotNetReference;
            notifyInstallButton();
        },
        install: async function () {
            if (!deferredInstallPrompt) {
                return "unavailable";
            }

            const prompt = deferredInstallPrompt;
            deferredInstallPrompt = null;
            await prompt.prompt();
            const result = await prompt.userChoice;
            notifyInstallButton();
            return result.outcome;
        },
        dispose: function (dotNetReference) {
            if (installButton === dotNetReference) {
                installButton = null;
            }
        }
    };
})();
