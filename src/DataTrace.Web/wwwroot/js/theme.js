(function () {
    var storageKey = 'dt-theme-mode';

    function apply(isDark) {
        document.documentElement.classList.toggle('dt-theme-dark', !!isDark);
    }

    // 同时写一份 Cookie：服务端预渲染时要靠它决定首帧用哪套调色板。
    // 只存 localStorage 的话，MudThemeProvider 首帧只能是浅色，暗色用户每次都先闪一帧白底。
    function persist(isDark) {
        try {
            localStorage.setItem(storageKey, isDark ? 'dark' : 'light');
        } catch (e) { }
        try {
            document.cookie = storageKey + '=' + (isDark ? 'dark' : 'light')
                + '; path=/; max-age=31536000; samesite=lax';
        } catch (e) { }
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
            persist(isDark);
        }
    };

    // 首次访问还没有 Cookie 时，把 localStorage 里的偏好补写成 Cookie，
    // 这样下一次刷新就已经不需要等 JS 了。
    try {
        var stored = localStorage.getItem(storageKey);
        if (stored && document.cookie.indexOf(storageKey + '=') < 0) {
            persist(stored === 'dark');
        }
    } catch (e) { }

    window.dtTheme.get();
})();
