// JS interop for the session-detail SOC chart (issue #49) and the forecast plan timeline
// (issue #50). Thin on purpose: uPlot (vendored at lib/uplot/, see VENDORED.md) does the actual
// rendering; this only wires it to a DOM element id and keeps one instance per element so a Blazor
// Server circuit can dispose it cleanly on navigation.
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

    // The forecast plan timeline (issue #50): forecast surplus against the charge window, with the
    // required-SOC-floor projection overlaid on a second axis -- one picture for what the entity list
    // on its own can't communicate. Recreated (not patched via setData) on every poll like the rest of
    // the plan page's live figures; a plan updates every few seconds, so a brief redraw is the honest
    // cost of staying current rather than a defect to work around.
    function renderTimelineChart(elementId, timestampsUnixSeconds, surplusWatts, floorPercent, windowStartSec, windowEndSec) {
        const el = document.getElementById(elementId);
        if (!el || typeof uPlot === "undefined") {
            return;
        }

        dispose(elementId);

        const accent = cssVar("--accent", "#0b6b3a");
        const amber = cssVar("--amber", "#f5b400");
        const ink = cssVar("--ink", "#1c1f23");
        const line = cssVar("--line", "#e3e6ea");

        const opts = {
            width: el.clientWidth || 600,
            height: 280,
            scales: {
                x: { time: true },
                w: {},
                p: { range: [0, 100] },
            },
            cursor: { drag: { x: true, y: false } },
            series: [
                {},
                {
                    label: "Forecast surplus",
                    scale: "w",
                    stroke: accent,
                    width: 2,
                    fill: accent + "26",
                    value: (u, v) => (v == null ? "-" : Math.round(v) + " W"),
                },
                {
                    label: "Required SOC floor",
                    scale: "p",
                    stroke: amber,
                    width: 2,
                    dash: [4, 4],
                    value: (u, v) => (v == null ? "-" : Math.round(v) + "%"),
                },
            ],
            axes: [
                { stroke: ink, grid: { stroke: line } },
                // Explicit size: uPlot's auto-sizing left a 4-digit "6000 W" label clipped against the
                // canvas edge at the default gutter width.
                { scale: "w", stroke: ink, grid: { stroke: line }, size: 64, values: (u, vals) => vals.map((v) => v + " W") },
                { scale: "p", side: 1, stroke: ink, grid: { show: false }, size: 50, values: (u, vals) => vals.map((v) => v + "%") },
            ],
            hooks: {
                // A shaded band for the charge window, drawn behind the series -- the picture the
                // eleven separate numbers can't give: surplus, the floor, and the window all at once.
                draw: [
                    (u) => {
                        if (windowStartSec == null || windowEndSec == null) {
                            return;
                        }

                        const x0 = u.valToPos(windowStartSec, "x", true);
                        const x1 = u.valToPos(windowEndSec, "x", true);
                        const ctx = u.ctx;

                        ctx.save();
                        ctx.fillStyle = accent + "1a";
                        ctx.fillRect(x0, u.bbox.top, x1 - x0, u.bbox.height);
                        ctx.restore();
                    },
                ],
            },
        };

        const chart = new uPlot(opts, [timestampsUnixSeconds, surplusWatts, floorPercent], el);

        const resize = () => chart.setSize({ width: el.clientWidth || 600, height: 280 });
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

    return { renderSocChart, renderTimelineChart, dispose };
})();
