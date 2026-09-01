// The one browser capability the UI cannot reach from C# (issue #143): the clipboard. Kept apart
// from charts.js because it has nothing to do with charts, and answers with a boolean rather than
// throwing -- navigator.clipboard is undefined on a plain-HTTP origin in some browsers, and this is
// a LAN appliance served over HTTP, so a copy that cannot happen has to be an ordinary "no" that
// leaves the Reveal button doing its job.
window.gleanvolt = window.gleanvolt || {};

window.gleanvolt.copyToClipboard = async function (text) {
    if (!navigator.clipboard) {
        return false;
    }

    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
};
