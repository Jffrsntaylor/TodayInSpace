// Shared state and math for the Live Sky section (used by both the 3D globe and the flat map).
//  - Loads satellite orbital elements (TLEs) and the aurora forecast from our own API
//  - Propagates satellite positions in the browser with satellite.js (SGP4)
//  - Owns the readout panel, satellite picker, Follow button and Globe/Map toggle
window.TISSky = (function () {
    'use strict';

    var SATS = [
        { id: 'iss', name: 'ISS', full: 'International Space Station', color: '#b7c0ff' },
        { id: 'hubble', name: 'Hubble', full: 'Hubble Space Telescope', color: '#ffc46b' },
        { id: 'tiangong', name: 'Tiangong', full: 'Tiangong Space Station', color: '#5fd3e6' }
    ];

    var rad = function (d) { return d * Math.PI / 180; };
    var deg = function (r) { return r * 180 / Math.PI; };
    var wrapLon = function (lon) { return ((lon + 540) % 360) - 180; };
    // Shift a longitude by whole turns so it's as close as possible to ref, so lines and
    // animated markers stay continuous across the 180th meridian.
    var unwrapNear = function (lon, ref) { return lon + 360 * Math.round((ref - lon) / 360); };

    var satrecs = {};
    var listeners = [];
    var state = { selected: 'iss', following: false, view: 'globe', ready: false };

    function emit(type) {
        listeners.forEach(function (fn) { try { fn(type, state); } catch (e) { console.error(e); } });
    }

    function hasSat(id) { return !!satrecs[id]; }

    // Geodetic position (degrees, km) and speed (km/h) of a satellite at a given time.
    function position(id, date) {
        var rec = satrecs[id];
        if (!rec) return null;
        var pv = satellite.propagate(rec, date);
        if (!pv || !pv.position) return null;
        var geo = satellite.eciToGeodetic(pv.position, satellite.gstime(date));
        var v = pv.velocity;
        return {
            lat: satellite.degreesLat(geo.latitude),
            lon: satellite.degreesLong(geo.longitude),
            alt: geo.height,
            speed: v ? Math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * 3600 : null
        };
    }

    // Orbital period in minutes, from the TLE mean motion (radians/minute).
    function periodMinutes(id) {
        var rec = satrecs[id];
        return rec && rec.no ? (2 * Math.PI) / rec.no : 95;
    }

    // Continuous track from `fromMin` to `toMin` minutes relative to now (negative = past),
    // with longitudes unwrapped starting near `refLon`. Returns [{lat, lon, alt}].
    function track(id, fromMin, toMin, stepSec, refLon) {
        var pts = [];
        var now = Date.now();
        var dir = toMin >= fromMin ? 1 : -1;
        var ref = refLon;
        for (var t = fromMin * 60; dir > 0 ? t <= toMin * 60 : t >= toMin * 60; t += dir * stepSec) {
            var p = position(id, new Date(now + t * 1000));
            if (!p) continue;
            var lon = ref === undefined ? p.lon : unwrapNear(p.lon, ref);
            pts.push({ lat: p.lat, lon: lon, alt: p.alt });
            ref = lon;
        }
        return pts;
    }

    var EARTH_RADIUS_KM = 6371;

    // A satellite's visibility footprint: how far from the point under it (as a central angle, degrees)
    // it's still at least minElevation degrees above the horizon. For the ISS at ~420 km and 10°, ~12.5°.
    function footprintRadius(altKm, minElevation) {
        var e = rad(minElevation);
        return deg(Math.acos(EARTH_RADIUS_KM * Math.cos(e) / (EARTH_RADIUS_KM + altKm)) - e);
    }

    // Great-circle angle (degrees) between two points, by the haversine formula (stable for short distances).
    function angleBetween(lat1, lon1, lat2, lon2) {
        var a = Math.pow(Math.sin(rad(lat2 - lat1) / 2), 2) +
            Math.cos(rad(lat1)) * Math.cos(rad(lat2)) * Math.pow(Math.sin(rad(lon2 - lon1) / 2), 2);
        return deg(2 * Math.asin(Math.min(1, Math.sqrt(a))));
    }

    // A closed circle of `steps` + 1 points, radiusDeg (central angle) around a point on the sphere,
    // with longitudes kept continuous near the center's so it draws across the 180th meridian.
    function circle(lat, lon, radiusDeg, steps) {
        var pts = [], phi = rad(lat), r = rad(radiusDeg);
        for (var i = 0; i <= steps; i++) {
            var b = 2 * Math.PI * i / steps;
            var pLat = Math.asin(Math.sin(phi) * Math.cos(r) + Math.cos(phi) * Math.sin(r) * Math.cos(b));
            var dLon = Math.atan2(Math.sin(b) * Math.sin(r) * Math.cos(phi), Math.cos(r) - Math.sin(phi) * Math.sin(pLat));
            pts.push({ lat: deg(pLat), lon: unwrapNear(lon + deg(dLon), lon) });
        }
        return pts;
    }

    // Low-precision solar position (fraction of a degree): mean anomaly -> ecliptic longitude
    // -> declination / right ascension, then subtract sidereal time for the subsolar longitude.
    function subsolarPoint(date) {
        var d = date.getTime() / 86400000 - 10957.5;           // days since J2000.0
        var g = rad(357.529 + 0.98560028 * d);
        var q = 280.459 + 0.98564736 * d;
        var lambda = rad(q + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g));
        var eps = rad(23.439 - 0.00000036 * d);
        var dec = Math.asin(Math.sin(eps) * Math.sin(lambda));
        var ra = Math.atan2(Math.cos(eps) * Math.sin(lambda), Math.cos(lambda));
        var gmst = 280.46061837 + 360.98564736629 * d;
        return { lat: deg(dec), lon: wrapLon(deg(ra) - gmst) };
    }

    function getJson(url) {
        return fetch(url).then(function (r) {
            if (!r.ok) throw new Error(url + ' ' + r.status);
            return r.json();
        });
    }

    var satsReady = Promise.allSettled(SATS.map(function (s) {
        return getJson('/api/sky/tle/' + s.id).then(function (tle) {
            satrecs[s.id] = satellite.twoline2satrec(tle.line1, tle.line2);
        });
    })).then(function (results) {
        var failed = [];
        results.forEach(function (r, i) { if (r.status === 'rejected') failed.push(SATS[i].name); });
        if (!hasSat(state.selected)) {
            var first = SATS.filter(function (s) { return hasSat(s.id); })[0];
            if (first) state.selected = first.id;
        }
        return failed;
    });

    // The "valid around" time sits in the note under both views, so it's set here rather than
    // in sky-map.js, which only loads once someone opens the map.
    function showAuroraTime(data) {
        var span = document.getElementById('aurora-time');
        if (!span || !data || !data.forecastTime) return;
        var t = new Date(data.forecastTime);
        span.textContent = isNaN(t) ? data.forecastTime : t.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
    }

    function loadAurora() {
        return getJson('/api/sky/aurora').then(function (data) { showAuroraTime(data); return data; });
    }
    var auroraReady = loadAurora();
    auroraReady.catch(function () { setStatus('Couldn’t load the aurora forecast right now.'); });

    // ---- UI: readouts, satellite picker, follow, view toggle ----
    var $ = function (id) { return document.getElementById(id); };
    var fmt = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });

    // The function refreshes every 3 hours, so 12 hours means four runs in a row were missed.
    var PEOPLE_STALE_HOURS = 12;

    // People in Space. The section stays hidden unless a real number arrives. The number is shown
    // before the list is built, so a problem with the list can never hide the number too.
    getJson('/api/sky/people').then(function (data) {
        var section = $('people');
        if (!section || !data || typeof data.count !== 'number') return;
        $('people-count').textContent = fmt.format(data.count);
        section.hidden = false;
        showUpdated('people-source', data.updated, PEOPLE_STALE_HOURS);
        try {
            if (Array.isArray(data.people) && data.people.length) showPeople(data.people);
        } catch (e) {
            console.error('Couldn’t show the list of people in space', e);
        }
    }).catch(function () { /* no data and no cached copy: keep the card hidden */ });

    // "just now", "5 minutes ago", "2 hours ago", "3 days ago".
    function timeAgo(ms) {
        var minutes = Math.floor(ms / 60000);
        if (minutes < 5) return 'just now';
        if (minutes < 60) return minutes + ' minutes ago';
        var hours = Math.floor(minutes / 60);
        if (hours < 24) return hours + (hours === 1 ? ' hour ago' : ' hours ago');
        var days = Math.floor(hours / 24);
        return days + (days === 1 ? ' day ago' : ' days ago');
    }

    // Adds when the data was fetched to a card's source line, and says so if it's getting old.
    // Old data is still shown: being honest about its age beats hiding it.
    // verb is "updated" unless given, e.g. "checked" for data that's re-checked but may not have changed.
    function showUpdated(id, updated, staleHours, verb) {
        var source = $(id);
        var t = new Date(updated);
        if (!source || !updated || isNaN(t)) return;
        var age = Math.max(0, Date.now() - t);
        verb = verb || 'updated';
        source.title = verb.charAt(0).toUpperCase() + verb.slice(1) + ' ' + t.toLocaleString();
        source.appendChild(document.createTextNode(' · ' + verb + ' ' + timeAgo(age)));
        if (age > staleHours * 3600000)
            source.appendChild(el('span', 'stale', ' · may be out of date'));
    }

    // ---- Launches ----

    // The function refreshes hourly, so 6 hours means six runs in a row were missed.
    var LAUNCHES_STALE_HOURS = 6;
    var HOUR = 3600000;
    var PROVIDERS_SHOWN = 5;
    // Only these precisions carry a real time. Coarser ones (DAY, M, Q...) use placeholders like 00:00Z.
    var TIMED = { SEC: true, MIN: true, HR: true };
    var STATUS_TEXT = { 1: 'Go for launch', 2: 'Date not final', 5: 'On hold', 8: 'To be confirmed' };
    var STATUS_GO = 1;

    // Shared with the globe, which puts the upcoming launches' pads on the map.
    var launchesData = getJson('/api/sky/launches');
    var launchesReady = launchesData.then(function (data) {
        return data && Array.isArray(data.upcoming) ? data.upcoming : [];
    });
    // The globe loads later (or never), so mark a failure as handled here; it's not an error worth logging.
    launchesReady.catch(function () { });

    // The card shows whatever half has data: the next launch, the year's count, or both.
    launchesData.then(function (data) {
        var section = $('launches');
        if (!section || !data) return;
        var upcoming = Array.isArray(data.upcoming) ? data.upcoming : [];
        var hasStats = !!data.stats && typeof data.stats.total === 'number';
        if (hasStats) showYear(data.stats);
        if (upcoming.length) startNextLaunch(upcoming);
        if (!hasStats && $('launch-next').hidden) return;
        section.hidden = false;
        showUpdated('launches-source', data.updated, LAUNCHES_STALE_HOURS);
    }).catch(function () { /* no data and no cached copy: keep the card hidden */ });

    // The number first, then the breakdown in a try/catch, so a problem with the breakdown can't hide it.
    function showYear(stats) {
        var year = typeof stats.year === 'number' ? stats.year : '';
        $('launch-year-heading').textContent = 'Launches in ' + year;
        $('launch-total').textContent = fmt.format(stats.total);
        $('launch-year').hidden = false;
        try {
            if (stats.total === 0) {
                $('launch-outcomes').textContent = 'No orbital launches yet in ' + year;
                return;
            }
            var parts = [fmt.format(stats.successes) + ' successful', fmt.format(stats.failures) + ' failed'];
            if (stats.partialFailures > 0) parts.push(fmt.format(stats.partialFailures) + ' partial');
            if (stats.inFlight > 0) parts.push(fmt.format(stats.inFlight) + ' in flight');
            $('launch-outcomes').textContent = parts.join(' · ');
            if (Array.isArray(stats.providers) && stats.providers.length) showProviders(stats.providers);
            if (stats.latest) showLatest(stats.latest);
        } catch (e) {
            console.error('Couldn’t show the launch breakdown', e);
        }
    }

    // The server sorts providers by count, then name. The top few get a bar; the rest are summed.
    function showProviders(providers) {
        var list = $('launch-providers');
        var top = providers.slice(0, PROVIDERS_SHOWN);
        var max = top[0].count || 1;
        top.forEach(function (p) { list.appendChild(barRow(p.name, p.count, max)); });
        var others = providers.slice(PROVIDERS_SHOWN).reduce(function (sum, p) { return sum + p.count; }, 0);
        if (others > 0) list.appendChild(providerRow('Others', others, 'others'));
        list.hidden = false;
    }

    // A row with its bar filled relative to the biggest count. Also used by the Satellites card.
    function barRow(name, count, max, flag) {
        var row = providerRow(name, count, null, flag);
        var fill = el('span', 'launch-bar-fill');
        // Set from script, not a style="" attribute, so it still works once the CSP is enforced.
        fill.style.width = Math.max(2, Math.round(count / max * 100)) + '%';
        row.querySelector('.launch-bar').appendChild(fill);
        return row;
    }

    function providerRow(name, count, extra, flag) {
        var row = el('li', 'launch-provider' + (extra ? ' ' + extra : ''));
        row.appendChild(el('span', 'launch-bar'));
        var label = el('span', 'launch-provider-name');
        var img = flagImg(flag);
        if (img) label.appendChild(img);
        label.appendChild(document.createTextNode(name));
        label.title = name;
        row.appendChild(label);
        row.appendChild(el('span', 'launch-provider-count', fmt.format(count)));
        return row;
    }

    function showLatest(latest) {
        var t = new Date(latest.net);
        var box = $('launch-latest');
        box.textContent = 'Latest: ' + latest.name +
            (isNaN(t) ? '' : ' · ' + t.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }));
        box.hidden = false;
    }

    // The data can be up to an hour old, so a launch is kept for an hour after its time ("Launch window
    // open") and then skipped. The pick is redone on every tick, so the card moves on by itself.
    function pickNextLaunch(upcoming, now) {
        for (var i = 0; i < upcoming.length; i++) {
            var t = Date.parse(upcoming[i].net);
            if (!isNaN(t) && t > now - HOUR) return upcoming[i];
        }
        return null;
    }

    function startNextLaunch(upcoming) {
        var shown = null;
        function update() {
            var launch = pickNextLaunch(upcoming, Date.now());
            var box = $('launch-next');
            if (!launch) {
                box.hidden = true;
                return false;
            }
            if (launch !== shown) {
                showNextLaunch(launch);
                shown = launch;
            }
            updateCountdown(launch);
            box.hidden = false;
            return true;
        }
        try {
            if (update()) {
                setInterval(function () { if (!document.hidden) update(); }, 30000);
                // Ticks pause in a background tab, so catch up as soon as it's visible again.
                document.addEventListener('visibilitychange', function () { if (!document.hidden) update(); });
            }
        } catch (e) {
            console.error('Couldn’t show the next launch', e);
        }
    }

    function showNextLaunch(launch) {
        $('launch-name').textContent = launch.name;
        $('launch-where').textContent = [launch.provider, launch.location].filter(Boolean).join(' · ');

        var time = $('launch-time');
        time.setAttribute('datetime', launch.net);
        time.textContent = launchWhen(launch);
        $('launch-status').textContent = launchStatus(launch);
    }

    // The local time when the launch has a real one, otherwise just the day or month it's expected.
    function launchWhen(launch) {
        var t = new Date(launch.net);
        var precision = launch.precision || '';
        if (TIMED[precision]) {
            return t.toLocaleString(undefined,
                { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
        }
        if (precision === 'DAY') {
            // Shown in UTC: the placeholder time is midnight UTC, which is the day before in the Americas.
            return t.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC' });
        }
        return 'NET ' + t.toLocaleDateString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' });
    }

    function launchStatus(launch) {
        var status = STATUS_TEXT[launch.statusId] || launch.statusAbbrev || '';
        if (launch.probability != null) status += (status ? ' · ' : '') + launch.probability + '% weather go';
        return status;
    }

    function updateCountdown(launch) {
        var box = $('launch-countdown');
        var text = launchCountdown(launch, Date.now());
        box.hidden = !text;
        if (text) box.textContent = text;
    }

    // "T−2d 4h", "T−3h 12m", "T−8m"; "Launch window open" for an hour after T-0. Null unless the launch is
    // Go with a real time: anything else could count down to a placeholder.
    function launchCountdown(launch, now) {
        if (launch.statusId !== STATUS_GO || !TIMED[launch.precision]) return null;
        var ms = Date.parse(launch.net) - now;
        return ms <= 0 ? 'Launch window open' : countdown(ms);
    }

    function countdown(ms) {
        var minutes = Math.ceil(ms / 60000);
        var d = Math.floor(minutes / 1440), h = Math.floor(minutes % 1440 / 60), m = minutes % 60;
        if (d > 0) return 'T−' + d + 'd ' + h + 'h';
        if (h > 0) return 'T−' + h + 'h ' + m + 'm';
        return 'T−' + m + 'm';
    }

    // ---- Satellites ----

    // The function checks every 6 hours, so 30 hours means five checks in a row were missed.
    var SATELLITES_STALE_HOURS = 30;
    var OWNERS_SHOWN = 5;
    // SATCAT's code for objects with no owner yet. Counted in "Others", never shown on its own.
    var UNASSIGNED = 'TBD';

    // The section stays hidden unless a real number arrives. The number is shown first, and the rest
    // in a try/catch, so a problem with the breakdown can never hide the number too.
    getJson('/api/sky/satellites').then(function (data) {
        var section = $('satellites');
        if (!section || !data || typeof data.active !== 'number') return;
        $('sat-active').textContent = fmt.format(data.active);
        section.hidden = false;
        try {
            showSatelliteSource(data);
            if (data.starlink > 0 && data.active > 0) {
                var box = $('sat-starlink');
                box.textContent = fmt.format(data.starlink) + ' of them are Starlink (' +
                    Math.round(data.starlink / data.active * 100) + '%)';
                box.hidden = false;
            }
            if (Array.isArray(data.owners) && data.owners.length) showOwners(data.owners, data.active);
            if (typeof data.inOrbit === 'number') showInventory(data);
        } catch (e) {
            console.error('Couldn’t show the satellite breakdown', e);
        }
    }).catch(function () { /* no data and no cached copy: keep the card hidden */ });

    // The server sorts owners by count, then name. The top few get a bar and a flag; the rest are summed.
    function showOwners(owners, active) {
        var list = $('sat-owners');
        var top = owners.filter(function (o) { return o.code !== UNASSIGNED; }).slice(0, OWNERS_SHOWN);
        if (!top.length) return;
        var max = top[0].count || 1;
        var shown = 0;
        top.forEach(function (o) {
            list.appendChild(barRow(o.name, o.count, max, o.flag ? { code: o.flag, country: o.name } : null));
            shown += o.count;
        });
        if (active - shown > 0) list.appendChild(providerRow('Others', active - shown, 'others'));
        list.hidden = false;
    }

    // "34,970 tracked objects in Earth orbit: 20,095 satellites · 12,523 debris · 2,297 rocket bodies"
    function showInventory(data) {
        var parts = [fmt.format(data.payloads) + ' satellites', fmt.format(data.debris) + ' debris',
                     fmt.format(data.rocketBodies) + ' rocket bodies'];
        if (data.unknown > 0) parts.push(fmt.format(data.unknown) + ' unknown');
        var box = $('sat-inventory');
        box.textContent = fmt.format(data.inOrbit) + ' tracked objects in Earth orbit: ' + parts.join(' · ');
        box.hidden = false;
    }

    // "Source: CelesTrak SATCAT · catalog updated Oct 11 · checked 2 hours ago"
    function showSatelliteSource(data) {
        var t = new Date(data.catalogUpdated);
        if (data.catalogUpdated && !isNaN(t))
            $('satellites-source').appendChild(document.createTextNode(
                ' · catalog updated ' + t.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })));
        showUpdated('satellites-source', data.checked, SATELLITES_STALE_HOURS, 'checked');
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text) node.textContent = text;
        return node;
    }

    function flagImg(flag) {
        if (!flag || !/^[A-Z]{2}$/.test(flag.code)) return null;
        var img = el('img', 'flag');
        img.src = '/lib/flag-icons/4x3/' + flag.code.toLowerCase() + '.svg';
        img.width = 20;
        img.height = 15;
        img.alt = flag.country || flag.code;
        img.loading = 'lazy';
        // No flag for this code: drop the image and let the name stand on its own.
        img.addEventListener('error', function () { img.remove(); });
        return img;
    }

    // The server already sorts people by station group, then name, so groups appear in order.
    function showPeople(people) {
        var box = $('people-groups');
        if (!box) return;
        var groups = [];
        people.forEach(function (p) {
            var last = groups[groups.length - 1];
            if (!last || last.station !== p.station) groups.push(last = { station: p.station, people: [] });
            last.people.push(p);
        });

        groups.forEach(function (g) {
            var group = el('div', 'people-group');
            group.appendChild(el('h3', 'people-station', g.station + ' · ' + g.people.length));
            var chips = el('ul', 'people-chips');
            g.people.forEach(function (p) {
                var chip = el('li', 'person');
                var flags = el('span', 'flags');
                (p.flags || []).forEach(function (f) { var img = flagImg(f); if (img) flags.appendChild(img); });
                if (flags.childNodes.length) chip.appendChild(flags);
                var text = el('span', 'person-text');
                text.appendChild(el('span', 'person-name', p.name));
                if (p.agency) text.appendChild(el('span', 'person-agency', p.agency));
                chip.appendChild(text);
                chips.appendChild(chip);
            });
            group.appendChild(chips);
            box.appendChild(group);
        });
        box.hidden = false;
        $('people').classList.add('has-list');
    }

    function satById(id) { return SATS.filter(function (s) { return s.id === id; })[0]; }

    function updateReadout() {
        var p = position(state.selected, new Date());
        var name = $('sat-name');
        if (name) name.textContent = satById(state.selected).full;
        if (!p) return;
        $('sat-lat').textContent = Math.abs(p.lat).toFixed(2) + '° ' + (p.lat >= 0 ? 'N' : 'S');
        $('sat-lon').textContent = Math.abs(p.lon).toFixed(2) + '° ' + (p.lon >= 0 ? 'E' : 'W');
        $('sat-alt').textContent = fmt.format(p.alt) + ' km';
        $('sat-speed').textContent = p.speed ? fmt.format(p.speed) + ' km/h' : '—';
        showEpoch();
    }

    // Ticks every second next to the coordinates, so it's plain the numbers are for right now.
    function showUtc(now) {
        var utc = $('sky-utc');
        if (utc) utc.textContent = now.toISOString().slice(11, 19) + ' UTC';
    }

    // SGP4 is most accurate near the element set's epoch and drifts a few km a day after it, so the
    // epoch's age is shown, and called out once it's old enough that the dot may be noticeably off.
    var EPOCH_STALE_DAYS = 3;

    // Julian date -> ms since 1970. satellite.js keeps the TLE epoch (line 1, cols 19-32) as jdsatepoch.
    function epochOf(id) {
        var rec = satrecs[id];
        return rec && rec.jdsatepoch ? (rec.jdsatepoch - 2440587.5) * 86400000 : null;
    }

    // "12 min ago", "3 h ago", "2 days ago". Short, because it shares a line with the clock on phones.
    function shortAgo(ms) {
        var minutes = Math.max(0, Math.floor(ms / 60000));
        if (minutes < 60) return minutes + ' min ago';
        var hours = Math.floor(minutes / 60);
        if (hours < 48) return hours + ' h ago';
        return Math.floor(hours / 24) + ' days ago';
    }

    function showEpoch() {
        var span = $('sat-epoch'), epoch = epochOf(state.selected);
        if (!span || epoch === null) return;
        var age = Date.now() - epoch;
        span.textContent = 'Orbit data: CelesTrak · element set from ' + shortAgo(age);
        span.title = 'Element set epoch ' + new Date(epoch).toISOString().replace('T', ' ').slice(0, 16) + ' UTC';
        span.classList.toggle('stale', age > EPOCH_STALE_DAYS * 86400000);
    }

    function select(id) {
        if (!satById(id)) return;
        state.selected = id;
        document.querySelectorAll('[data-sat]').forEach(function (b) {
            b.setAttribute('aria-pressed', String(b.getAttribute('data-sat') === id));
        });
        updateReadout();
        emit('select');
    }

    function setFollowing(on) {
        state.following = on;
        var btn = $('sat-follow');
        if (btn) {
            btn.setAttribute('aria-pressed', String(on));
            btn.textContent = on ? 'Following' : 'Follow';
        }
        emit('follow');
    }

    function setView(view) {
        state.view = view;
        document.querySelectorAll('[data-view]').forEach(function (b) {
            var active = b.getAttribute('data-view') === view;
            b.setAttribute('aria-selected', String(active));
            b.tabIndex = active ? 0 : -1;
        });
        var g = $('sky-globe'), m = $('sky-map');
        if (g) g.hidden = view !== 'globe';
        if (m) m.hidden = view !== 'map';
        document.querySelectorAll('[data-view-only]').forEach(function (el) {
            el.hidden = el.getAttribute('data-view-only') !== view;
        });
        emit('view');
    }

    function setStatus(msg) {
        var s = $('sky-status');
        if (s) s.textContent = msg;
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('[data-sat]').forEach(function (b) {
            b.addEventListener('click', function () { select(b.getAttribute('data-sat')); });
        });
        document.querySelectorAll('[data-view]').forEach(function (b) {
            b.addEventListener('click', function () { setView(b.getAttribute('data-view')); });
        });
        var follow = $('sat-follow');
        if (follow) follow.addEventListener('click', function () { setFollowing(!state.following); });
        showUtc(new Date());
    });

    satsReady.then(function (failed) {
        state.ready = true;
        select(state.selected);
        if (failed.length) setStatus('Couldn’t load ' + failed.join(' and ') + ' right now.');
        emit('ready');
    });

    var tick = 0;
    setInterval(function () {
        if (document.hidden) return;
        tick++;
        showUtc(new Date());
        updateReadout();
        emit('tick');
        if (tick % 60 === 0) emit('minute');
    }, 1000);

    return {
        SATS: SATS,
        state: state,
        // Late subscribers (scripts that load after the data arrived) still get the 'ready' event.
        on: function (fn) {
            listeners.push(fn);
            if (state.ready) setTimeout(function () { fn('ready', state); }, 0);
        },
        hasSat: hasSat,
        position: position,
        periodMinutes: periodMinutes,
        track: track,
        subsolarPoint: subsolarPoint,
        footprintRadius: footprintRadius,
        angleBetween: angleBetween,
        circle: circle,
        unwrapNear: unwrapNear,
        satsReady: satsReady,
        auroraReady: auroraReady,
        launchesReady: launchesReady,
        pickNextLaunch: pickNextLaunch,
        launchCountdown: launchCountdown,
        launchWhen: launchWhen,
        launchStatus: launchStatus,
        loadAurora: loadAurora,
        select: select,
        setFollowing: setFollowing,
        setView: setView,
        setStatus: setStatus,
        rad: rad,
        deg: deg
    };
})();
