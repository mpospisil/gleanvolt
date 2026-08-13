uPlot 1.6.31, MIT licensed (see `LICENSE` in this folder).

Vendored rather than fetched from a CDN (issue #44's decision): the dashboard has to keep rendering
during an internet outage, which is exactly when a locally controlled system is most worth looking
at. `uPlot.iife.min.js` and `uPlot.min.css` are the unmodified `dist/` build artifacts from
https://www.npmjs.com/package/uplot — to upgrade, replace both files with a newer version's and bump
the number above.
