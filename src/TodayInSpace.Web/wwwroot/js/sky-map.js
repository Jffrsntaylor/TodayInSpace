// Flat map view for Live Sky (Leaflet): satellites and their ground tracks, NOAA's aurora
// forecast, and the day/night terminator. Shares data and state with the globe via TISSky.
(function () {
    'use strict';

    var sky = window.TISSky;
    var el = document.getElementById('sky-map');
    if (!sky || !el || !window.L) return;

    var map = null;
    var satLayers = {};
    var nightLayer, auroraLayer, canvas;
    var lastAurora = null;

    function auroraColor(p) {
        if (p >= 50) return '#ff5d8f';
        if (p >= 30) return '#ffd166';
        if (p >= 15) return '#7dffb3';
        return '#35d49a';
    }

    // The map is created the first time its tab is shown (Leaflet needs a visible container to size itself).
    function init() {
        if (map) return;
        var narrow = el.clientWidth < 600;
        map = L.map(el, {
            center: [20, 0],
            zoom: narrow ? 1 : 2,
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

        canvas = L.canvas({ padding: 0.5 });
        nightLayer = L.polygon([], { renderer: canvas, stroke: false, fillColor: '#00030d', fillOpacity: 0.5, interactive: false }).addTo(map);
        auroraLayer = L.layerGroup().addTo(map);

        var overlays = {};
        sky.SATS.forEach(function (s) {
            // Each track is a group of short polylines so it can fade out along its length.
            var past = L.layerGroup();
            var next = L.layerGroup();
            var icon = L.divIcon({
                className: 'map-sat map-sat--' + s.id,
                html: '<span class="map-sat__pulse"></span><span class="map-sat__dot"></span><span class="map-sat__label">' + s.name + '</span>',
                iconSize: [24, 24],
                iconAnchor: [12, 12]
            });
            var marker = L.marker([0, 0], { icon: icon, keyboard: false, title: s.full });
            marker.on('click', function () { sky.select(s.id); });
            var group = L.layerGroup([past, next, marker]).addTo(map);
            satLayers[s.id] = { past: past, next: next, marker: marker, sat: s };
            overlays[s.name] = group;
        });
        overlays['Aurora forecast'] = auroraLayer;
        overlays['Night side'] = nightLayer;
        L.control.layers(null, overlays, { position: 'topright' }).addTo(map);

        map.on('moveend', function () { if (!sky.state.following) updateTracks(); });
        map.on('dragstart', function () { if (sky.state.following) sky.setFollowing(false); });

        var p = sky.position(sky.state.selected, new Date());
        if (p) map.setView([Math.max(-45, Math.min(45, p.lat)), p.lon], map.getZoom(), { animate: false });

        updateNight();
        updateTracks();
        updateMarkers();
        if (lastAurora) drawAurora(lastAurora);
    }

    // Leaflet lines have a single opacity, so a fading track is drawn as bands whose opacity steps
    // from `from` at the first point to `to` at the last. Enough bands that the steps don't show.
    var FADE_BANDS = 24;
    function drawFaded(group, latlngs, style, from, to) {
        group.clearLayers();
        var last = latlngs.length - 1;
        var bands = Math.min(FADE_BANDS, last);
        for (var i = 0; i < bands; i++) {
            var a = Math.floor(i * last / bands), b = Math.floor((i + 1) * last / bands);
            group.addLayer(L.polyline(latlngs.slice(a, b + 1), L.extend({
                renderer: canvas,
                opacity: from + (to - from) * (i + 0.5) / bands,
                interactive: false
            }, style)));
        }
    }

    // Same rule as the globe: the selected satellite gets its next orbit bold plus its last half-orbit
    // dashed; the others get only their next orbit, thin and dim, so the map isn't a tangle of six lines.
    // Every track fades out at its far end, so no line stops with a hard edge.
    function updateTracks() {
        if (!map) return;
        var center = map.getCenter().lng;
        sky.SATS.forEach(function (s) {
            var layers = satLayers[s.id];
            var now = sky.position(s.id, new Date());
            if (!layers || !now) return;
            var selected = s.id === sky.state.selected;
            var ref = sky.unwrapNear(now.lon, center);
            var period = sky.periodMinutes(s.id);
            var toLatLng = function (pt) { return [pt.lat, pt.lon]; };
            drawFaded(layers.next, sky.track(s.id, 0, period, 30, ref).map(toLatLng),
                { color: s.color, weight: selected ? 2.5 : 1.2 }, selected ? 0.95 : 0.5, 0);
            // Walks backward from now, so it starts at the satellite and fades toward its oldest point.
            drawFaded(layers.past, selected ? sky.track(s.id, 0, -period / 2, 30, ref).map(toLatLng) : [],
                { color: s.color, weight: 1.5, dashArray: '3 6' }, 0.45, 0);
        });
    }

    function updateMarkers() {
        if (!map) return;
        var center = map.getCenter().lng;
        sky.SATS.forEach(function (s) {
            var layers = satLayers[s.id];
            var p = sky.position(s.id, new Date());
            if (!layers || !p) return;
            layers.marker.setLatLng([p.lat, sky.unwrapNear(p.lon, center)]);
            var node = layers.marker.getElement();
            if (node) node.classList.toggle('is-selected', s.id === sky.state.selected);
        });
        if (sky.state.following) {
            var sel = sky.position(sky.state.selected, new Date());
            if (sel) map.panTo([sel.lat, sky.unwrapNear(sel.lon, center)], { animate: true, duration: 0.8 });
        }
    }

    // Night-side polygon, drawn across three world widths so it still covers the map after panning past 180°.
    function updateNight() {
        if (!map) return;
        var sun = sky.subsolarPoint(new Date());
        var tanDec = Math.tan(sky.rad(sun.lat));
        if (Math.abs(tanDec) < 1e-6) tanDec = tanDec < 0 ? -1e-6 : 1e-6;
        var pts = [];
        for (var lon = -540; lon <= 540; lon += 2) {
            pts.push([sky.deg(Math.atan(-Math.cos(sky.rad(lon - sun.lon)) / tanDec)), lon]);
        }
        var darkPole = sun.lat > 0 ? -90 : 90;
        pts.push([darkPole, 540], [darkPole, -540]);
        nightLayer.setLatLngs(pts);
    }

    function drawAurora(data) {
        lastAurora = data;
        if (!map) return;
        auroraLayer.clearLayers();
        // One copy per world width (-360, 0, +360) so the ovals don't vanish after panning.
        [-360, 0, 360].forEach(function (shift) {
            data.points.forEach(function (pt) {
                var lon = pt[0] + shift, lat = pt[1], p = pt[2];
                L.rectangle([[lat - 0.5, lon - 0.5], [lat + 0.5, lon + 0.5]], {
                    renderer: canvas,
                    stroke: false,
                    fillColor: auroraColor(p),
                    fillOpacity: Math.min(0.12 + p / 90, 0.75),
                    interactive: false
                }).addTo(auroraLayer);
            });
        });
    }

    // sky-core.js shows the forecast time and any load error; this just draws the ovals.
    sky.auroraReady.then(drawAurora).catch(function () { });
    setInterval(function () {
        if (document.hidden) return;
        sky.loadAurora().then(drawAurora).catch(function () { });
    }, 10 * 60 * 1000);

    sky.on(function (type) {
        if (type === 'view' && sky.state.view === 'map') {
            init();
            map.invalidateSize();
            updateTracks();
            updateMarkers();
        }
        if (!map || sky.state.view !== 'map') return;
        if (type === 'tick') updateMarkers();
        else if (type === 'minute') { updateTracks(); updateNight(); }
        else if (type === 'select' || type === 'ready') {
            updateTracks();   // the bold orbit and the past track follow the selection
            updateMarkers();
            var p = sky.position(sky.state.selected, new Date());
            if (p && type === 'select') map.panTo([p.lat, sky.unwrapNear(p.lon, map.getCenter().lng)]);
        }
    });

    // sky-loader.js loads this file after the Map view is already showing, so the 'view'
    // event that would normally create the map has been and gone.
    if (sky.state.view === 'map') init();
})();
