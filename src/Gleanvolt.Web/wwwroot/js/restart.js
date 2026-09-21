// Brings the page back after a restart asked for from the UI (issue #204). The circuit the page runs
// on dies with the process, and Blazor's own reconnect gives up on a circuit the new process has
// never heard of -- so the browser watches for the controller itself: first for it to go away, then
// for it to answer again, and then reloads onto the new process.
window.gleanvolt = window.gleanvolt || {};

window.gleanvolt.reloadAfterRestart = function () {
    const started = Date.now();
    const interval = 1500;
    // A restart that was never seen to go down still reloads eventually, rather than leaving the
    // "Restarting…" line up for ever.
    const giveUpWaitingForDown = 60000;
    let wentDown = false;

    const poll = async function () {
        let up = false;

        try {
            // The sign-in page answers anyone, with or without a login configured.
            const response = await fetch("/login", { cache: "no-store" });
            up = response.ok;
        } catch {
            up = false;
        }

        if (!up) {
            wentDown = true;
        } else if (wentDown || Date.now() - started > giveUpWaitingForDown) {
            window.location.reload();
            return;
        }

        window.setTimeout(poll, interval);
    };

    window.setTimeout(poll, interval);
};
