// Show or hide what is typed in a password box (RevealInput.razor). Delegated from the document so it
// works on static pages and on interactive ones alike, and on boxes rendered after the page loaded.
window.gleanvolt = window.gleanvolt || {};

document.addEventListener('click', function (e) {
    const button = e.target.closest('.reveal-toggle');
    if (!button) {
        return;
    }

    const input = button.closest('.reveal')?.querySelector('input');
    if (!input) {
        return;
    }

    e.preventDefault();
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    button.setAttribute('aria-pressed', show ? 'true' : 'false');
    input.focus();
});

// Masked again before a form is sent, so the browser treats the value as a password (offering to save
// it) and nothing is left on screen after a static page's post.
document.addEventListener('submit', function (e) {
    e.target.querySelectorAll('.reveal input').forEach(function (input) {
        input.type = 'password';
    });
    e.target.querySelectorAll('.reveal-toggle').forEach(function (button) {
        button.setAttribute('aria-pressed', 'false');
    });
}, true);
