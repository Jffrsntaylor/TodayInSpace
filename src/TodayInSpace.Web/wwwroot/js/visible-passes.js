// "Your sky this week": when the ISS, Tiangong and Hubble can be seen from the visitor's location.
//  - Location is a US zip (looked up by /api/location/zip) or the browser's geolocation
//  - lat/lon are rounded to 2 decimals (about 1 km) before they leave the browser
//  - The location is remembered in this browser's localStorage only
(function () {
    'use strict';

    var STORAGE_KEY = 'tis.location';
    var COMPASS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];

    var $ = function (id) { return document.getElementById(id); };
    var section = $('your-sky');
    if (!section) return;

    var form = $('sky-where'), zipInput = $('sky-zip'), geoButton = $('sky-geo');
    var showing = $('sky-showing'), place = $('sky-place'), errorBox = $('sky-error');
    var intro = $('sky-intro'), list = $('sky-passes'), help = $('sky-help');

    var timeFmt = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });
    var dayFmt = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric' });

    var round2 = function (n) { return Math.round(n * 100) / 100; };

    // Storage can be missing or throw (private windows, blocked site data); the page works without it.
    function remembered() {
        try {
            var loc = JSON.parse(localStorage.getItem(STORAGE_KEY));
            if (loc && isFinite(loc.lat) && isFinite(loc.lon) && typeof loc.label === 'string') return loc;
        } catch (e) { /* ignore */ }
        return null;
    }
    function remember(loc) {
        try { localStorage.setItem(STORAGE_KEY, JSON.stringify(loc)); } catch (e) { /* ignore */ }
    }

    function showError(message) {
        errorBox.textContent = message;
        errorBox.hidden = !message;
    }

    function askForLocation() {
        form.hidden = false;
        showing.hidden = true;
        zipInput.focus();
    }

    function useLocation(lat, lon, label) {
        var loc = { lat: round2(lat), lon: round2(lon), label: label };
        remember(loc);
        load(loc);
    }

    function load(loc) {
        showError('');
        form.hidden = true;
        showing.hidden = false;
        place.textContent = loc.label;
        intro.hidden = true;
        list.textContent = 'Working out passes…';
        list.setAttribute('aria-busy', 'true');

        fetch('/api/sky/visible?lat=' + loc.lat + '&lon=' + loc.lon)
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(render)
            .catch(function () {
                list.textContent = '';
                help.hidden = true;
                showError('We couldn’t load the passes right now. Please try again in a few minutes.');
            })
            .then(function () { list.removeAttribute('aria-busy'); });
    }

    function compass(azimuth) { return COMPASS[Math.round(azimuth / 22.5) % 16]; }

    function height(elevation) {
        var words = elevation >= 60 ? 'high' : elevation >= 30 ? 'medium' : 'low';
        return words + ' — ' + Math.round(elevation) + '°';
    }

    function minutes(seconds) { return Math.max(1, Math.round(seconds / 60)) + ' min'; }

    // Passes are grouped by local calendar day rather than by night: most ISS passes are before dawn,
    // and "Mon, Oct 12" over a 5 am Tuesday pass was confusing.
    function dayOf(date) { return new Date(date.getFullYear(), date.getMonth(), date.getDate()); }

    function dayTitle(day, today) {
        var diff = Math.round((day - today) / 86400000);
        return diff === 0 ? 'Today' : diff === 1 ? 'Tomorrow' : dayFmt.format(day);
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    }

    function render(data) {
        list.textContent = '';
        var now = Date.now();
        var passes = (data.passes || []).filter(function (p) { return Date.parse(p.end.time) > now; });
        var today = dayOf(new Date(now));

        var groups = [];
        passes.forEach(function (p) {
            var start = new Date(p.start.time);
            var day = dayOf(start);
            var group = groups[groups.length - 1];
            if (!group || group.day.getTime() !== day.getTime()) {
                group = { day: day, passes: [] };
                groups.push(group);
            }
            group.passes.push({ pass: p, start: start });
        });

        groups.forEach(function (g) {
            var block = el('div', 'pass-day');
            block.appendChild(el('h3', 'pass-day-title', dayTitle(g.day, today)));
            var ul = el('ul', 'pass-list');
            g.passes.forEach(function (item) {
                var p = item.pass;
                var li = el('li', 'pass pass-' + p.satellite);
                li.appendChild(el('span', 'pass-sat', p.name));
                var time = el('time', 'pass-time', timeFmt.format(item.start));
                time.dateTime = p.start.time;
                li.appendChild(time);
                li.appendChild(el('span', 'pass-path', 'from ' + compass(p.start.azimuth) + ' to ' + compass(p.end.azimuth)));
                li.appendChild(el('span', 'pass-height', height(p.max.elevation)));
                li.appendChild(el('span', 'pass-length', minutes(p.durationSeconds)));
                ul.appendChild(li);
            });
            block.appendChild(ul);
            list.appendChild(block);
        });

        // One line for each satellite with nothing to show, saying why.
        var notes = el('ul', 'pass-notes');
        (data.satellites || []).forEach(function (s) {
            var upcoming = passes.filter(function (p) { return p.satellite === s.id; }).length;
            if (upcoming > 0) return;
            var text = !s.available
                ? s.name + ' predictions aren’t available right now.'
                : !s.anyPassAbove10
                    ? s.name + '’s orbit keeps it too far ' + (data.lat >= 0 ? 'south' : 'north') + ' to see from here.'
                    : 'No visible ' + s.name + ' passes in the next ' + data.days + ' days.';
            notes.appendChild(el('li', null, text));
        });
        if (notes.childNodes.length) list.appendChild(notes);

        help.hidden = passes.length === 0;
    }

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        var zip = zipInput.value.trim();
        if (!/^\d{5}$/.test(zip)) {
            showError('Please enter a 5-digit US zip code.');
            return;
        }
        showError('');
        fetch('/api/location/zip/' + zip)
            .then(function (r) {
                if (r.status === 404 || r.status === 400) throw new Error('unknown');
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (z) { useLocation(z.lat, z.lon, z.zip); })
            .catch(function (err) {
                showError(err.message === 'unknown'
                    ? 'We couldn’t find that zip code.'
                    : 'We couldn’t look up that zip code right now. Please try again.');
            });
    });

    if (navigator.geolocation) {
        geoButton.hidden = false;
        geoButton.addEventListener('click', function () {
            showError('');
            geoButton.disabled = true;
            navigator.geolocation.getCurrentPosition(function (pos) {
                geoButton.disabled = false;
                useLocation(pos.coords.latitude, pos.coords.longitude, 'your location');
            }, function (err) {
                geoButton.disabled = false;
                showError(err.code === err.PERMISSION_DENIED
                    ? 'Location permission was denied. You can enter a zip code instead.'
                    : 'We couldn’t get your location. You can enter a zip code instead.');
            }, { enableHighAccuracy: false, timeout: 15000, maximumAge: 10 * 60 * 1000 });
        });
    }

    $('sky-change').addEventListener('click', askForLocation);

    var saved = remembered();
    if (saved) load(saved);
})();
