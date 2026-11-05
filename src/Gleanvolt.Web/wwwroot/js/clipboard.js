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

// The recovery key's Copy button (#302). A plain function rather than a Blazor event: the key is shown
// on the setup page, which is static server-side rendering with no circuit to call back into. Where
// the clipboard is not available (plain HTTP in some browsers) the key is selected instead, so a
// Ctrl+C still does it.
window.gleanvolt.copyRecoveryKey = async function (button) {
    const key = document.getElementById('recovery-key');
    if (!key) {
        return;
    }

    const status = button.parentElement.querySelector('.recovery-key-status');
    if (await window.gleanvolt.copyToClipboard(key.textContent.trim())) {
        if (status) {
            status.hidden = false;
        }
        return;
    }

    const range = document.createRange();
    range.selectNodeContents(key);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
};
