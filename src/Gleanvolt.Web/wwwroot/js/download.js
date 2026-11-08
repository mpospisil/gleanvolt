// Hands the browser a file the server made (issue #319: the register snapshot). Built from text rather
// than fetched from a URL, so the file never has an address of its own that an open UI would serve to
// anyone who guessed it.
window.gleanvolt = window.gleanvolt || {};

window.gleanvolt.downloadText = function (fileName, text, contentType) {
    const url = URL.createObjectURL(new Blob([text], { type: contentType || "application/json" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
};
