// Left/Right arrow keys follow the archive's day links. Skipped while typing in the
// date picker and on a focused video, where the arrows seek.
document.addEventListener('keydown', function (e) {
    if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
    var t = e.target;
    if (t instanceof Element && (t.isContentEditable || t.closest('input, textarea, select, video'))) return;

    var rel = e.key === 'ArrowLeft' ? 'prev' : e.key === 'ArrowRight' ? 'next' : null;
    var link = rel && document.querySelector('.day-nav a[rel="' + rel + '"]');
    if (link) location.href = link.href;
});
