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
        showUpdated(data.updated);
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

    // Adds when the list was fetched to the card's source line, and says so if it's getting old.
    // Old data is still shown: being honest about its age beats hiding it.
    function showUpdated(updated) {
        var source = $('people-source');
        var t = new Date(updated);
        if (!source || !updated || isNaN(t)) return;
        var age = Math.max(0, Date.now() - t);
        source.title = 'Updated ' + t.toLocaleString();
        source.appendChild(document.createTextNode(' · updated ' + timeAgo(age)));
        if (age > PEOPLE_STALE_HOURS * 3600000)
            source.appendChild(el('span', 'stale', ' · may be out of date'));
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
        unwrapNear: unwrapNear,
        satsReady: satsReady,
        auroraReady: auroraReady,
        loadAurora: loadAurora,
        select: select,
        setFollowing: setFollowing,
        setView: setView,
        setStatus: setStatus,
        rad: rad,
        deg: deg
    };
})();
