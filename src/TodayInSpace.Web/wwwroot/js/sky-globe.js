// 3D globe view for Live Sky (globe.gl / three.js): drag to rotate, scroll or pinch to zoom,
// click a satellite to select it. Shows ISS and Hubble at true relative altitude with their orbits,
// the aurora forecast, and sunlight coming from the Sun's real direction.
(function () {
    'use strict';

    var sky = window.TISSky;
    var el = document.getElementById('sky-globe');
    if (!sky || !el) return;

    function webglAvailable() {
        try {
            var c = document.createElement('canvas');
            return !!(window.WebGLRenderingContext && (c.getContext('webgl2') || c.getContext('webgl')));
        } catch (e) { return false; }
    }

    if (!window.Globe || !webglAvailable()) {
        // No 3D support: fall back to the flat map and hide the toggle.
        var toggle = document.querySelector('.view-toggle');
        if (toggle) toggle.hidden = true;
        sky.setView('map');
        return;
    }

    var EARTH_RADIUS_KM = 6371;
    var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    var globe = new Globe(el, { animateIn: false })
        .backgroundColor('rgba(0,0,0,0)')
        .globeImageUrl(el.getAttribute('data-earth-texture'))
        .showAtmosphere(true)
        .atmosphereColor('#7aa2ff')
        .atmosphereAltitude(0.18)
        .width(el.clientWidth)
        .height(el.clientHeight);

    // ---- Lighting: a "sun" light from the real subsolar direction, so the night side is dark ----
    var sunLight = null, ambient = null;
    (globe.lights() || []).forEach(function (l) {
        if (l.isDirectionalLight) sunLight = l;
        if (l.isAmbientLight) ambient = l;
    });
    if (ambient) ambient.intensity = 0.35;
    if (sunLight) sunLight.intensity = 3.4;

    function updateSun() {
        if (!sunLight) return;
        var s = sky.subsolarPoint(new Date());
        var p = globe.getCoords(s.lat, s.lon, 50);
        if (sunLight.parent && sunLight.parent.isCamera) {
            // If the light rides along with the camera, move it into the scene so it stays fixed to the Sun.
            sunLight.parent.remove(sunLight);
            globe.scene().add(sunLight);
        }
        sunLight.position.set(p.x, p.y, p.z);
        if (sunLight.target && sunLight.target.position) sunLight.target.position.set(0, 0, 0);
    }

    // ---- Satellites (HTML markers that follow the globe and hide behind it) ----
    var markers = sky.SATS.map(function (s) {
        var node = document.createElement('button');
        node.type = 'button';
        node.className = 'globe-sat globe-sat--' + s.id;
        node.style.setProperty('--sat-color', s.color);
        node.setAttribute('aria-label', 'Select ' + s.full);
        node.innerHTML = '<span class="globe-sat__pulse"></span><span class="globe-sat__dot"></span><span class="globe-sat__label">' + s.name + '</span>';
        node.addEventListener('click', function () { sky.select(s.id); });
        return { id: s.id, node: node, lat: 0, lng: 0, alt: 0, visible: false };
    });

    globe
        .htmlElementsData([])
        .htmlLat('lat')
        .htmlLng('lng')
        .htmlAltitude('alt')
        .htmlElement(function (d) { return d.node; })
        .htmlTransitionDuration(reduceMotion ? 0 : 1000);

    function updateMarkers() {
        var now = new Date();
        var shown = [];
        markers.forEach(function (m) {
            var p = sky.position(m.id, now);
            if (!p) return;
            // Keep longitude continuous so the 1-second tween never spins the long way around.
            m.lng = m.visible ? sky.unwrapNear(p.lon, m.lng) : p.lon;
            m.lat = p.lat;
            m.alt = p.alt / EARTH_RADIUS_KM;
            m.visible = true;
            m.node.classList.toggle('is-selected', m.id === sky.state.selected);
            shown.push(m);
        });
        globe.htmlElementsData(shown);
    }

    // ---- Orbits: the next full orbit bright, the last half-orbit faint ----
    globe
        .pathsData([])
        .pathPoints('pts')
        .pathPointLat(function (p) { return p.lat; })
        .pathPointLng(function (p) { return p.lon; })
        .pathPointAlt(function (p) { return p.alt / EARTH_RADIUS_KM; })
        .pathColor('color')
        .pathStroke('stroke')
        .pathTransitionDuration(0);

    // Fat (wide) lines can't be transparent, so fades are done by blending toward the background color.
    function shade(hex, t) {
        var n = parseInt(hex.slice(1), 16), bg = [11, 16, 32];
        var c = [n >> 16 & 255, n >> 8 & 255, n & 255].map(function (v, i) { return Math.round(bg[i] + (v - bg[i]) * t); });
        return 'rgb(' + c.join(',') + ')';
    }

    function updateOrbits() {
        var paths = [];
        sky.SATS.forEach(function (s) {
            if (!sky.hasSat(s.id)) return;
            var period = sky.periodMinutes(s.id);
            var selected = s.id === sky.state.selected;
            // Next full orbit: bright at the satellite, fading toward the end.
            paths.push({
                pts: sky.track(s.id, 0, period, 20),
                color: [shade(s.color, selected ? 1 : 0.7), shade(s.color, 0.15)],
                stroke: selected ? 0.3 : 0.18
            });
            // Last half orbit: faint.
            paths.push({
                pts: sky.track(s.id, -period / 2, 0, 20),
                color: [shade(s.color, 0.05), shade(s.color, selected ? 0.45 : 0.3)],
                stroke: 0.12
            });
        });
        globe.pathsData(paths);
    }

    // ---- Aurora forecast as a glowing band of points ----
    function auroraColor(p) {
        if (p >= 50) return '#ff5d8f';
        if (p >= 30) return '#ffd166';
        if (p >= 15) return '#7dffb3';
        return '#35d49a';
    }

    globe
        .pointsData([])
        .pointLat(function (d) { return d[1]; })
        .pointLng(function (d) { return d[0]; })
        .pointColor(function (d) { return auroraColor(d[2]); })
        .pointAltitude(function (d) { return 0.004 + d[2] / 4000; })
        .pointRadius(0.55)
        .pointsMerge(true);

    function showAurora(data) {
        globe.pointsData(data && data.points ? data.points : []);
    }

    // ---- Camera: slow auto-rotate until the visitor grabs the globe ----
    var controls = globe.controls();
    controls.autoRotate = !reduceMotion;
    controls.autoRotateSpeed = 0.35;
    controls.enableDamping = true;
    controls.minDistance = globe.getGlobeRadius() * 1.3;
    controls.maxDistance = globe.getGlobeRadius() * 6;
    controls.addEventListener('start', function () {
        controls.autoRotate = false;
        if (sky.state.following) sky.setFollowing(false);
    });

    function lookAtSelected(ms) {
        var p = sky.position(sky.state.selected, new Date());
        if (p) globe.pointOfView({ lat: p.lat, lng: p.lon, altitude: Math.max(globe.pointOfView().altitude, 1.5) }, ms);
    }

    // Start zoomed out, then glide to the selected satellite once its orbit data has loaded.
    var centered = false;
    var startAltitude = el.clientWidth < 600 ? 2.1 : 1.75;
    globe.pointOfView({ lat: 15, lng: 0, altitude: 3.2 }, 0);
    function centerOnSelected() {
        if (centered) return;
        var p = sky.position(sky.state.selected, new Date());
        if (!p) return;
        centered = true;
        globe.pointOfView({ lat: Math.max(-40, Math.min(40, p.lat)), lng: p.lon, altitude: startAltitude }, reduceMotion ? 0 : 2000);
    }

    // ---- Wire up ----
    sky.on(function (type) {
        if (type === 'ready') {
            updateSun();
            updateMarkers();
            updateOrbits();
            centerOnSelected();
        } else if (type === 'tick') {
            if (sky.state.view !== 'globe') return;
            updateMarkers();
            if (sky.state.following) lookAtSelected(1000);
        } else if (type === 'minute') {
            updateSun();
            updateOrbits();
        } else if (type === 'select') {
            updateOrbits();
            updateMarkers();
            if (sky.state.view === 'globe') {
                controls.autoRotate = false;
                lookAtSelected(1200);
            }
        } else if (type === 'follow') {
            if (sky.state.following) { controls.autoRotate = false; lookAtSelected(800); }
        } else if (type === 'view') {
            if (sky.state.view === 'globe') {
                globe.width(el.clientWidth).height(el.clientHeight);
                globe.resumeAnimation();
            } else {
                globe.pauseAnimation();
            }
        }
    });

    updateSun();
    sky.auroraReady.then(showAurora).catch(function () { /* map legend shows status */ });
    setInterval(function () {
        if (document.hidden) return;
        sky.loadAurora().then(showAurora).catch(function () { });
    }, 10 * 60 * 1000);

    // Keep the canvas sized to its box; pause rendering while scrolled out of view to save battery.
    window.addEventListener('resize', function () {
        if (!el.hidden) globe.width(el.clientWidth).height(el.clientHeight);
    });
    if ('IntersectionObserver' in window) {
        new IntersectionObserver(function (entries) {
            entries.forEach(function (e) {
                if (sky.state.view !== 'globe') return;
                if (e.isIntersecting) globe.resumeAnimation(); else globe.pauseAnimation();
            });
        }).observe(el);
    }
})();
