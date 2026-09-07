// JS interop for the session-detail chart (issues #49 and #175), the forecast plan timeline (issue
// #50) and the energy day chart (issue #173). Thin on purpose: uPlot (vendored at lib/uplot/, see
// VENDORED.md) does the actual rendering; this only wires it to a DOM element id and keeps one
// instance per element so a Blazor Server circuit can dispose it cleanly on navigation.
window.solaxCharts = (function () {
    const charts = new Map();

    // Read once at render time rather than kept in sync with the OS theme: uPlot has no notion of a
    // live palette swap, and re-rendering on every prefers-color-scheme change is more machinery than
    // a chart that looks right until the next page load is worth.
    function cssVar(name, fallback) {
        const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return value || fallback;
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

    // The energy history as a day rather than as ninety-six rows (issue #173): what the roof made,
    // what crossed the meter each way, what the car took and where the battery sat, all against one
    // midnight-to-midnight axis. Modelled on the inverter portal's own day view, because that is the
    // picture the owner already reads -- with two things it cannot do: the forecast overlaid on the
    // solar it was a forecast of, and the stretches nobody observed left as holes.
    //
    // One payload object rather than a dozen positional arguments; the field names are what
    // EnergyChartSeries serialises to. All the arithmetic lives there, on purpose: this function
    // decides colours and shapes and nothing else.
    function renderEnergyDayChart(elementId, day) {
        const el = document.getElementById(elementId);
        if (!el || typeof uPlot === "undefined") {
            return;
        }

        dispose(elementId);

        const ink = cssVar("--ink", "#1c1f23");
        const line = cssVar("--line", "#e3e6ea");
        const muted = cssVar("--muted", "#6b7280");
        const solar = cssVar("--solar", "#f5b400");
        const gridColour = cssVar("--grid-flow", "#6b7280");
        const battery = cssVar("--accent", "#0b6b3a");
        const ev = cssVar("--ev", "#b45309");
        const house = cssVar("--house", "#7c3aed");

        // A bucket is a rectangle over a quarter hour, not a point in the middle of one. Stepped
        // paths draw what was recorded; a linear interpolation would draw slopes nothing did.
        const stepped = uPlot.paths.stepped({ align: 1 });
        const watts = (u, v) => (v == null ? "-" : Math.round(v).toLocaleString() + " W");

        // Points off on every series: ninety-six buckets a day, seven lines, and a marker on each one
        // is a chart made of dots. The cursor still reads every series into the legend.
        const power = (label, stroke, fill) => ({
            label,
            scale: "w",
            stroke,
            width: 2,
            fill: fill === false ? undefined : stroke + "26", // ~15% alpha
            paths: stepped,
            points: { show: false },
            value: watts,
        });

        const series = [{}, power("Solar", solar)];
        const data = [day.timestamps, day.solar];

        if (day.hasForecast) {
            // Dashed, and in the solar colour rather than a neutral one: it is a forecast *of that
            // line*, and two grey series (this and the meter) would be two grey series. Where the day
            // went as predicted the dashes disappear into the solar line, which is the right picture.
            series.push({
                label: "Forecast",
                scale: "w",
                stroke: solar,
                width: 1,
                dash: [4, 4],
                paths: stepped,
                points: { show: false },
                value: watts,
            });
            data.push(day.forecast);
        }

        series.push(
            power("Grid", gridColour, false),
            power("Battery", battery, false),
            power("EV charger", ev),
            power("House", house, false),
            {
                label: "Battery SOC",
                scale: "p",
                stroke: battery,
                width: 1,
                dash: [2, 3],
                paths: stepped,
                points: { show: false },
                value: (u, v) => (v == null ? "-" : Math.round(v) + "%"),
            });
        data.push(day.grid, day.battery, day.ev, day.house, day.soc);

        const opts = {
            width: el.clientWidth || 600,
            height: 320,
            scales: {
                // The whole day, whatever part of it has rows -- a morning's data is a morning, not a
                // full-width chart that happens to stop at noon.
                x: { time: true, range: [day.dayStart, day.dayEnd] },
                w: {},
                p: { range: [0, 100] },
            },
            cursor: { drag: { x: true, y: false } },
            series,
            axes: [
                { stroke: ink, grid: { stroke: line } },
                // Wider than the plan chart's gutter: this axis goes below zero, and a "-4,000 W" label
                // that does not fit loses its minus sign against the canvas edge rather than wrapping.
                { scale: "w", stroke: ink, grid: { stroke: line }, size: 78, values: (u, vals) => vals.map((v) => v.toLocaleString() + " W") },
                { scale: "p", side: 1, stroke: ink, grid: { show: false }, size: 50, values: (u, vals) => vals.map((v) => v + "%") },
            ],
            hooks: {
                draw: [
                    (u) => {
                        const ctx = u.ctx;
                        ctx.save();

                        // The windows the service only partly saw. Marked rather than warned about,
                        // like the italic rows in the table: their figures are averages over what was
                        // observed, which is honest, but a reader must be able to see which ones.
                        ctx.fillStyle = muted + "88";
                        for (let i = 0; i < day.partialStarts.length; i++) {
                            const x0 = u.valToPos(day.partialStarts[i], "x", true);
                            const x1 = u.valToPos(day.partialEnds[i], "x", true);
                            ctx.fillRect(x0, u.bbox.top + u.bbox.height - 7, Math.max(x1 - x0, 2), 7);
                        }

                        // Zero matters here in a way it does not on the other charts: grid and battery
                        // are signed, and which side of this line they are on is the whole reading.
                        const y0 = u.valToPos(0, "w", true);
                        if (y0 >= u.bbox.top && y0 <= u.bbox.top + u.bbox.height) {
                            ctx.strokeStyle = ink;
                            ctx.globalAlpha = 0.35;
                            ctx.beginPath();
                            ctx.moveTo(u.bbox.left, y0);
                            ctx.lineTo(u.bbox.left + u.bbox.width, y0);
                            ctx.stroke();
                        }

                        ctx.restore();
                    },
                ],
            },
        };

        // The site's zone, not the browser's: the table underneath is in the site's local time, and a
        // chart an hour out of step with the rows below it is worse than no chart. Probed once rather
        // than trusted -- an id Intl has never heard of (a Windows zone name that had no IANA mapping)
        // would otherwise throw on the first tick label and leave the page with no chart at all, and
        // the browser's own zone is a far better answer than that.
        try {
            uPlot.tzDate(new Date(), day.timeZoneId);
            opts.tzDate = (ts) => uPlot.tzDate(new Date(ts * 1000), day.timeZoneId);
        } catch {
            // Left to uPlot's default, which is the browser's local time.
        }

        const chart = new uPlot(opts, data, el);

        const resize = () => chart.setSize({ width: el.clientWidth || 600, height: 320 });
        window.addEventListener("resize", resize);

        charts.set(elementId, { chart, resize });
    }

    // The session as every meter saw it (issue #175), replacing the single SOC line #49 started with.
    // Same vocabulary as the day chart above -- signed grid and battery, watts left, per cent right,
    // the forecast dashed over the solar it was a forecast of -- over the session's own window, plus
    // the three things a session has and a calendar day does not: the car's own SOC, the stretches the
    // battery hold was armed for, and a rule at each moment something changed.
    //
    // One payload object, the field names being what SessionChartSeries serialises to. Straight lines,
    // not stepped: a sample is a reading at an instant, where an energy bucket is a rectangle over a
    // quarter hour. All the arithmetic is on the C# side; this decides colours and shapes only.
    function renderSessionChart(elementId, session) {
        const el = document.getElementById(elementId);
        if (!el || typeof uPlot === "undefined") {
            return;
        }

        dispose(elementId);

        const ink = cssVar("--ink", "#1c1f23");
        const line = cssVar("--line", "#e3e6ea");
        const muted = cssVar("--muted", "#6b7280");
        const solar = cssVar("--solar", "#f5b400");
        const gridColour = cssVar("--grid-flow", "#6b7280");
        const battery = cssVar("--accent", "#0b6b3a");
        const ev = cssVar("--ev", "#b45309");
        const vehicle = cssVar("--vehicle", "#2563eb");

        const watts = (u, v) => (v == null ? "-" : Math.round(v).toLocaleString() + " W");
        const percent = (u, v) => (v == null ? "-" : Math.round(v) + "%");

        // Points left to uPlot: a three-hour charge at a 30-second cadence is a chart made of dots if
        // they are forced on, and a session that lasted six samples is unreadable without them. The
        // default shows them exactly when there is room, which is the rule we would have written.
        const power = (label, stroke, fill) => ({
            label,
            scale: "w",
            stroke,
            width: 2,
            fill: fill === false ? undefined : stroke + "26", // ~15% alpha
            value: watts,
        });

        const series = [{}, power("Solar", solar)];
        const data = [session.timestamps, session.solar];

        if (session.hasForecast) {
            series.push({ label: "Forecast", scale: "w", stroke: solar, width: 1, dash: [4, 4], value: watts });
            data.push(session.forecast);
        }

        series.push(power("Grid", gridColour, false), power("Battery", battery, false), power("EV charger", ev));
        data.push(session.grid, session.battery, session.ev);

        series.push({ label: "Battery SOC", scale: "p", stroke: battery, width: 1, dash: [2, 3], value: percent });
        data.push(session.soc);

        if (session.hasVehicleSoc) {
            // Stepped, and stepped alone among these series: the car reports a reading taken at a time
            // of its own choosing and then says nothing for hours. A sloped line between two of those
            // would draw a charge curve the car never sent.
            series.push({
                label: "Car SOC",
                scale: "p",
                stroke: vehicle,
                width: 2,
                paths: uPlot.paths.stepped({ align: 1 }),
                value: percent,
            });
            data.push(session.vehicleSoc);
        }

        const opts = {
            width: el.clientWidth || 600,
            height: 320,
            scales: {
                // The session's own window, whatever part of it has samples -- a charge the service
                // only saw the end of is the end of a charge, not a full-width chart.
                x: { time: true, range: [session.windowStart, session.windowEnd] },
                w: {},
                p: { range: [0, 100] },
            },
            cursor: { drag: { x: true, y: false } },
            series,
            axes: [
                { stroke: ink, grid: { stroke: line } },
                // Wide gutter, as on the day chart: this axis goes below zero, and a "-4,000 W" label
                // that does not fit loses its minus sign against the canvas edge rather than wrapping.
                { scale: "w", stroke: ink, grid: { stroke: line }, size: 78, values: (u, vals) => vals.map((v) => v.toLocaleString() + " W") },
                { scale: "p", side: 1, stroke: ink, grid: { show: false }, size: 50, values: (u, vals) => vals.map((v) => v + "%") },
            ],
            hooks: {
                // The stretches the discharge hold was armed for. drawClear, not draw: this one is a
                // full-height fill, and it belongs *behind* the series rather than tinting them --
                // the battery line inside a band is the evidence for whether the hold actually held,
                // which is the only way that question has ever been answerable.
                drawClear: [
                    (u) => {
                        const ctx = u.ctx;
                        ctx.save();
                        ctx.fillStyle = battery + "1a";
                        for (let i = 0; i < session.holdStarts.length; i++) {
                            const x0 = u.valToPos(session.holdStarts[i], "x", true);
                            const x1 = u.valToPos(session.holdEnds[i], "x", true);
                            ctx.fillRect(x0, u.bbox.top, Math.max(x1 - x0, 2), u.bbox.height);
                        }
                        ctx.restore();
                    },
                ],
                draw: [
                    (u) => {
                        const ctx = u.ctx;
                        ctx.save();

                        // A rule where something changed. The list under the chart says what; up here
                        // they only have to line up with the step in the line beside them.
                        ctx.strokeStyle = muted;
                        ctx.globalAlpha = 0.7;
                        ctx.setLineDash([3, 3]);
                        for (let i = 0; i < session.markTimes.length; i++) {
                            const x = Math.round(u.valToPos(session.markTimes[i], "x", true)) + 0.5;
                            ctx.beginPath();
                            ctx.moveTo(x, u.bbox.top);
                            ctx.lineTo(x, u.bbox.top + u.bbox.height);
                            ctx.stroke();
                        }
                        ctx.setLineDash([]);

                        // Zero matters here as it does on the day chart: grid and battery are signed,
                        // and which side of this line they are on is the whole reading.
                        const y0 = u.valToPos(0, "w", true);
                        if (y0 >= u.bbox.top && y0 <= u.bbox.top + u.bbox.height) {
                            ctx.strokeStyle = ink;
                            ctx.globalAlpha = 0.35;
                            ctx.beginPath();
                            ctx.moveTo(u.bbox.left, y0);
                            ctx.lineTo(u.bbox.left + u.bbox.width, y0);
                            ctx.stroke();
                        }

                        ctx.restore();
                    },
                ],
            },
        };

        // The site's zone, not the browser's, and probed before it is trusted -- see the day chart
        // above for why an id Intl has never heard of must not cost the page its chart.
        try {
            uPlot.tzDate(new Date(), session.timeZoneId);
            opts.tzDate = (ts) => uPlot.tzDate(new Date(ts * 1000), session.timeZoneId);
        } catch {
            // Left to uPlot's default, which is the browser's local time.
        }

        const chart = new uPlot(opts, data, el);

        const resize = () => chart.setSize({ width: el.clientWidth || 600, height: 320 });
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

    return { renderTimelineChart, renderEnergyDayChart, renderSessionChart, dispose };
})();
