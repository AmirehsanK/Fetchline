// The one thing the editor needs that markup cannot say: put the caret on a stretch of the text.
window.fetchlineEditor = {
    select: function (textarea, start, end) {
        textarea.focus();
        textarea.setSelectionRange(start, Math.max(start, end));
    },
};
