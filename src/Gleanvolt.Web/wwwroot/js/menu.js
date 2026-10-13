// Closes the phone menu (MainLayout.razor). Opening it needs no script -- it is a checkbox -- but
// enhanced navigation patches the page in place and keeps the header, so a menu left checked would
// still be covering the page that was just asked for. Also closed by Escape and by a tap outside it,
// as anyone who has used a phone menu expects.
document.addEventListener('click', function (e) {
    const toggle = document.getElementById('menu-toggle');
    if (!toggle || !toggle.checked) {
        return;
    }

    if (e.target.closest('.menu a') || !e.target.closest('.topbar')) {
        toggle.checked = false;
    }
});

document.addEventListener('keydown', function (e) {
    const toggle = document.getElementById('menu-toggle');
    if (e.key === 'Escape' && toggle && toggle.checked) {
        toggle.checked = false;
        toggle.focus();
    }
});
