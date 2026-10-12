// Section bar and back-to-top button for the homepage. The bar's links work on their own; this adds
// smooth scrolling, the active-section highlight, and hides chips for sections that aren't showing.
(function () {
    var nav = document.querySelector('.section-nav');
    if (!nav) return;
    var row = nav.querySelector('ul');
    var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

    function behavior() { return reduceMotion.matches ? 'auto' : 'smooth'; }

    // One entry per chip, in page order.
    var items = [];
    nav.querySelectorAll('a[href^="#"]').forEach(function (link) {
        var section = document.getElementById(link.getAttribute('href').slice(1));
        items.push({ link: link, li: link.parentElement, section: section });
    });

    // ---- Hidden sections drop out ----
    // People, Launches and Satellites start hidden and sky-core.js un-hides them when data arrives,
    // so the chips follow the section's hidden attribute instead of sky-core.js knowing about the bar.
    function syncChips() {
        items.forEach(function (it) { it.li.hidden = !it.section || it.section.hidden; });
        updateFades();
    }

    // ---- Sideways scrolling on phones ----
    function updateFades() {
        var max = row.scrollWidth - row.clientWidth;
        row.classList.toggle('more-left', row.scrollLeft > 2);
        row.classList.toggle('more-right', row.scrollLeft < max - 2);
    }
    row.addEventListener('scroll', updateFades, { passive: true });
    window.addEventListener('resize', updateFades);

    // Scrolls only the chip row. Element.scrollIntoView would also scroll the page.
    function revealChip(link) {
        var li = link.parentElement;
        var left = li.offsetLeft - row.offsetLeft;
        if (left < row.scrollLeft + 24 || left + li.offsetWidth > row.scrollLeft + row.clientWidth - 24) {
            row.scrollTo({ left: left - (row.clientWidth - li.offsetWidth) / 2, behavior: behavior() });
        }
    }

    // ---- Active section ----
    var active = null;
    function setActive(it) {
        if (it === active) return;
        if (active) active.link.removeAttribute('aria-current');
        active = it;
        if (it) {
            it.link.setAttribute('aria-current', 'true');
            revealChip(it.link);
        }
    }

    // A section counts as "in view" while it crosses a band just under the sticky bar. The first
    // one in page order wins, so a short section isn't skipped when the next one starts on screen.
    var inBand = new Set();
    if ('IntersectionObserver' in window) {
        var io = new IntersectionObserver(function (entries) {
            entries.forEach(function (e) {
                if (e.isIntersecting) inBand.add(e.target); else inBand.delete(e.target);
            });
            var current = items.filter(function (it) { return it.section && inBand.has(it.section); })[0];
            // Keep the last chip lit between sections; clear it back above the first one.
            if (current) setActive(current);
            else if (window.scrollY < firstTop()) setActive(null);
        }, { rootMargin: '-70px 0px -55% 0px' });
        items.forEach(function (it) { if (it.section) io.observe(it.section); });
    }

    function firstTop() {
        var first = items.filter(function (it) { return it.section && !it.section.hidden; })[0];
        return first ? first.section.getBoundingClientRect().top + window.scrollY - 70 : 0;
    }

    // ---- Clicks ----
    nav.addEventListener('click', function (e) {
        var link = e.target.closest('a[href^="#"]');
        if (!link || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        var it = items.filter(function (i) { return i.link === link; })[0];
        if (!it || !it.section || it.section.hidden) return;
        e.preventDefault();
        pendingTarget = null;
        it.section.scrollIntoView({ behavior: behavior(), block: 'start' });
        // replaceState so ten chip clicks don't mean ten presses of Back.
        history.replaceState(null, '', link.getAttribute('href'));
        setActive(it);
    });

    // ---- Deep links like /#launches ----
    // The browser can't scroll to a section that's still hidden, and the APOD image and the sections
    // that fill in later push things down after it does. Keep re-landing on the target as the page
    // settles, until the visitor starts scrolling on their own.
    var pendingTarget = location.hash.length > 1 ? document.getElementById(location.hash.slice(1)) : null;
    if (pendingTarget && !items.some(function (it) { return it.section === pendingTarget; })) pendingTarget = null;

    function reland() {
        if (pendingTarget && !pendingTarget.hidden) pendingTarget.scrollIntoView({ block: 'start' });
    }
    function giveUpReland() { pendingTarget = null; }
    ['wheel', 'touchstart', 'keydown', 'mousedown'].forEach(function (type) {
        window.addEventListener(type, giveUpReland, { once: true, passive: true });
    });
    window.addEventListener('load', function () {
        reland();
        // Feeds that never answer shouldn't pin the page forever.
        setTimeout(giveUpReland, 8000);
    });

    if ('MutationObserver' in window) {
        var mo = new MutationObserver(function () {
            syncChips();
            reland();
        });
        items.forEach(function (it) {
            if (it.section) mo.observe(it.section, { attributes: true, attributeFilter: ['hidden'] });
        });
    }

    // ---- Back to top ----
    var toTop = document.getElementById('to-top');
    var skyPanel = document.querySelector('.sky-panel');
    var masthead = document.getElementById('top');
    if (toTop) {
        toTop.hidden = false;
        toTop.classList.add('tucked');
        toTop.addEventListener('click', function () {
            window.scrollTo({ top: 0, behavior: behavior() });
            history.replaceState(null, '', location.pathname + location.search);
            // Without this, the next Tab would continue from wherever the button was.
            if (masthead) masthead.focus({ preventScroll: true });
        });
    }

    // Shows after about a screen of scrolling, and steps aside while it would cover the Live Sky
    // panel (its satellite buttons, and the globe that needs dragging). On wide screens the button
    // sits beside the column, so it never overlaps.
    var ticking = false;
    function updateToTop() {
        ticking = false;
        if (!toTop) return;
        var show = window.scrollY > window.innerHeight * 0.9;
        if (show && skyPanel) {
            var b = toTop.getBoundingClientRect(), p = skyPanel.getBoundingClientRect();
            var overlaps = b.left < p.right && b.right > p.left && b.top < p.bottom && b.bottom > p.top;
            if (overlaps) show = false;
        }
        toTop.classList.toggle('tucked', !show);
    }
    function onScroll() {
        if (!ticking) { ticking = true; requestAnimationFrame(updateToTop); }
    }
    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onScroll);

    syncChips();
    updateToTop();
})();
