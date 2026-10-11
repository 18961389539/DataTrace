// DataTrace 前端小工具。避免使用 eval，便于日后启用 CSP。
// mime 可选：报表 PDF 传 application/pdf；历史调用不传，默认还是 text/csv（带 BOM 的 UTF-8）。
window.dtDownload = function (fileName, contentBase64, mime) {
    var binary = atob(contentBase64);
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }

    var blob = new Blob([bytes], { type: mime || 'text/csv;charset=utf-8' });
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

// Download large exports over HTTP, keeping the CSV out of Blazor's SignalR payloads.
window.dtDownloadUrl = async function (fileName, url) {
    var response = await fetch(url, { credentials: 'same-origin' });
    if (!response.ok || !response.headers.get('content-type')?.includes('text/csv')) {
        throw new Error('下载请求失败 (' + response.status + ')');
    }

    var blob = await response.blob();
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

// 登录页密码显隐。登录走原生 POST（发生在 SignalR 建立之前），circuit 起来之前按钮也必须能用；
// 而首次交互渲染会把这段表单节点重建一遍，直接挂在按钮上的监听会丢。
// 所以监听委托在 document 上，按钮只认 data-dt-pass-toggle，状态只落在 aria-pressed（样式按它换眼睛图标）。
(function () {
    document.addEventListener('click', function (e) {
        var btn = e.target && e.target.closest && e.target.closest('[data-dt-pass-toggle]');
        if (!btn) { return; }

        var wrap = btn.closest('[data-dt-pass]');
        var input = wrap ? wrap.querySelector('input') : null;
        if (!input || (input.type !== 'password' && input.type !== 'text')) { return; }

        var show = input.type === 'password';
        input.type = show ? 'text' : 'password';
        btn.setAttribute('aria-pressed', show ? 'true' : 'false');
        btn.setAttribute('aria-label', show ? '隐藏密码' : '显示密码');
    });
})();


// 系统设置分区锚点滚动（平滑；缺失元素时静默）。
window.dtFocusValidation = async function (field) {
    await new Promise(function (resolve) { requestAnimationFrame(function () { requestAnimationFrame(resolve); }); });
    var root = Array.from(document.querySelectorAll('[data-dt-validation]'))
        .find(function (element) { return element.getAttribute('data-dt-validation') === field; });
    if (!root) return false;
    var input = root.matches('input,button,[tabindex]') ? root : root.querySelector('input,button,[tabindex]');
    if (!input) return false;
    root.scrollIntoView({ block: 'center', behavior: 'instant' });
    input.focus({ preventScroll: true });
    return document.activeElement === input;
};

window.dtScrollIntoView = function (id) {
    if (!id) { return false; }
    var el = document.getElementById(id);
    if (!el) { return false; }
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    return true;
};

// 大屏/车间模式：给 body 挂 class，偏好写入 localStorage；退出按钮始终由页面提供。

// Dashboard recent-collect collapse preference; same pattern as shop-floor. Default collapsed when unset.
window.dtQueryPresets = {
    get: function (account) {
        var stored = localStorage.getItem('dt-query-presets:' + account);
        if (!stored) return [];
        var presets = JSON.parse(stored);
        if (!Array.isArray(presets)) throw new Error('Invalid query presets');
        if (!presets.every(function (preset) {
            return preset && typeof preset.name === 'string' && typeof preset.url === 'string' && typeof preset.range === 'string';
        })) throw new Error('Invalid query preset fields');
        return presets;
    },
    set: function (account, presets) {
        try {
            localStorage.setItem('dt-query-presets:' + account, JSON.stringify(presets));
            return true;
        } catch (error) { return false; }
    }
};

window.dtRecentOpen = {
    key: 'dt-recent-open',
    get: function () {
        try { return localStorage.getItem(this.key) === '1'; } catch (e) { return false; }
    },
    set: function (on) {
        try { localStorage.setItem(this.key, on ? '1' : '0'); } catch (e) { }
        return !!on;
    }
};

// 审计日志页「存储位置与保留策略」的展开状态；默认折叠，与最近采集面板同一套路。
window.dtLogNotice = {
    key: 'dt-log-notice',
    get: function () {
        try { return localStorage.getItem(this.key) === '1'; } catch (e) { return false; }
    },
    set: function (on) {
        try { localStorage.setItem(this.key, on ? '1' : '0'); } catch (e) { }
        return !!on;
    }
};

window.dtQueryScroll = {
    key: 'dt-query-scroll',
    save: function (url) {
        var table = document.querySelector('.dt-query-results .mud-table-container');
        try {
            sessionStorage.setItem(this.key, JSON.stringify({ url: url, top: window.scrollY, table: table ? table.scrollTop : 0 }));
        } catch (error) { }
    },
    restore: function (url) {
        try {
            var state = JSON.parse(sessionStorage.getItem(this.key) || 'null');
            if (!state || state.url !== url) { return; }
            requestAnimationFrame(function () {
                var table = document.querySelector('.dt-query-results .mud-table-container');
                if (table) { table.scrollTop = state.table; }
                window.scrollTo(0, state.top);
            });
            sessionStorage.removeItem(this.key);
        } catch (error) { }
    }
};
document.addEventListener('click', function (event) {
    var link = event.target.closest && event.target.closest('.dt-query-results a[href*="/query/"]');
    if (!link) { return; }
    var table = link.closest('.dt-query-results');
    window.dtQueryScroll.save(table.getAttribute('data-dt-query-key'));
});

window.dtShopFloor = {
    key: 'dt-shopfloor',
    get: function () {
        try { return localStorage.getItem(this.key) === '1'; } catch (e) { return false; }
    },
    set: function (on) {
        try { localStorage.setItem(this.key, on ? '1' : '0'); } catch (e) { }
        document.body.classList.toggle('dt-shopfloor', !!on);
        return !!on;
    }
};
(function () {
    try {
        if (window.dtShopFloor.get()) {
            document.body.classList.add('dt-shopfloor');
        }
    } catch (e) { }
})();

// 异常呼叫：方波短鸣，不依赖音频文件。浏览器未授权自动播放时，点「重新响铃」会在用户手势里恢复。
window.dtAlarm = (function () {
    var ctx = null;
    var timer = null;

    function beep() {
        var AudioCtx = window.AudioContext || window.webkitAudioContext;
        if (!AudioCtx) {
            return;
        }

        if (!ctx) {
            ctx = new AudioCtx();
        }

        if (ctx.state === 'suspended') {
            ctx.resume();
        }

        var osc = ctx.createOscillator();
        var gain = ctx.createGain();
        osc.type = 'square';
        osc.frequency.value = 880;
        gain.gain.setValueAtTime(0.0001, ctx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.08, ctx.currentTime + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.18);
        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.start();
        osc.stop(ctx.currentTime + 0.2);
    }

    return {
        start: function () {
            if (timer) {
                return;
            }

            beep();
            timer = setInterval(beep, 1400);
        },
        stop: function () {
            if (!timer) {
                return;
            }

            clearInterval(timer);
            timer = null;
        }
    };
})();

// 页面可见性：切到后台时通知 .NET。
// 诊断页据此挂起按秒的轮询 —— 每个开着的标签页都在取数，没人看的时候不该继续占着服务端。
window.dtVisibility = (function () {
    var dotNetReference = null;

    function notify() {
        if (!dotNetReference) {
            return;
        }

        // 切页竞态（回调落到已释放的对象上）不该在控制台喧哗：出错了当没人订阅就是。
        dotNetReference.invokeMethodAsync('OnVisibilityChanged', document.hidden).catch(function () { });
    }

    document.addEventListener('visibilitychange', notify);

    return {
        initialize: function (reference) {
            dotNetReference = reference;
            // 打开时就在后台（新标签页、切走的旧页签）也要先报一次。
            notify();
        },
        dispose: function (reference) {
            if (dotNetReference === reference) {
                dotNetReference = null;
            }
        }
    };
})();
