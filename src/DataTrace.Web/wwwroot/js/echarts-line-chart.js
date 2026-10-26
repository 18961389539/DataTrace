const charts = new WeakMap();
const resizeObservers = new WeakMap();
const themeObservers = new WeakMap();
const optionsByElement = new WeakMap();
const readouts = new WeakMap();

function readoutValueText(value) {
    if (Array.isArray(value)) {
        return value.length > 1 ? readoutValueText(value[value.length - 1]) : String(value[0]);
    }

    return value === null || value === undefined ? '—' : String(value);
}

function buildReadout(chart, dataIndex) {
    const option = chart.getOption();
    const seriesList = Array.isArray(option.series) ? option.series : [];
    const lines = [];
    let axisValue = null;

    seriesList.forEach(series => {
        const data = Array.isArray(series.data) ? series.data : [];
        const item = data[dataIndex];
        if (item === null || item === undefined) {
            return;
        }

        const raw = item && typeof item === 'object' && 'value' in item ? item.value : item;
        if (axisValue === null) {
            axisValue = Array.isArray(raw) ? raw[0] : dataIndex;
        }

        const text = item && typeof item === 'object' && item.tip
            ? item.tip
            : `${series.name || '数值'}：${readoutValueText(raw)}`;
        lines.push(text);
    });

    if (lines.length === 0) {
        return null;
    }

    const prefix = axisValue === null ? '' : `X ${readoutValueText(axisValue)} · `;
    return `${prefix}${lines.join(' · ')}`;
}

function pin(state, chart, index, hint) {
    const text = buildReadout(chart, index);
    if (!text) {
        return;
    }

    state.index = index;
    state.panel.textContent = `${text}（${hint}）`;
    state.panel.hidden = false;
}

function ensureReadout(element, chart) {
    if (readouts.has(element)) {
        return readouts.get(element);
    }

    const host = element.parentElement;
    if (!host) {
        return null;
    }

    const panel = document.createElement('div');
    panel.className = 'dt-chart-readout';
    panel.setAttribute('aria-live', 'polite');
    panel.hidden = true;
    host.appendChild(panel);

    const state = { index: -1, panel: panel, enableZoom: false };
    readouts.set(element, state);

    // 点击任意横向位置固定最近采样点。不能只靠 ECharts 的 series click：
    // 折线很细，点在图中间空白处时拿不到 series 事件。
    element.addEventListener('click', event => {
        const instance = charts.get(element);
        if (!instance) {
            return;
        }

        const rect = element.getBoundingClientRect();
        const xPixel = event.clientX - rect.left;
        const yPixel = event.clientY - rect.top;
        if (xPixel < 0 || xPixel > rect.width || yPixel < 0 || yPixel > rect.height) {
            return;
        }

        // 底部是缩放滑块，点它不是为了取数。
        if (state.enableZoom && yPixel > rect.height - 30) {
            return;
        }

        let xValue;
        try {
            xValue = Number(instance.convertFromPixel({ xAxisIndex: 0 }, xPixel));
        } catch (error) {
            return;
        }

        if (!Number.isFinite(xValue)) {
            return;
        }

        const option = instance.getOption();
        const series = Array.isArray(option.series) ? option.series[0] : null;
        const data = Array.isArray(series?.data) ? series.data : [];
        let bestIndex = -1;
        let bestDistance = Infinity;

        data.forEach((item, index) => {
            const raw = item && typeof item === 'object' && 'value' in item ? item.value : item;
            const x = Array.isArray(raw) ? Number(raw[0]) : index;
            if (!Number.isFinite(x)) {
                return;
            }

            const distance = Math.abs(x - xValue);
            if (distance < bestDistance) {
                bestDistance = distance;
                bestIndex = index;
            }
        });

        if (bestIndex >= 0) {
            pin(state, instance, bestIndex, '已固定：按 Esc 取消');
        }
    });

    element.addEventListener('keydown', event => {
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight' && event.key !== 'Escape') {
            return;
        }

        event.preventDefault();
        const option = chart.getOption();
        const seriesList = Array.isArray(option.series) ? option.series : [];
        const count = seriesList.reduce((max, series) =>
            Math.max(max, Array.isArray(series.data) ? series.data.length : 0), 0);

        if (event.key === 'Escape') {
            state.index = -1;
            panel.hidden = true;
            panel.textContent = '';
            chart.dispatchAction({ type: 'hideTip' });
            return;
        }

        if (count === 0) {
            return;
        }

        const delta = event.key === 'ArrowRight' ? 1 : -1;
        state.index = state.index < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.min(count - 1, Math.max(0, state.index + delta));

        chart.dispatchAction({ type: 'showTip', seriesIndex: 0, dataIndex: state.index });
        pin(state, chart, state.index, '方向键移动，Esc 取消');
    });

    return state;
}

