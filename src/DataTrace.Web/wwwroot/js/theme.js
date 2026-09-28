(function () {
    var storageKey = 'dt-theme-mode';

    function apply(isDark) {
        document.documentElement.classList.toggle('dt-theme-dark', !!isDark);
    }

    window.dtTheme = {
        get: function () {
            var isDark = false;
            try { isDark = localStorage.getItem(storageKey) === 'dark'; } catch (e) { }
            apply(isDark);
            return isDark;
        },
        set: function (isDark) {
            isDark = !!isDark;
            apply(isDark);
            try { localStorage.setItem(storageKey, isDark ? 'dark' : 'light'); } catch (e) { }
        }
    };

    window.dtTheme.get();
})();
