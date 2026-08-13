// JS interop for the session-detail SOC chart (issue #49). Thin on purpose: uPlot (vendored at
// lib/uplot/, see VENDORED.md) does the actual rendering; this only wires it to a DOM element id and
// keeps one instance per element so a Blazor Server circuit can dispose it cleanly on navigation.
window.solaxCharts = (function () {
    const charts = new Map();

    // Read once at render time rather than kept in sync with the OS theme: uPlot has no notion of a
    // live palette swap, and re-rendering on every prefers-color-scheme change is more machinery than
    // a chart that looks right until the next page load is worth.
    function cssVar(name, fallback) {
        const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return value || fallback;
    }

    function renderSocChart(elementId, timestampsUnixSeconds, socPercent) {
        const el = document.getElementById(elementId);
        if (!el || typeof uPlot === "undefined") {
            return;
        }

        dispose(elementId);

        const accent = cssVar("--accent", "#0b6b3a");
        const ink = cssVar("--ink", "#1c1f23");
        const line = cssVar("--line", "#e3e6ea");

        const opts = {
            width: el.clientWidth || 600,
            height: 260,
            scales: { x: { time: true } },
            cursor: { drag: { x: true, y: false } },
            series: [
                {},
                {
                    label: "Battery SOC",
                    stroke: accent,
                    width: 2,
                    fill: accent + "26", // ~15% alpha
                    value: (u, v) => (v == null ? "-" : v.toFixed(0) + "%"),
                },
            ],
            axes: [
                { stroke: ink, grid: { stroke: line } },
                { stroke: ink, grid: { stroke: line }, values: (u, vals) => vals.map((v) => v + "%") },
            ],
        };

        const chart = new uPlot(opts, [timestampsUnixSeconds, socPercent], el);

        const resize = () => chart.setSize({ width: el.clientWidth || 600, height: 260 });
        window.addEventListener("resize", resize);

        charts.set(elementId, { chart, resize });
    }

    function dispose(elementId) {
        const entry = charts.get(elementId);
        if (!entry) {
            return;
        }

        window.removeEventListener("resize", entry.resize);
        entry.chart.destroy();
        charts.delete(elementId);
    }

    return { renderSocChart, dispose };
})();
