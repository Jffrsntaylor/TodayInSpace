// "Your sky this week": when the ISS, Tiangong and Hubble can be seen from the visitor's location,
// which planets are up tonight and what the Moon is doing, and whether you'll see any of it:
// aurora odds and (in the US) cloud cover from the National Weather Service.
//  - Location is a US zip (looked up by /api/location/zip) or the browser's geolocation
//  - lat/lon are rounded to 2 decimals (about 1 km) before they leave the browser
//  - The location is remembered in this browser's localStorage only
//  - Times are shown in the place's own time zone when the API knows it, so a zip in another state
//    shows that place's local times
(function () {
    'use strict';

    var STORAGE_KEY = 'tis.location';
    var COMPASS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];

    var $ = function (id) { return document.getElementById(id); };

    // The /api/sky/visible request for the location being shown, so the globe and map can reuse it.
    var current = null, changeListeners = [];

    // Shared with the globe and map, which draw your location and your next ISS pass from the same
    // saved location and the same response, without a second copy of the storage format or a second request.
    window.TISYourSky = {
        location: remembered,
        // Called with the new location whenever the visitor picks one.
        on: function (fn) { changeListeners.push(fn); },
        // The next visible pass of a satellite that hasn't ended yet: { start, end, when: "Tue 8:42 PM" }, or null.
        nextPass: function (id) {
            var loc = remembered();
            if (!loc) return Promise.resolve(null);
            var request = current && current.loc.lat === loc.lat && current.loc.lon === loc.lon
                ? current.request : fetchVisible(loc);
            return request.then(function (data) { return nextPass(data, id); });
        }
    };

    function nextPass(data, id) {
        var now = Date.now();
        var p = (data.passes || []).filter(function (p) {
            return p.satellite === id && Date.parse(p.end.time) > now;
        })[0];
        if (!p) return null;
        // In the place's own time zone, matching the list in "Your sky".
        var zone = data.place && data.place.timeZone, fmt;
        var opts = { weekday: 'short', hour: 'numeric', minute: '2-digit' };
        try { fmt = new Intl.DateTimeFormat(undefined, Object.assign({ timeZone: zone || undefined }, opts)); }
        catch (e) { fmt = new Intl.DateTimeFormat(undefined, opts); }
        var start = new Date(p.start.time);
        return { start: start, end: new Date(p.end.time), when: fmt.format(start) };
    }

    function fetchVisible(loc) {
        return fetch('/api/sky/visible?lat=' + loc.lat + '&lon=' + loc.lon).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    var section = $('your-sky');
    if (!section) return;

    var form = $('sky-where'), zipInput = $('sky-zip'), geoButton = $('sky-geo');
    var showing = $('sky-showing'), place = $('sky-place'), errorBox = $('sky-error');
    var intro = $('sky-intro'), list = $('sky-passes'), help = $('sky-help'), tonightBox = $('sky-tonight');
    var conditionsBox = $('sky-conditions');
    var kpPanel = $('explain-kp-sky');

    // Rebuilt by useTimeZone for each response.
    var timeFmt, dayFmt, keyFmt, zoneName;

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

    // timeZone is an IANA name ("America/Los_Angeles"), or null for the browser's own zone.
    // An unknown name throws in Intl, so fall back to the browser's zone.
    function useTimeZone(timeZone) {
        try {
            makeFormats(timeZone || undefined);
        } catch (e) {
            timeZone = null;
            makeFormats(undefined);
        }
        zoneName = timeZone ? genericZoneName(timeZone) : null;
    }
    function makeFormats(timeZone) {
        timeFmt = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit', timeZone: timeZone });
        dayFmt = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric', timeZone: timeZone });
        keyFmt = new Intl.DateTimeFormat('en-US', { year: 'numeric', month: '2-digit', day: '2-digit', timeZone: timeZone });
    }
    useTimeZone(null);

    // "Pacific Time". Older browsers don't know 'longGeneric'; then the label just isn't shown.
    function genericZoneName(timeZone) {
        try {
            var part = new Intl.DateTimeFormat('en-US', { timeZone: timeZone, timeZoneName: 'longGeneric' })
                .formatToParts(new Date()).filter(function (p) { return p.type === 'timeZoneName'; })[0];
            return part ? part.value : null;
        } catch (e) {
            return null;
        }
    }

    // The calendar date in the current zone as "YYYY-MM-DD", so days can be compared as strings.
    function dayKey(date) {
        var parts = {};
        keyFmt.formatToParts(date).forEach(function (p) { parts[p.type] = p.value; });
        return parts.year + '-' + parts.month + '-' + parts.day;
    }
    function nextDayKey(key) {
        var d = new Date(key + 'T12:00:00Z');
        d.setUTCDate(d.getUTCDate() + 1);
        return d.toISOString().slice(0, 10);
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

    // name is the zip's postal city and state ("Olympia, WA"), when the zip lookup has one.
    function useLocation(lat, lon, label, name) {
        var loc = { lat: round2(lat), lon: round2(lon), label: label };
        if (name) loc.name = name;
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
        tonightBox.hidden = true;
        conditionsBox.hidden = true;
        // The Kp explainer belongs to the aurora line that's about to be rebuilt.
        if (kpPanel) kpPanel.hidden = true;

        current = { loc: loc, request: fetchVisible(loc) };
        changeListeners.forEach(function (fn) { try { fn(loc); } catch (e) { console.error(e); } });
        current.request
            .then(function (data) { render(data, loc); })
            .catch(function () {
                list.textContent = '';
                help.hidden = true;
                tonightBox.hidden = true;
                conditionsBox.hidden = true;
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
    function dayTitle(key, date, today) {
        return key === today ? 'Today' : key === nextDayKey(today) ? 'Tomorrow' : dayFmt.format(date);
    }

    // A small "☁ 25%" for a pass or planet row; nothing without a cloud forecast.
    function cloudTag(percent) {
        if (percent == null) return null;
        var tag = el('span', 'sky-cloud', '☁ ' + percent + '%');
        tag.title = percent + '% cloud cover forecast';
        return tag;
    }

    // "Olympia, WA (98501)", or just the zip / "your location" outside the US.
    // A zip's own postal name wins over the weather service's, which is the town nearest its forecast
    // grid point (Tumwater for 98501).
    function placeLabel(data, loc) {
        var name = typeof loc.name === 'string' ? loc.name
            : data.place ? data.place.city + ', ' + data.place.state : null;
        if (!name) return loc.label;
        return /^\d{5}$/.test(loc.label) ? name + ' (' + loc.label + ')' : name;
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    }

    function render(data, loc) {
        useTimeZone(data.place && data.place.timeZone);
        place.textContent = placeLabel(data, loc);
        section.classList.toggle('with-clouds', !!data.clouds);
        list.textContent = '';
        var now = Date.now();
        var passes = (data.passes || []).filter(function (p) { return Date.parse(p.end.time) > now; });
        var today = dayKey(new Date(now));
        renderConditions(data, today);

        var groups = [];
        passes.forEach(function (p) {
            var start = new Date(p.start.time);
            var key = dayKey(start);
            var group = groups[groups.length - 1];
            if (!group || group.key !== key) {
                group = { key: key, date: start, passes: [] };
                groups.push(group);
            }
            group.passes.push({ pass: p, start: start });
        });

        groups.forEach(function (g) {
            var block = el('div', 'pass-day');
            block.appendChild(el('h3', 'pass-day-title', dayTitle(g.key, g.date, today)));
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
                var cloud = cloudTag(p.cloudPercent);
                if (cloud) li.appendChild(cloud);
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

        help.textContent = (zoneName ? 'Times in ' + zoneName + '.' : 'Times in your time zone.') +
            ' Predictions can shift by a minute or two as orbits change.';
        help.hidden = passes.length === 0;
        renderTonight(data);
    }

    // The aurora and cloud lines at the top: "will I actually see it?"
    function renderConditions(data, today) {
        conditionsBox.textContent = '';
        if (data.aurora) conditionsBox.appendChild(auroraRow(data.aurora, today));

        var clouds = data.clouds;
        if (clouds && clouds.tonightMin != null) {
            var range = clouds.tonightMin === clouds.tonightMax
                ? clouds.tonightMin + '%' : clouds.tonightMin + '–' + clouds.tonightMax + '%';
            var row = el('div', 'cond');
            row.appendChild(el('span', 'cond-label', 'Clouds'));
            row.appendChild(el('div', 'cond-text', 'Tonight ' + range + ' cloudy'));
            conditionsBox.appendChild(row);
        }
        conditionsBox.hidden = !conditionsBox.childNodes.length;
    }

    // "Kp" opens the same explainer as the Space Weather card (the panel is in Index.cshtml; explainers.js toggles it).
    function kpTerm() {
        var button = el('button', 'term term-inline', 'Kp');
        button.type = 'button';
        button.setAttribute('aria-expanded', 'false');
        button.setAttribute('aria-controls', 'explain-kp-sky');
        button.appendChild(el('span', 'sr-only', ' (what’s this?)'));
        return button;
    }

    // "Tonight: unlikely (Kp 3 forecast; you'd need about Kp 5 to see it low in the north)",
    // then the other nights on one line, then OVATION's chance right now.
    function auroraRow(aurora, today) {
        var row = el('div', 'cond');
        row.appendChild(el('span', 'cond-label', 'Aurora'));
        var body = el('div', 'cond-text');
        var low = 'low in the ' + (aurora.geomagneticLatitude < 0 ? 'south' : 'north');
        var words = { overhead: 'possible overhead', horizon: 'possible ' + low, unlikely: 'unlikely' };

        var nights = (aurora.nights || []).filter(function (n) { return n.date >= today; });
        var more = [];
        nights.forEach(function (n, i) {
            var title = n.date === today ? 'Tonight' : weekday(n.date);
            if (i === 0) {
                var first = el('div', null, title + ': ' + words[n.chance] + ' (');
                first.appendChild(kpTerm());
                first.appendChild(document.createTextNode(' ' + n.kp + ' forecast' + needed(aurora, n, low) + ')'));
                body.appendChild(first);
            } else {
                more.push(title + ': ' + words[n.chance] + (n.chance === 'unlikely' ? '' : ' (Kp ' + n.kp + ')'));
            }
        });
        if (more.length) body.appendChild(el('div', null, more.join(' · ')));

        if (aurora.nowPercent != null) {
            body.appendChild(el('div', 'cond-now', 'Right now: ' +
                (aurora.nowPercent < 5 ? 'under 5%' : aurora.nowPercent + '%') + ' chance overhead'));
        }
        row.appendChild(body);
        return row;
    }

    // What it would take, said once for the first night.
    function needed(aurora, night, low) {
        if (night.chance === 'overhead') return '';
        if (night.chance === 'horizon') {
            return aurora.kpForOverhead != null ? '; about Kp ' + aurora.kpForOverhead + ' would put it overhead' : '';
        }
        return aurora.kpForHorizon != null
            ? '; you’d need about Kp ' + aurora.kpForHorizon + ' to see it ' + low
            : '; it would take a rare, extreme storm to see it from this far ' + (aurora.geomagneticLatitude < 0 ? 'north' : 'south');
    }

    // "2026-10-14" -> "Wed". It's a calendar day, so format it at noon UTC to keep the right day name.
    function weekday(date) {
        return new Intl.DateTimeFormat(undefined, { weekday: 'short', timeZone: 'UTC' })
            .format(new Date(date + 'T12:00:00Z'));
    }

    var PHASES = {
        newMoon: 'New moon', waxingCrescent: 'Waxing crescent', firstQuarter: 'First quarter',
        waxingGibbous: 'Waxing gibbous', fullMoon: 'Full moon', waningGibbous: 'Waning gibbous',
        lastQuarter: 'Last quarter', waningCrescent: 'Waning crescent'
    };

    // Magnitude runs backwards: the lower the number, the brighter.
    function brightness(magnitude) {
        return magnitude <= -2 ? 'very bright' : magnitude <= 0 ? 'bright' : magnitude <= 2 ? 'easy to see' : 'faint';
    }

    function where(point) {
        var words = point.elevation < 20 ? 'low' : point.elevation <= 50 ? 'halfway up' : 'high';
        return words + ' in the ' + compass(point.azimuth);
    }

    function at(iso) { return timeFmt.format(new Date(iso)); }

    // A best time right at the start or end of the dark hours reads better as words than as a clock time.
    // When it was already dark at "generated", the dark hours start then, not at dusk.
    function bestTime(best, darkness, generated) {
        var t = Date.parse(best.time), start = Date.parse(darkness.start);
        if (t - start < 5 * 60000) return start - Date.parse(generated) < 60000 ? 'best now' : 'best at dusk';
        if (Date.parse(darkness.end) - t < 5 * 60000) return 'best at dawn';
        return 'best ' + at(best.time);
    }

    function riseSet(body) {
        var parts = [];
        if (body.rise) parts.push('rises ' + at(body.rise));
        if (body.set) parts.push('sets ' + at(body.set));
        return parts.join(' · ');
    }

    function joinNames(names) {
        return names.length < 3 ? names.join(' and ') : names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];
    }

    function renderTonight(data) {
        tonightBox.textContent = '';
        tonightBox.appendChild(el('h3', 'pass-day-title', 'Planets tonight'));

        if (!data.darkness) {
            tonightBox.appendChild(el('p', 'tonight-note', 'It doesn’t get dark enough tonight to see the planets.'));
        } else {
            var planets = data.planets || [];
            var ul = el('ul', 'pass-list');
            planets.filter(function (p) { return p.visible; }).forEach(function (p) {
                var li = el('li', 'planet planet-' + p.id);
                li.appendChild(el('span', 'planet-name', p.name));
                li.appendChild(el('span', 'planet-bright', brightness(p.magnitude)));
                li.appendChild(el('span', 'planet-where', where(p.best)));
                li.appendChild(el('span', 'planet-best', bestTime(p.best, data.darkness, data.generated)));
                li.appendChild(el('span', 'planet-riseset', riseSet(p)));
                var cloud = cloudTag(p.cloudPercent);
                if (cloud) li.appendChild(cloud);
                ul.appendChild(li);
            });
            if (ul.childNodes.length) tonightBox.appendChild(ul);

            // The ones you can't see, one line per reason.
            var reasons = { nearSun: 'too close to the Sun to see tonight', belowHorizon: 'too low to see while it’s dark' };
            Object.keys(reasons).forEach(function (reason) {
                var names = planets.filter(function (p) { return p.reason === reason; })
                    .map(function (p) { return p.name; });
                if (names.length) tonightBox.appendChild(el('p', 'tonight-note', joinNames(names) + ': ' + reasons[reason]));
            });
        }

        if (data.moon) tonightBox.appendChild(moonLine(data.moon, !!data.darkness));
        tonightBox.hidden = false;
    }

    // Without any dark hours, only the phase means anything.
    function moonLine(moon, hasDarkness) {
        var p = el('p', 'tonight-moon');
        p.appendChild(el('span', 'planet-name', 'Moon'));
        var text = [PHASES[moon.phase] || moon.phase, Math.round(moon.illumination * 100) + '% lit'];
        var times = riseSet(moon);
        if (times) text.push(times);
        else if (hasDarkness) text.push(moon.upDuringDarkness ? 'up all night' : 'not up while it’s dark');

        var line = text.join(' · ');
        // A bright Moon washes out faint stars and the Milky Way while it's up.
        if (moon.illumination >= 0.5 && moon.upDuringDarkness) {
            line += moon.set && !moon.rise ? ' — its light hides faint stars until then'
                : moon.rise && !moon.set ? ' — its light hides faint stars after that'
                : ' — its light hides faint stars';
        }
        p.appendChild(el('span', null, line));
        return p;
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
            .then(function (z) { useLocation(z.lat, z.lon, z.zip, z.city && z.state ? z.city + ', ' + z.state : null); })
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
