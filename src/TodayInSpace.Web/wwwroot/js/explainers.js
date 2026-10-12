// The ⓘ explainers on the Space Weather card. Each term is a button whose aria-controls names its panel.
// Tap to open, tap again or press Esc to close. One open at a time keeps the cards from jumping around.
// Listens on the document so buttons visible-passes.js adds later (the "Kp" in the aurora line) work too.
(function () {
    var open = null;   // the button whose panel is showing

    function panelFor(button) { return document.getElementById(button.getAttribute('aria-controls')); }

    function close() {
        if (!open) return;
        panelFor(open).hidden = true;
        open.setAttribute('aria-expanded', 'false');
        open = null;
    }

    function show(button) {
        var panel = panelFor(button);
        if (!panel) return;
        close();
        panel.hidden = false;
        button.setAttribute('aria-expanded', 'true');
        open = button;
    }

    document.addEventListener('click', function (e) {
        var button = e.target.closest('button.term');
        if (!button) return;
        if (button === open) close(); else show(button);
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape' || !open) return;
        // If focus was inside the panel (say, on "Learn more"), put it back on the term so it isn't lost.
        var button = open;
        var focusInside = panelFor(button).contains(document.activeElement);
        close();
        if (focusInside) button.focus();
    });
})();