function resolveColor(element, color) {
    let value = color;
    for (let depth = 0; depth < 4; depth++) {
        const match = /^var\((--[\w-]+)\)$/.exec(value.trim());
        if (!match) {
            return value;
        }

        value = getComputedStyle(element).getPropertyValue(match[1]).trim();
    }

    return value;
}

function formatAxisValue(value) {
    const number = Number(value);
    const absolute = Math.abs(number);
    if (absolute >= 100000) {
        return number.toExponential(1).replace('e', 'E');
    }

    const digits = absolute >= 100 ? 0 : absolute >= 1 ? 1 : 3;
    return number.toFixed(digits);
}

function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, character => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    })[character]);
}

function createOption(element, model) {
    const style = getComputedStyle(element);
    const axisColor = resolveColor(element, style.getPropertyValue('--dt-chart-axis').trim());
    const gridColor = resolveColor(element, style.getPropertyValue('--dt-chart-grid').trim());
    const surfaceColor = resolveColor(element, style.getPropertyValue('--mud-palette-surface').trim());
    const textColor = resolveColor(element, style.getPropertyValue('--mud-palette-text-primary').trim());
    const borderColor = resolveColor(element, style.getPropertyValue('--mud-palette-lines-default').trim());
    const left = model.showAxis ? 58 : 4;
    const right = 14;
    const top = model.showAxis ? 12 : 4;
    const bottom = model.showAxis ? (model.annotated ? 78 : 58) : 4;
    const axis = {
        show: model.showAxis,
        axisTick: { show: false },
        axisLine: { show: model.showAxis, lineStyle: { color: axisColor } },
        axisLabel: {
            show: model.showAxis,
            color: axisColor,
            hideOverlap: true,
            formatter: formatAxisValue
        },
        splitLine: { show: model.showAxis, lineStyle: { color: gridColor, width: 1 } }
    };

    return {
        animation: false,
        aria: {
            enabled: true,
            description: model.ariaLabel,
            decal: { show: false }
        },
        color: model.series.map(series => resolveColor(element, series.color)),
        grid: { left, right, top, bottom, containLabel: false },
        xAxis: {
            ...axis,
            type: model.xType || 'value',
            min: model.xMin,
            max: model.xMax,
            splitNumber: 4,
            axisLabel: {
                ...axis.axisLabel,
                formatter: model.xType === 'time'
                    ? value => window.echarts.format.formatTime('MM-dd hh:mm', value)
                    : formatAxisValue
            }
        },
        yAxis: { ...axis, type: 'value', min: model.yMin, max: model.yMax, splitNumber: 4 },
        dataZoom: model.showAxis && model.enableZoom ? [
            {
                type: 'inside',
                filterMode: 'none',
                zoomOnMouseWheel: true,
                moveOnMouseMove: true
            },
            {
                type: 'slider',
                filterMode: 'none',
                height: 18,
                bottom: 4,
                borderColor,
                backgroundColor: surfaceColor,
                fillerColor: 'rgba(72, 128, 176, .16)',
                handleSize: 12,
                showDetail: false
            }
        ] : [],
        series: model.series.map(series => ({
            name: series.name,
            type: 'line',
            step: series.isDiscrete ? 'end' : false,
            showSymbol: series.showSymbol,
            symbol: 'circle',
            symbolSize: 7,
            connectNulls: false,
            lineStyle: { width: 2 },
            emphasis: { focus: 'series' },
            data: series.data
        })),
        tooltip: model.showAxis ? {
            trigger: 'axis',
            triggerOn: 'mousemove|click',
            renderMode: 'html',
            className: 'echarts-tooltip',
            confine: true,
            axisPointer: { type: 'line', lineStyle: { color: axisColor } },
            backgroundColor: surfaceColor,
            borderColor,
            textStyle: { color: textColor, fontSize: 13 },
            extraCssText: 'border-radius:4px;box-shadow:0 4px 14px rgba(15,32,51,.18);',
            formatter: parameters => (Array.isArray(parameters) ? parameters : [parameters])
                .map(parameter => parameter.data && parameter.data.tip ? escapeHtml(parameter.data.tip) : '')
                .filter(Boolean)
                .join('<br/>')
        } : { show: false },
        graphic: model.showAxis && model.annotated ? [
            {
                type: 'text',
                left,
                bottom: model.enableZoom ? 28 : 4,
                silent: true,
                style: { text: model.xStartText, fill: axisColor, fontSize: 12 }
            },
            {
                type: 'text',
                right,
                bottom: model.enableZoom ? 28 : 4,
                silent: true,
                style: { text: model.xEndText, fill: axisColor, fontSize: 12, textAlign: 'right' }
            }
        ] : []
    };
}

