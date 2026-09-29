// Shared state and math for the Live Sky section (used by both the 3D globe and the flat map).
//  - Loads satellite orbital elements (TLEs) and the aurora forecast from our own API
//  - Propagates satellite positions in the browser with satellite.js (SGP4)
//  - Owns the readout panel, satellite picker, Follow button and Globe/Map toggle
window.TISSky = (function () {
    'use strict';

    var SATS = [
        { id: 'iss', name: 'ISS', full: 'International Space Station', color: '#b7c0ff' },
        { id: 'hubble', name: 'Hubble', full: 'Hubble Space Telescope', color: '#ffc46b' }
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

    function loadAurora() { return getJson('/api/sky/aurora'); }
    var auroraReady = loadAurora();

    // ---- UI: readouts, satellite picker, follow, view toggle ----
    var $ = function (id) { return document.getElementById(id); };
    var fmt = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });

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
