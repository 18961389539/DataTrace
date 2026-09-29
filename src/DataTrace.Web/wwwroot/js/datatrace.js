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
window.dtScrollIntoView = function (id) {
    if (!id) { return false; }
    var el = document.getElementById(id);
    if (!el) { return false; }
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    return true;
};

// 折线图悬停读数：面板锚在光标旁，显示离光标最近的那个采样点的数值
// （近右/下边缘时自动翻到另一侧）。数值与点位都由服务端渲染，这里只做
// "找最近的点 + 挪面板"，不发任何回服务端的消息。
//
// 用 document 委托而不是各图表各自 bind：读数只认 DOM 里已有的点位与 data-tip，
// 不需要 circuit 参与 —— 页面是预渲染直出、或 SignalR 断线时，读数照样能用。
(function () {
    var READOUT = '.dt-chart-readout';
    var active = null;
    var activePoint = null;
    var pending = null;

    function hide(el) {
        if (el) { el.hidden = true; }
    }

    function clearPoint() {
        if (activePoint) {
            activePoint.classList.remove('is-active');
            activePoint = null;
        }
    }

    function move(el, x, y) {
        var box = el.getBoundingClientRect();
        var left = x + 14;
        var top = y - 8;
        if (left + box.width > window.innerWidth - 8) {
            left = x - box.width - 14;
        }
        if (top + box.height > window.innerHeight - 8) {
            top = y - box.height - 8;
        }
        el.style.left = Math.max(8, left) + 'px';
        el.style.top = Math.max(8, top) + 'px';
    }

    function nearest(box, svg, ev) {
        // 点在 SVG 自己的坐标系里；鼠标是 CSS 像素，乘上 viewBox 缩放比再比距离。
        var view = svg.getAttribute('viewBox').split(' ');
        var rect = svg.getBoundingClientRect();
        var scale = rect.width ? parseFloat(view[2]) / rect.width : 0;
        var lx = (ev.clientX - rect.left) * scale;
        var ly = (ev.clientY - rect.top) * scale;

        var found = null;
        var best = Infinity;
        var circles = box.querySelectorAll('circle.dt-point');
        for (var j = 0; j < circles.length; j++) {
            var dx = circles[j].getAttribute('cx') - lx;
            var dy = circles[j].getAttribute('cy') - ly;
            var d = dx * dx + dy * dy;
            if (d < best) {
                best = d;
                found = circles[j];
            }
        }

        return found;
    }

    function update(ev) {
        var box = ev.target && ev.target.closest ? ev.target.closest('.dt-chart-box') : null;
        var readout = box ? box.querySelector(READOUT) : null;

        if (!readout) {
            // 移出图表（或进了迷你曲线）：收起面板，指针不用悬在图上也能清干净。
            hide(active);
            clearPoint();
            active = null;
            return;
        }

        var svg = box.querySelector('svg.dt-chart');
        var point = svg ? nearest(box, svg, ev) : null;
        var tip = point ? point.getAttribute('data-tip') : null;
        if (!tip) {
            return;
        }

        if (active && active !== readout) { hide(active); }
        active = readout;
        if (activePoint !== point) {
            clearPoint();
            point.classList.add('is-active');
            activePoint = point;
        }
        readout.textContent = tip;
        readout.hidden = false;
        move(readout, ev.clientX, ev.clientY);
    }

    // 鼠标事件比屏幕刷新密，几何计算按帧做一次就够（每帧遍历几十个点是这里的开销）。
    document.addEventListener('mousemove', function (ev) {
        if (pending) {
            pending.ev = ev;
            return;
        }

        pending = { ev: ev };
        window.requestAnimationFrame(function () {
            var frame = pending;
            pending = null;
            update(frame.ev);
        });
    }, { passive: true });
})();

// 大屏/车间模式：给 body 挂 class，偏好写入 localStorage；退出按钮始终由页面提供。

// Dashboard recent-collect collapse preference; same pattern as shop-floor. Default collapsed when unset.
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