export function update(element, model) {
    let chart = charts.get(element);
    if (!model.series.length) {
        if (chart) {
            chart.clear();
        }
        const state = readouts.get(element);
        if (state) {
            state.index = -1;
            state.panel.hidden = true;
            state.panel.textContent = '';
        }
        return;
    }

    if (!chart) {
        if (!window.echarts) {
            throw new Error('ECharts runtime was not loaded.');
        }

        chart = window.echarts.init(element, null, { renderer: 'canvas' });
        charts.set(element, chart);
        ensureReadout(element, chart);
        const resizeObserver = new ResizeObserver(() => chart.resize());
        resizeObserver.observe(element);
        resizeObservers.set(element, resizeObserver);
        const themeObserver = new MutationObserver(() => {
            const options = optionsByElement.get(element);
            if (options) {
                chart.setOption(createOption(element, options), { notMerge: true, lazyUpdate: true });
            }
        });
        themeObserver.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ['class', 'data-theme']
        });
        themeObservers.set(element, themeObserver);
    }

    optionsByElement.set(element, model);
    chart.setOption(createOption(element, model), { notMerge: true, lazyUpdate: true });
    const readoutState = readouts.get(element);
    if (readoutState) {
        readoutState.enableZoom = model.showAxis && model.enableZoom;
    }
    if (readoutState && !readoutState.panel.hidden && readoutState.index >= 0) {
        // 数据在实时刷新：固定读数跟着同一索引更新，别停在一分钟前的值上。
        const refreshed = buildReadout(chart, readoutState.index);
        if (refreshed) {
            readoutState.panel.textContent = `${refreshed}（已固定：按 Esc 取消）`;
        }
    }
    // 同组的图共享轴指针与提示（联动游标）：echarts.connect 对同组注册是幂等的。
    const group = model.connectGroup || null;
    if (group) {
        chart.group = group;
        window.echarts.connect(group);
    } else if (chart.group) {
        chart.group = null;
    }
    chart.resize();
}

export function dispose(element) {
    const chart = charts.get(element);
    if (chart) {
        resizeObservers.get(element)?.disconnect();
        themeObservers.get(element)?.disconnect();
        chart.dispose();
        charts.delete(element);
        resizeObservers.delete(element);
        themeObservers.delete(element);
        optionsByElement.delete(element);
        const state = readouts.get(element);
        state?.panel.remove();
        readouts.delete(element);
    }
}
