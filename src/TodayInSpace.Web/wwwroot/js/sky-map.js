// Live Sky map: ISS position and orbit (propagated in the browser from a TLE with satellite.js),
// NOAA's aurora forecast, and the current day/night terminator, drawn with Leaflet.
(function () {
    'use strict';

    var el = document.getElementById('sky-map');
    if (!el || !window.L || !window.satellite) return;

    var $ = function (id) { return document.getElementById(id); };
    var rad = function (d) { return d * Math.PI / 180; };
    var deg = function (r) { return r * 180 / Math.PI; };
    var wrapLon = function (lon) { return ((lon + 540) % 360) - 180; };

    // ---- Map ----
    var map = L.map(el, {
        center: [20, 0],
        zoom: 2,
        minZoom: 1,
        maxZoom: 6,
        worldCopyJump: true,
        scrollWheelZoom: false,   // don't hijack page scrolling; use +/- or pinch
        zoomSnap: 0.5
    });

    L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
        subdomains: 'abcd',
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> &copy; <a href="https://carto.com/attributions">CARTO</a>'
    }).addTo(map);

    var canvas = L.canvas({ padding: 0.5 });

    var nightLayer = L.polygon([], {
        renderer: canvas, stroke: false, fillColor: '#00030d', fillOpacity: 0.5, interactive: false
    }).addTo(map);

    var auroraLayer = L.layerGroup().addTo(map);

    var trackPast = L.polyline([], { renderer: canvas, color: '#9aa6ff', weight: 1.5, opacity: 0.45, dashArray: '3 6', interactive: false });
    var trackNext = L.polyline([], { renderer: canvas, color: '#b7c0ff', weight: 2, opacity: 0.9, interactive: false });
    var trackLayer = L.layerGroup([trackPast, trackNext]).addTo(map);

    var issIcon = L.divIcon({
        className: 'iss-marker',
        html: '<span class="iss-pulse"></span><span class="iss-dot"></span>',
        iconSize: [24, 24],
        iconAnchor: [12, 12]
    });
    var issMarker = L.marker([0, 0], { icon: issIcon, keyboard: false, title: 'International Space Station' });
    var issLayer = L.layerGroup([issMarker]).addTo(map);

    L.control.layers(null, {
        'Space station': issLayer,
        'Orbit path': trackLayer,
        'Aurora forecast': auroraLayer,
        'Night side': nightLayer
    }, { position: 'topright' }).addTo(map);

    // ---- ISS (SGP4 propagation) ----
    var satrec = null;
    var following = false;

    function issAt(date) {
        var pv = satellite.propagate(satrec, date);
        if (!pv || !pv.position) return null;
        var geo = satellite.eciToGeodetic(pv.position, satellite.gstime(date));
        var v = pv.velocity;
        return {
            lat: satellite.degreesLat(geo.latitude),
            lon: satellite.degreesLong(geo.longitude),
            alt: geo.height,
            speed: v ? Math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * 3600 : null // km/s -> km/h
        };
    }

    // Ground track split wherever it crosses the antimeridian so lines don't streak across the map.
    function groundTrack(fromMin, toMin, stepSec) {
        var segments = [[]];
        var prevLon = null;
        var now = Date.now();
        for (var t = fromMin * 60; t <= toMin * 60; t += stepSec) {
            var p = issAt(new Date(now + t * 1000));
            if (!p) continue;
            if (prevLon !== null && Math.abs(p.lon - prevLon) > 180) segments.push([]);
            segments[segments.length - 1].push([p.lat, p.lon]);
            prevLon = p.lon;
        }
        return segments;
    }

    function updateTracks() {
        if (!satrec) return;
        trackPast.setLatLngs(groundTrack(-45, 0, 30));
        trackNext.setLatLngs(groundTrack(0, 95, 30)); // ~one orbit ahead
    }

    var fmt = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });

    function updateIss() {
        if (!satrec) return;
        var p = issAt(new Date());
        if (!p) return;
        issMarker.setLatLng([p.lat, p.lon]);
        $('iss-lat').textContent = Math.abs(p.lat).toFixed(2) + '° ' + (p.lat >= 0 ? 'N' : 'S');
        $('iss-lon').textContent = Math.abs(p.lon).toFixed(2) + '° ' + (p.lon >= 0 ? 'E' : 'W');
        $('iss-alt').textContent = fmt.format(p.alt) + ' km';
        $('iss-speed').textContent = p.speed ? fmt.format(p.speed) + ' km/h' : '—';
        if (following) map.panTo([p.lat, p.lon], { animate: true, duration: 0.8 });
    }

    var followBtn = $('iss-follow');
    if (followBtn) {
        followBtn.addEventListener('click', function () {
            following = !following;
            followBtn.setAttribute('aria-pressed', String(following));
            followBtn.textContent = following ? 'Following ISS' : 'Follow ISS';
            if (following) {
                var p = satrec && issAt(new Date());
                if (p) map.setView([p.lat, p.lon], Math.max(map.getZoom(), 3));
            }
        });
    }
    map.on('dragstart', function () {
        if (following && followBtn) followBtn.click(); // user took over the map
    });

    function loadIss() {
        return fetch('/api/sky/iss-tle')
            .then(function (r) { if (!r.ok) throw new Error('TLE ' + r.status); return r.json(); })
            .then(function (tle) {
                satrec = satellite.twoline2satrec(tle.line1, tle.line2);
                updateIss();
                updateTracks();
            });
    }

    // ---- Day / night terminator ----
    // Low-precision solar position (accurate to a fraction of a degree), from the standard
    // almanac approximation: mean anomaly -> ecliptic longitude -> declination / right ascension.
    function subsolarPoint(date) {
        var d = date.getTime() / 86400000 - 10957.5;           // days since J2000.0
        var g = rad(357.529 + 0.98560028 * d);                   // mean anomaly
        var q = 280.459 + 0.98564736 * d;                        // mean longitude
        var lambda = rad(q + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g));
        var eps = rad(23.439 - 0.00000036 * d);                  // obliquity
        var dec = Math.asin(Math.sin(eps) * Math.sin(lambda));
        var ra = Math.atan2(Math.cos(eps) * Math.sin(lambda), Math.cos(lambda));
        var gmst = 280.46061837 + 360.98564736629 * d;           // degrees
        return { lat: deg(dec), lon: wrapLon(deg(ra) - gmst) };
    }

    function nightPolygon(date) {
        var sun = subsolarPoint(date);
        var tanDec = Math.tan(rad(sun.lat));
        if (Math.abs(tanDec) < 1e-6) tanDec = tanDec < 0 ? -1e-6 : 1e-6;
        var pts = [];
        for (var lon = -180; lon <= 180; lon += 2) {
            var lat = deg(Math.atan(-Math.cos(rad(lon - sun.lon)) / tanDec));
            pts.push([lat, lon]);
        }
        var darkPole = sun.lat > 0 ? -90 : 90;
        pts.push([darkPole, 180], [darkPole, -180]);
        return pts;
    }

    function updateNight() {
        nightLayer.setLatLngs(nightPolygon(new Date()));
    }

    // ---- Aurora ----
    function auroraColor(p) {
        if (p >= 50) return '#ff5d8f';
        if (p >= 30) return '#ffd166';
        if (p >= 15) return '#7dffb3';
        return '#35d49a';
    }

    function loadAurora() {
        return fetch('/api/sky/aurora')
            .then(function (r) { if (!r.ok) throw new Error('Aurora ' + r.status); return r.json(); })
            .then(function (data) {
                auroraLayer.clearLayers();
                data.points.forEach(function (pt) {
                    var lon = pt[0], lat = pt[1], p = pt[2];
                    L.rectangle([[lat - 0.5, lon - 0.5], [lat + 0.5, lon + 0.5]], {
                        renderer: canvas,
                        stroke: false,
                        fillColor: auroraColor(p),
                        fillOpacity: Math.min(0.12 + p / 90, 0.75),
                        interactive: false
                    }).addTo(auroraLayer);
                });
                if (data.forecastTime) {
                    var t = new Date(data.forecastTime);
                    $('aurora-time').textContent = isNaN(t) ? data.forecastTime :
                        t.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
                }
            });
    }

    // ---- Status + loop ----
    function setStatus(msg) {
        var s = $('sky-status');
        if (s) s.textContent = msg;
    }

    updateNight();
    Promise.allSettled([loadIss(), loadAurora()]).then(function (results) {
        var failed = [];
        if (results[0].status === 'rejected') failed.push('space station');
        if (results[1].status === 'rejected') failed.push('aurora forecast');
        setStatus(failed.length ? 'Couldn’t load ' + failed.join(' and ') + ' right now.' : '');
    });

    var tick = 0;
    setInterval(function () {
        if (document.hidden) return;
        tick++;
        updateIss();
        if (tick % 60 === 0) { updateTracks(); updateNight(); }
        if (tick % 600 === 0) loadAurora().catch(function () { /* keep last layer */ });
    }, 1000);
})();
