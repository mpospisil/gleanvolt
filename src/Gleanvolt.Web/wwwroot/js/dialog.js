// Opens a <dialog> as a modal (#255, the Targeted preview). Blazor renders the element; only the
// browser can make it modal. Escape would close it behind Blazor's back and leave the component
// thinking the preview is still up, so Escape presses the dialog's own cancel button instead, and
// the component closes it the same way it does for a click.
window.gleanvoltDialog = {
    show: function (id) {
        const dialog = document.getElementById(id);
        if (!dialog || dialog.open) {
            return;
        }

        if (!dialog.dataset.cancelWired) {
            dialog.dataset.cancelWired = "1";
            dialog.addEventListener("cancel", function (e) {
                e.preventDefault();
                const cancel = dialog.querySelector("[data-dialog-cancel]");
                if (cancel) {
                    cancel.click();
                }
            });
        }

        dialog.showModal();
    },
};
