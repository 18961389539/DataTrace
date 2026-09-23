// DataTrace 前端小工具。避免使用 eval，便于日后启用 CSP。
window.dtDownload = function (fileName, contentBase64) {
    var binary = atob(contentBase64);
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }

    var blob = new Blob([bytes], { type: 'text/csv;charset=utf-8' });
    var url = URL.createObjectURL(blob);
    var link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
};

// 导航后把焦点移到页标题，读屏与键盘用户才知道页面换了。
// 标题本身不可聚焦，必须补 tabindex="-1" —— 框架的 FocusOnNavigate 就是这么做的，
// 少了这一步 el.focus() 是空操作，焦点会留在 body 上。
window.dtFocusPageTitle = function (selector) {
    var el = document.querySelector(selector);
    if (!el) {
        return;
    }

    if (!el.hasAttribute('tabindex')) {
        el.setAttribute('tabindex', '-1');
    }

    el.focus({ preventScroll: true });
};

// 复制文本到剪贴板，用于托盘码/流水号一键复制。
// 返回值必须可信：界面按它决定提示"已复制"还是"复制失败"，
// 而工控机通常走 http://局域网IP，非安全上下文下 navigator.clipboard 是不可用的。
window.dtCopy = async function (text) {
    if (!text) { return false; }
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            return false;
        }
    }

    var area = document.createElement('textarea');
    area.value = text;
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    document.body.removeChild(area);
    return !!ok;
};

// 登录走原生 POST（登录发生在 SignalR 建立之前），Blazor 拦不住连点，
// 所以在提交那一刻自己把按钮锁掉。失败回跳后是全新页面，按钮会自动恢复可用。
(function () {
    function lock(form) {
        form.addEventListener('submit', function () {
            var btn = form.querySelector('button[type=submit]');
            if (!btn || btn.disabled) { return; }
            btn.disabled = true;
            btn.textContent = '登录中…';
        });
    }

    function lockAll() {
        var forms = document.querySelectorAll('form[action="/account/login"]');
        for (var i = 0; i < forms.length; i++) { lock(forms[i]); }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', lockAll);
    } else {
        lockAll();
    }
})();
