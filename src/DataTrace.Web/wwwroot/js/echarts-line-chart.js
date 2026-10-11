const charts = new WeakMap();
const resizeObservers = new WeakMap();
const themeObservers = new WeakMap();
const optionsByElement = new WeakMap();

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
    const bottom = model.showAxis ? (model.annotated ? 48 : 30) : 4;
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
        xAxis: { ...axis, type: 'value', min: model.xMin, max: model.xMax, splitNumber: 4 },
        yAxis: { ...axis, type: 'value', min: model.yMin, max: model.yMax, splitNumber: 4 },
        series: model.series.map(series => ({
            name: series.name,
            type: 'line',
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
            triggerOn: 'mousemove',
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
                bottom: 4,
                silent: true,
                style: { text: model.xStartText, fill: axisColor, fontSize: 12 }
            },
            {
                type: 'text',
                right,
                bottom: 4,
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
        return;
    }

    if (!chart) {
        if (!window.echarts) {
            throw new Error('ECharts runtime was not loaded.');
        }

        chart = window.echarts.init(element, null, { renderer: 'canvas' });
        charts.set(element, chart);
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
    }
}
