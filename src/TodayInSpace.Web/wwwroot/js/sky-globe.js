// 3D globe view for Live Sky (globe.gl / three.js): drag to rotate, scroll or pinch to zoom,
// click a satellite to select it. Shows ISS, Hubble and Tiangong at true relative altitude with their orbits,
// the aurora forecast as a glowing shell, city lights on the night side, and sunlight from the Sun's real direction.
// With a location saved in "Your sky", also a pin there, your next visible ISS pass drawn in space, and the
// selected satellite's footprint: where on Earth it's at least 10° above the horizon right now.
// The upcoming launches' pads get a small rocket, and the next one a label with its countdown.
// Chips under the globe add every other active satellite as a cloud of tiny dots (Starlink, navigation,
// geostationary, everything else); tap a dot for its name.
(function () {
    'use strict';

    var sky = window.TISSky;
    var el = document.getElementById('sky-globe');
    if (!sky || !el) return;

    // Clear the "Loading 3D globe…" placeholder that shows while sky-loader.js fetches globe.gl.
    el.textContent = '';

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
        // The city-light and aurora shaders read the same direction to decide where it's night.
        if (sunDir) sunDir.set(p.x, p.y, p.z).normalize();
    }

    // ---- Night side: city lights and the aurora shell ----
    // globe.gl bundles its own three.js and doesn't expose it, so the few classes we need are borrowed
    // from objects it has already built: Mesh from the globe, ShaderMaterial from the atmosphere,
    // Texture from the day map, Vector3 from the sun light.
    var sunDir = sunLight ? new sunLight.position.constructor(1, 0, 0) : null;

    // Shared GLSL: 1 on the night side, 0 in daylight, fading across a ~13° twilight band
    // (from the Sun 3° above the horizon to 10° below) so the terminator glows instead of cutting.
    var NIGHT_GLSL = 'float tisNight(vec3 n, vec3 s) { return 1.0 - smoothstep(-0.17, 0.05, dot(normalize(n), s)); }\n';

    function findGlobeParts() {
        var mat = globe.globeMaterial(), globeMesh = null, atmosphere = null;
        globe.scene().traverse(function (o) {
            if (o.isMesh && o.material === mat) globeMesh = o;
            if (o.__globeObjType === 'atmosphere') atmosphere = o;
        });
        return { mat: mat, mesh: globeMesh, atmosphere: atmosphere };
    }

    function loadImage(url) {
        return new Promise(function (resolve, reject) {
            var img = new Image();
            img.onload = function () { resolve(img); };
            img.onerror = reject;
            img.src = url;
        });
    }

    // City lights: keep the globe's own Phong material (so the day side is lit exactly as before) and add
    // the night texture as emissive light, scaled by how dark it is at that spot.
    function addCityLights(parts, url) {
        if (!url || !sunDir || !parts.mat || !parts.mat.map) return;
        loadImage(url).then(function (img) {
            var tex = new parts.mat.map.constructor(img);
            tex.colorSpace = parts.mat.map.colorSpace;
            tex.anisotropy = parts.mat.map.anisotropy;
            tex.needsUpdate = true;

            parts.mat.onBeforeCompile = function (shader) {
                shader.uniforms.tisNightMap = { value: tex };
                shader.uniforms.tisSunDir = { value: sunDir };
                shader.vertexShader = 'varying vec3 vTisNormal;\n' + shader.vertexShader.replace(
                    '#include <defaultnormal_vertex>',
                    '#include <defaultnormal_vertex>\nvTisNormal = normalize(mat3(modelMatrix) * objectNormal);');
                shader.fragmentShader = 'uniform sampler2D tisNightMap;\nuniform vec3 tisSunDir;\nvarying vec3 vTisNormal;\n' + NIGHT_GLSL +
                    shader.fragmentShader.replace('#include <emissivemap_fragment>', [
                        '#include <emissivemap_fragment>',
                        '#ifdef USE_MAP',
                        '  vec3 tisLights = texture2D(tisNightMap, vMapUv).rgb;',
                        // The Black Marble map also shows moonlit land in dim blue; keep only the actual lights
                        // so the night side still reads as night, then warm them slightly.
                        '  float tisLum = dot(tisLights, vec3(0.2126, 0.7152, 0.0722));',
                        '  tisLights *= smoothstep(0.05, 0.2, tisLum) * vec3(1.0, 0.88, 0.7) * 1.4;',
                        '  totalEmissiveRadiance += tisLights * tisNight(vTisNormal, tisSunDir);',
                        '#endif'
                    ].join('\n'));
            };
            parts.mat.needsUpdate = true;
        }).catch(function (e) { console.warn('Night lights unavailable; globe stays as is.', e); });
    }

    // Aurora: the OVATION grid painted onto an equirectangular canvas, wrapped on a shell ~100 km up.
    var AURORA_SCALE = 1 + 100 / EARTH_RADIUS_KM;
    var auroraShell = null, auroraSmall = null, auroraCanvas = null, auroraTex = null, lastAurora = null;

    // Probability (%) -> [r, g, b, alpha]: soft green when faint, bright green as it strengthens,
    // and a touch of red/magenta at the top end, like the upper fringes of a strong display.
    function auroraRgba(p) {
        var g = Math.min(1, (p - 5) / 25), hi = Math.max(0, Math.min(0.6, (p - 30) / 50));
        var r = 40 + 70 * g, gr = 200 + 55 * g, b = 120 + 30 * g;
        r += (255 - r) * hi; gr += (80 - gr) * hi; b += (170 - b) * hi;
        // Alpha rises steeply so the wide 5–15% fringe is only a haze and the likely core stands out.
        var a = Math.min(1, (p - 5) / 35);
        return [r, gr, b, 255 * (0.08 + 0.92 * a * a)];
    }

    function paintAurora(points) {
        var w = 360, h = 180;
        var ctx = auroraSmall.getContext('2d');
        var img = ctx.createImageData(w, h);
        (points || []).forEach(function (d) {
            if (d[2] < 5) return;
            // OVATION longitudes run 0–359 east of Greenwich; the texture's left edge is 180° W.
            var x = Math.round(d[0] + 180) % w, y = Math.max(0, Math.min(h - 1, Math.round(90 - d[1])));
            var c = auroraRgba(d[2]), i = (y * w + x) * 4;
            img.data[i] = c[0]; img.data[i + 1] = c[1]; img.data[i + 2] = c[2]; img.data[i + 3] = c[3];
        });
        ctx.putImageData(img, 0, 0);

        // Upscale with a small blur so it reads as a glow, not a grid of 1° pixels.
        var big = auroraCanvas.getContext('2d');
        big.clearRect(0, 0, auroraCanvas.width, auroraCanvas.height);
        if ('filter' in big) big.filter = 'blur(2px)';
        big.drawImage(auroraSmall, 0, 0, auroraCanvas.width, auroraCanvas.height);
        auroraTex.needsUpdate = true;
    }

    function addAuroraShell(parts) {
        if (!sunDir || !parts.mesh || !parts.atmosphere || !parts.mat.map) return false;
        auroraSmall = document.createElement('canvas');
        auroraSmall.width = 360; auroraSmall.height = 180;
        auroraCanvas = document.createElement('canvas');
        auroraCanvas.width = 720; auroraCanvas.height = 360;
        auroraTex = new parts.mat.map.constructor(auroraCanvas);

        var uniforms = { auroraMap: { value: auroraTex }, sunDir: { value: sunDir }, time: { value: 0 } };
        var material = new parts.atmosphere.material.constructor({
            uniforms: uniforms,
            transparent: true,
            depthWrite: false,
            blending: 2, // THREE.AdditiveBlending
            vertexShader: [
                'varying vec2 vUv;',
                'varying vec3 vNormalW;',
                'void main() {',
                '  vUv = uv;',
                '  vNormalW = normalize(mat3(modelMatrix) * normal);',
                '  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);',
                '}'
            ].join('\n'),
            fragmentShader: 'uniform sampler2D auroraMap;\nuniform vec3 sunDir;\nuniform float time;\nvarying vec2 vUv;\nvarying vec3 vNormalW;\n' + NIGHT_GLSL + [
                'void main() {',
                '  vec4 a = texture2D(auroraMap, vUv);',
                // Two slow waves drifting along longitude at different speeds: a gentle, uneven shimmer.
                '  float shimmer = 1.0 + 0.2 * sin(time * 0.7 + vUv.x * 70.0) * sin(time * 0.43 + vUv.x * 23.0 + 1.7);',
                // Faint by day so it doesn't look painted onto the sunlit side.
                '  float k = mix(0.15, 1.0, tisNight(vNormalW, sunDir)) * shimmer;',
                '  gl_FragColor = vec4(a.rgb * k, a.a * k);',
                '}'
            ].join('\n')
        });

        // A child of the globe mesh, sharing its sphere, so it inherits the same rotation and texture layout.
        auroraShell = new parts.mesh.constructor(parts.mesh.geometry, material);
        auroraShell.scale.setScalar(AURORA_SCALE);
        // Runs only when the globe actually renders a frame, so a paused or offscreen globe does no work.
        auroraShell.onBeforeRender = function () {
            if (!reduceMotion) uniforms.time.value = performance.now() / 1000;
        };
        parts.mesh.add(auroraShell);
        return true;
    }

    // If either shader fails to compile, go back to the plain globe and the aurora points layer.
    var renderer = globe.renderer();
    if (renderer && renderer.debug) {
        renderer.debug.onShaderError = function () {
            console.warn('Globe night shaders failed to compile; using the plain globe and aurora points.');
            renderer.debug.onShaderError = null;
            var parts = findGlobeParts();
            if (parts.mat) { parts.mat.onBeforeCompile = function () { }; parts.mat.needsUpdate = true; }
            if (auroraShell) {
                auroraShell.parent.remove(auroraShell);
                auroraShell.material.dispose();
                auroraTex.dispose();
                auroraShell = null;
            }
            showAurora(lastAurora);
        };
    }

    var nightReady = false;
    globe.onGlobeReady(function () {
        if (nightReady) return;
        nightReady = true;
        var parts = findGlobeParts();
        try {
            addCityLights(parts, el.getAttribute('data-night-texture'));
            addAuroraShell(parts);
        } catch (e) {
            console.warn('Globe night layers unavailable; using the plain globe.', e);
            auroraShell = null;
        }
        showAurora(lastAurora);
    });

    // The Stations chip and the satellite point cloud (set up further down) both feed updateMarkers.
    var stationsOn = true, inView = true;
    var cloud = { worker: null, loaded: false, busy: false, label: null, eased: false };

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
            var p = stationsOn ? sky.position(m.id, now) : null;
            // Hidden (or not loaded yet): snap rather than tween when it comes back.
            if (!p) { m.visible = false; return; }
            // Keep longitude continuous so the 1-second tween never spins the long way around.
            m.lng = m.visible ? sky.unwrapNear(p.lon, m.lng) : p.lon;
            m.lat = p.lat;
            m.alt = p.alt / EARTH_RADIUS_KM;
            m.visible = true;
            m.node.classList.toggle('is-selected', m.id === sky.state.selected);
            shown.push(m);
        });
        if (you.loc) shown.push(you.pin);
        Array.prototype.push.apply(shown, pads.list);
        // globe.gl sets each element's z-index by distance, so the label could cover the open card,
        // which says the same thing anyway.
        if (stationsOn && you.passPath && card.hidden) shown.push(you.label);
        if (cloud.label) shown.push(cloud.label);
        globe.htmlElementsData(shown);
    }

    // ---- Orbits: the selected satellite's next orbit bright plus its last half-orbit faint;
    //      every other satellite gets only its next orbit, thin and dim ----
    globe
        .pathsData([])
        .pathPoints('pts')
        .pathPointLat(function (p) { return p.lat; })
        .pathPointLng(function (p) { return p.lon; })
        .pathPointAlt(function (p) { return p.alt / EARTH_RADIUS_KM; })
        .pathColor('color')
        .pathStroke('stroke')
        .pathTransitionDuration(0);

    // In this globe.gl version pathStroke is the line width in screen pixels, not degrees
    // (the fat lines are drawn with worldUnits off), so these are pixel widths.
    var STROKE_SELECTED = 2.5, STROKE_OTHER = 1.2, STROKE_PAST = 1.2;

    // sRGB 0..255 -> linear 0..255. globe.gl hands gradient colors to the GPU as-is, and the renderer
    // then encodes its output as sRGB, so without this every mid-tone is gamma-encoded twice and the
    // orange/cyan orbits come out washed-out, almost white. Pure 0 and 255 are unchanged either way.
    function toLinear(v) {
        var c = v / 255;
        c = c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
        return Math.round(c * 255);
    }

    // Fades are done by blending toward the page background (in sRGB, as the eye sees it), then
    // converting to linear for the GPU. Gradient path colors can't carry alpha in globe.gl
    // (fat lines drop it), so fading to the background is how a line ends without a hard edge.
    function shade(hex, t) {
        var n = parseInt(hex.slice(1), 16), bg = [11, 16, 32];
        var c = [n >> 16 & 255, n >> 8 & 255, n & 255].map(function (v, i) { return toLinear(bg[i] + (v - bg[i]) * t); });
        return 'rgb(' + c.join(',') + ')';
    }

    var orbitPaths = [];

    function updateOrbits() {
        var paths = orbitPaths = [];
        sky.SATS.forEach(function (s) {
            if (!sky.hasSat(s.id)) return;
            var period = sky.periodMinutes(s.id);
            if (s.id === sky.state.selected) {
                // Next full orbit: bright at the satellite, fading all the way into the background so
                // the far end (where Earth has turned ~23° under it) doesn't look like a line stopping mid-continent.
                paths.push({
                    pts: sky.track(s.id, 0, period, 20),
                    color: [shade(s.color, 1), shade(s.color, 0)],
                    stroke: STROKE_SELECTED
                });
                // Last half orbit: invisible at its oldest point, brightening as it reaches the satellite.
                paths.push({
                    pts: sky.track(s.id, -period / 2, 0, 20),
                    color: [shade(s.color, 0), shade(s.color, 0.5)],
                    stroke: STROKE_PAST
                });
            } else {
                // Other satellites: next orbit only, dim but still in their own color. No past track,
                // so at most 4 lines are on the globe and the bright one always belongs to the selection.
                paths.push({
                    pts: sky.track(s.id, 0, period, 20),
                    color: [shade(s.color, 0.6), shade(s.color, 0)],
                    stroke: STROKE_OTHER
                });
            }
        });
        renderPaths();
    }

    // The orbit objects are kept between calls, so the once-a-second footprint update doesn't recompute them.
    function renderPaths() {
        // The orbits, your ISS pass and the footprint all belong to the stations, so the Stations chip hides them too.
        if (!stationsOn) { globe.pathsData([]); return; }
        var paths = orbitPaths.slice();
        if (you.passPath) paths.push(you.passPath);
        if (you.footprint) paths.push(you.footprint);
        globe.pathsData(paths);
    }

    // ---- Your location (saved in "Your sky"): a pin, your next ISS pass, and the selected satellite's footprint ----
    var yourSky = window.TISYourSky;
    var PASS_COLOR = '#fff3c4', MIN_ELEVATION = 10;
    var you = { loc: null, pass: null, passPath: null, footprint: null };

    you.pin = { node: document.createElement('div'), lat: 0, lng: 0, alt: 0.002 };
    you.pin.node.className = 'globe-you';
    you.pin.node.innerHTML =
        '<button type="button" class="globe-you__pin" aria-expanded="false"><span class="globe-you__ring"></span><span class="globe-you__dot"></span></button>' +
        '<div class="globe-you__card" hidden><strong class="globe-you__title"></strong><span class="globe-you__pass"></span>' +
        '<span class="globe-you__now" hidden></span><a href="#your-sky">See your sky</a></div>';
    var pinButton = you.pin.node.querySelector('button');
    var card = you.pin.node.querySelector('.globe-you__card');
    var cardPass = card.querySelector('.globe-you__pass'), cardNow = card.querySelector('.globe-you__now');
    pinButton.addEventListener('click', function () {
        card.hidden = !card.hidden;
        pinButton.setAttribute('aria-expanded', String(!card.hidden));
        updateMarkers();
    });

    you.label = { node: document.createElement('div'), lat: 0, lng: 0, alt: 0 };
    // globe.gl centers each element on its point; a zero-size box with the text hanging off it puts the
    // text beside the point instead of on top of the arc.
    you.label.node.className = 'globe-pass-label';
    you.label.node.appendChild(document.createElement('span'));

    // "Your next ISS pass · Tue 6:43 AM", with the time in its own span so it can drop to a second line on phones.
    function setPassLabel(node, pass) {
        node.textContent = 'Your next ISS pass';
        var when = document.createElement('span');
        when.className = 'when';
        when.textContent = pass.when;
        node.appendChild(when);
    }

    function setLocation(loc) {
        you.loc = loc;
        you.pass = null;
        you.passPath = null;
        card.hidden = true;
        pinButton.setAttribute('aria-expanded', 'false');
        if (loc) {
            you.pin.lat = loc.lat;
            you.pin.lng = loc.lon;
            var title = /^\d{5}$/.test(loc.label) ? 'Your location (' + loc.label + ')' : 'Your location';
            card.querySelector('.globe-you__title').textContent = title;
            pinButton.setAttribute('aria-label', title);
            cardPass.textContent = '';
            updatePass();
        }
        updateFootprint();
        updateMarkers();
        renderPaths();
    }

    // Looked up again every minute (from the data "Your sky" already has), so it moves on once a pass is over.
    function updatePass() {
        if (!you.loc) return;
        var loc = you.loc;
        yourSky.nextPass('iss').then(function (pass) {
            if (you.loc !== loc) return;
            you.pass = pass;
            cardPass.textContent = pass ? 'Your next ISS pass · ' + pass.when : 'No visible ISS passes in the next few days';
            drawPass();
        }).catch(function () {
            cardPass.textContent = 'ISS passes aren’t available right now';
        });
    }

    // The ISS's track during the visible part of the pass, every 10 s, from the TLE the globe already has.
    function drawPass() {
        var pass = you.pass, now = Date.now();
        var pts = pass && sky.hasSat('iss')
            ? sky.track('iss', (pass.start - now) / 60000, (pass.end - now) / 60000, 10, you.loc.lon) : [];
        you.passPath = pts.length > 1 ? {
            pts: pts,
            color: [shade(PASS_COLOR, 0.6), shade(PASS_COLOR, 1), shade(PASS_COLOR, 0.6)],
            stroke: 3.5
        } : null;
        if (you.passPath) {
            // At the end of the pass. Both ends are about as far from the pin (each is 10° above your horizon),
            // and the middle is the closest point to it, where a label would cover the pin.
            var end = pts[pts.length - 1];
            you.label.lat = end.lat;
            you.label.lng = end.lon;
            you.label.alt = end.alt / EARTH_RADIUS_KM;
            setPassLabel(you.label.node.firstChild, pass);
        }
        updateMarkers();
        renderPaths();
    }

    // A thin ring on the ground around the selected satellite. Only drawn when there's a pin to compare it with.
    function updateFootprint() {
        var p = you.loc ? sky.position(sky.state.selected, new Date()) : null;
        if (!p) {
            you.footprint = null;
            setInside(null);
            return;
        }
        var radius = sky.footprintRadius(p.alt, MIN_ELEVATION);
        var sat = sky.SATS.filter(function (s) { return s.id === sky.state.selected; })[0];
        you.footprint = {
            // A few km up so the ring doesn't flicker into the globe's surface.
            pts: sky.circle(p.lat, p.lon, radius, 90).map(function (q) { return { lat: q.lat, lon: q.lon, alt: 8 }; }),
            color: shade(sat.color, 0.55),
            stroke: 1.2
        };
        setInside(sky.angleBetween(p.lat, p.lon, you.loc.lat, you.loc.lon) <= radius ? sat : null);
    }

    // sat is the satellite whose footprint covers the pin, or null.
    function setInside(sat) {
        you.pin.node.classList.toggle('is-inside', !!sat);
        cardNow.hidden = !sat;
        if (sat) cardNow.textContent = (sat.id === 'iss' ? 'The ISS' : sat.name) + ' is above your horizon now';
    }

    // ---- Launch pads: a small rocket at each upcoming launch's pad, the next one labeled ----
    var HOUR = 3600000;
    var pads = { upcoming: [], list: [], key: '', next: null };
    var padsOn = true;
    var padsChip = document.getElementById('pads-chip');

    // "Falcon 9 Block 5 | Starlink Group 15-25" -> "Falcon 9 · Starlink 15-25", short enough to sit on the globe.
    function shortLaunchName(name) {
        var parts = String(name).split(' | ');
        var rocket = parts[0].replace(/\s+Block\s+\d+\b/i, '');
        var mission = (parts[1] || '').replace(/\bGroup\s+/i, '');
        return mission ? rocket + ' · ' + mission : rocket;
    }

    function hasPad(l) {
        return typeof l.padLat === 'number' && typeof l.padLon === 'number' && isFinite(l.padLat) && isFinite(l.padLon);
    }

    function text(tag, className, value) {
        var n = document.createElement(tag);
        n.className = className;
        n.textContent = value;
        return n;
    }

    // Rebuilt only when the launches still to come change (one flies, or the data loads), so an open card
    // isn't closed under the visitor's finger every minute.
    function updatePads() {
        // Turned off: nothing to build or keep up to date until the chip turns them back on.
        if (!padsOn) return;
        var now = Date.now();
        // Same rule as the Launches card: a launch stays for an hour after T-0, then the next one takes over.
        var coming = pads.upcoming.filter(function (l) { return Date.parse(l.net) > now - HOUR; });
        var next = sky.pickNextLaunch(pads.upcoming, now);
        var key = coming.map(function (l) { return l.name + '@' + l.net; }).join('|');
        if (key === pads.key) return;
        pads.key = key;
        pads.next = next;

        // Several launches can share a pad, so they're grouped by position.
        var byPad = {};
        pads.list = [];
        el.classList.remove('has-pad-card');
        coming.filter(hasPad).forEach(function (l) {
            var id = l.padLat.toFixed(3) + ',' + l.padLon.toFixed(3);
            if (!byPad[id]) {
                byPad[id] = { lat: l.padLat, lng: l.padLon, alt: 0.002, launches: [] };
                pads.list.push(byPad[id]);
            }
            byPad[id].launches.push(l);
        });
        pads.list.forEach(buildPad);
        chipCount(padsChip, ' · ' + pads.list.length);
        updatePadLabel();
        updateMarkers();
    }

    function buildPad(pad) {
        var first = pad.launches[0];
        pad.isNext = pad.launches.indexOf(pads.next) >= 0;
        pad.node = document.createElement('div');
        pad.node.className = 'globe-pad' + (pad.isNext ? ' is-next' : '');

        var pin = document.createElement('button');
        pin.type = 'button';
        pin.className = 'globe-pad__pin';
        pin.setAttribute('aria-expanded', 'false');
        pin.setAttribute('aria-label', 'Launches from ' + (first.padName || first.location || 'this pad'));
        pin.innerHTML = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1c2 1.6 3 4 3 6.6V11l1.6 1.6V14L10 13H6l-2.6 1v-1.4L5 11V7.6C5 5 6 2.6 8 1z"/></svg>';
        pad.node.appendChild(pin);

        if (pad.isNext) {
            pad.label = text('span', 'globe-pad__label', '');
            pad.node.appendChild(pad.label);
        }

        var card = document.createElement('div');
        card.className = 'globe-pad__card';
        card.hidden = true;
        card.appendChild(text('strong', '', first.padName || first.location || 'Launch pad'));
        if (first.padName && first.location) card.appendChild(text('span', 'globe-pad__where', first.location));
        pad.launches.forEach(function (l) {
            card.appendChild(text('span', 'globe-pad__name', l.name));
            card.appendChild(text('span', 'globe-pad__when',
                [l.provider, sky.launchWhen(l), sky.launchStatus(l)].filter(Boolean).join(' · ')));
        });
        var link = text('a', '', 'See Launches');
        link.href = '#launches';
        card.appendChild(link);
        pad.node.appendChild(card);

        pin.addEventListener('click', function () {
            var open = card.hidden;
            // One card at a time, so they don't pile up on top of each other.
            pads.list.forEach(function (p) { setPadOpen(p, false); });
            setPadOpen(pad, open);
        });
    }

    function setPadOpen(pad, open) {
        var card = pad.node.querySelector('.globe-pad__card');
        card.hidden = !open;
        pad.node.querySelector('button').setAttribute('aria-expanded', String(open));
        pad.node.classList.toggle('is-open', open);
        // The next launch's label can sit on top of another pad's card, and the card says more anyway.
        el.classList.toggle('has-pad-card', pads.list.some(function (p) { return p.node.classList.contains('is-open'); }));
        if (open) {
            flipIfCut(pad.node, card, 'is-card-left', 'right');
            flipIfCut(pad.node, card, 'is-card-up', 'bottom');
        }
    }

    // Labels and cards hang right of and below the rocket. Near the globe box's right or bottom edge
    // (on a phone, much of the globe) they would be cut off, so they hang the other way instead.
    function flipIfCut(node, box, className, edge) {
        node.classList.remove(className);
        var limit = el.getBoundingClientRect()[edge];
        node.classList.toggle(className, box.getBoundingClientRect()[edge] > limit - 8);
    }

    // "Falcon 9 · Starlink 15-25 · T−22h 58m", or the date when there's no countdown to show.
    function updatePadLabel() {
        var pad = pads.list.filter(function (p) { return p.isNext; })[0];
        if (!pad) return;
        var when = sky.launchCountdown(pads.next, Date.now()) || sky.launchWhen(pads.next);
        // Only touch the text when the minute changes, not on every one-second tick.
        if (pad.when !== when) {
            pad.when = when;
            pad.label.textContent = shortLaunchName(pads.next.name);
            pad.label.appendChild(text('span', 'when', when));
        }
        // The globe turns under the label, so this is checked every tick.
        if (!pad.node.classList.contains('is-open')) flipIfCut(pad.node, pad.label, 'is-label-left', 'right');
    }

    sky.launchesReady.then(function (upcoming) {
        pads.upcoming = upcoming;
        if (padsChip) padsChip.hidden = !upcoming.some(hasPad);
        updatePads();
    }).catch(function () { /* no launch data: no pads */ });

    if (yourSky) yourSky.on(setLocation);

    // ---- Aurora fallback: a band of points, used when the shell above can't be built ----
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
        if (data) lastAurora = data;
        var points = lastAurora && lastAurora.points ? lastAurora.points : [];
        // Until the globe is ready we don't know yet whether the shell works, so show nothing rather than flash points.
        if (!nightReady) return;
        if (auroraShell) {
            paintAurora(points);
            globe.pointsData([]);
        } else {
            globe.pointsData(points);
        }
    }

    // ---- Every active satellite: a calm point cloud with group toggles ----
    // ~16,000 dots, no labels or orbit lines, one draw call per group (globe.gl's particles layer). The TLEs are
    // only downloaded when a visitor turns on a group, and sat-worker.js propagates them off the main thread.
    // Mild transparency so the dense Starlink shell shimmers instead of looking like a solid sheet.
    var GROUPS = [
        { id: 'starlink', color: 'rgba(225, 235, 255, 0.7)' },
        { id: 'navigation', color: 'rgba(255, 190, 90, 0.9)', shellKm: 20200 },
        { id: 'geo', color: 'rgba(95, 225, 240, 0.9)', shellKm: 35786 },
        { id: 'other', color: 'rgba(175, 180, 195, 0.55)' }
    ];
    var groupChips = document.querySelectorAll('.sat-groups [data-group]');
    var on = { stations: true, pads: true };

    // Points are drawn in device pixels, so scale up on high-DPI screens to stay ~1.5 CSS px.
    var POINT_SIZE = 1.5 * Math.min(window.devicePixelRatio || 1, 2);
    globe
        .particlesData(GROUPS)
        .particlesList(function () { return []; })
        .particlesSize(function () { return POINT_SIZE; })
        .particlesSizeAttenuation(false)
        .particlesColor('color');

    // globe.gl builds one three.js Points object per group; their position buffers are then filled directly
    // from the worker's Float32Arrays, which skips globe.gl's per-point objects entirely.
    function pointsOf(g) {
        if (g.points) return g.points;
        globe.scene().traverse(function (o) {
            if (o.__globeObjType === 'particles' && o.__data === g) g.points = o;
        });
        return g.points || null;
    }

    function showGroups() {
        GROUPS.forEach(function (g) {
            var pts = pointsOf(g);
            if (pts) pts.visible = cloud.loaded && !!on[g.id] && !!g.attr;
        });
    }

    function enabledGroups() {
        return GROUPS.filter(function (g) { return on[g.id]; }).map(function (g) { return g.id; });
    }

    function groupById(id) {
        return GROUPS.filter(function (g) { return g.id === id; })[0];
    }

    // Phones and small CPUs tick less often; a dot moves ~15 km per 2 s, under a pixel at most zooms.
    var TICK_MS = (navigator.hardwareConcurrency && navigator.hardwareConcurrency <= 4) ||
        window.matchMedia('(max-width: 640px)').matches ? 5000 : 2000;

    // Skipped while the globe isn't drawing (map view, scrolled away, tab hidden), so it costs nothing then.
    function cloudTick() {
        if (!cloud.loaded || cloud.busy || document.hidden || sky.state.view !== 'globe' || !inView) return;
        var ids = enabledGroups();
        if (!ids.length) return;
        cloud.busy = true;
        cloud.worker.postMessage({ type: 'tick', time: Date.now(), groups: ids });
    }
    setInterval(cloudTick, TICK_MS);
    document.addEventListener('visibilitychange', cloudTick);

    function onPositions(m) {
        cloud.busy = false;
        GROUPS.forEach(function (g) {
            var xyz = m.groups[g.id], pts = pointsOf(g);
            if (!xyz || !pts) return;
            if (!g.attr) {
                // globe.gl doesn't expose three.js, so borrow the attribute class from the empty one it made.
                var Attr = pts.geometry.getAttribute('position').constructor;
                g.attr = new Attr(new Float32Array(xyz.length), 3);
                pts.geometry.setAttribute('position', g.attr);
                // The cloud surrounds the camera's target, so never cull it as a whole.
                pts.frustumCulled = false;
            }
            g.attr.array.set(xyz);
            g.attr.needsUpdate = true;
            pts.geometry.computeBoundingSphere();
        });
        showGroups();
        moveLabel();
    }

    function setChip(chip, pressed) {
        chip.setAttribute('aria-pressed', pressed ? 'true' : 'false');
    }

    function chipCount(chip, text) {
        var n = chip.querySelector('.n');
        if (n) n.textContent = text;
    }

    function loadCloud() {
        if (cloud.worker || !window.Worker) return;
        var section = document.getElementById('live-sky');
        groupChips.forEach(function (c) { chipCount(c, ' · loading…'); });
        try {
            cloud.worker = new Worker(section.getAttribute('data-sat-worker'));
        } catch (e) {
            cloudFailed();
            return;
        }
        cloud.worker.onmessage = function (e) {
            var m = e.data;
            if (m.type === 'loaded') {
                cloud.loaded = true;
                GROUPS.forEach(function (g) { g.names = m.groups[g.id] || []; });
                groupChips.forEach(function (c) {
                    var g = groupById(c.dataset.group);
                    if (g) chipCount(c, ' ' + g.names.length.toLocaleString());
                });
                cloudTick();
            } else if (m.type === 'positions') {
                onPositions(m);
            } else if (m.type === 'error') {
                cloudFailed();
            }
        };
        cloud.worker.onerror = cloudFailed;
        cloud.worker.postMessage({
            type: 'init',
            url: new URL('/api/sky/active-satellites', location.href).href,
            lib: new URL(section.getAttribute('data-satellite-lib'), location.href).href,
            globeRadius: globe.getGlobeRadius()
        });
    }

    // No data (not written yet, or offline): say so on the chips. A later toggle tries again.
    function cloudFailed() {
        if (cloud.worker) cloud.worker.terminate();
        cloud.worker = null;
        cloud.loaded = false;
        cloud.busy = false;
        groupChips.forEach(function (c) {
            if (!groupById(c.dataset.group)) return;
            on[c.dataset.group] = false;
            setChip(c, false);
            chipCount(c, ' · unavailable');
        });
        showGroups();
    }

    // How far out the camera must be to see a whole shell (~4.2 Earth radii for navigation, ~6.6 for the ring).
    // Also lets the visitor zoom out that far, and keeps it inside the camera's far plane.
    function altitudeToSee(g) {
        var camera = globe.camera(), R = globe.getGlobeRadius();
        var shell = 1 + g.shellKm / EARTH_RADIUS_KM;
        var halfV = camera.fov * Math.PI / 360, halfH = Math.atan(Math.tan(halfV) * camera.aspect);
        var distance = shell * 1.15 / Math.tan(Math.min(halfV, halfH)) * R;
        controls.maxDistance = Math.max(controls.maxDistance, distance * 1.3);
        if (camera.far < distance * 2) { camera.far = distance * 2; camera.updateProjectionMatrix(); }
        return distance / R - 1;
    }

    function setGroup(id, value, byVisitor) {
        on[id] = value;
        groupChips.forEach(function (c) { if (c.dataset.group === id) setChip(c, value); });
        if (id === 'stations') {
            stationsOn = value;
            updateMarkers();
            renderPaths();
        } else if (value) {
            loadCloud();
            var g = groupById(id);
            if (g.shellKm) {
                var altitude = altitudeToSee(g);
                // Only the first time, so it doesn't fight a visitor who has since zoomed back in.
                if (byVisitor && !cloud.eased && globe.pointOfView().altitude < altitude) {
                    cloud.eased = true;
                    controls.autoRotate = false;
                    if (sky.state.following) sky.setFollowing(false);
                    globe.pointOfView({ altitude: altitude }, reduceMotion ? 0 : 1500);
                }
            }
            cloudTick();
        }
        if (cloud.label && !on[cloud.label.group]) hideLabel();
        showGroups();
    }

    var STORE_KEY = 'tis.globe.groups';
    function saveGroups() {
        try { localStorage.setItem(STORE_KEY, JSON.stringify(on)); } catch (e) { /* private mode: just don't remember */ }
    }

    groupChips.forEach(function (c) {
        c.disabled = false;
        c.addEventListener('click', function () {
            var id = c.dataset.group;
            setGroup(id, !on[id], true);
            saveGroups();
        });
    });

    // Off removes the pads, the next launch's label and any open card. On rebuilds them from the
    // launches already loaded.
    function setPads(value) {
        padsOn = on.pads = value;
        setChip(padsChip, value);
        pads.list.forEach(function (p) { setPadOpen(p, false); });
        el.classList.remove('has-pad-card');
        pads.list = [];
        pads.key = '';
        chipCount(padsChip, '');
        if (value) updatePads();
        updateMarkers();
    }

    if (padsChip) {
        padsChip.addEventListener('click', function () {
            setPads(!padsOn);
            saveGroups();
        });
    }

    // ---- Tap a dot: a small label with its name (the nearest visible dot within a few pixels) ----
    // How close a tap must be to a dot, in CSS pixels. Fingers get more room than a mouse.
    var PICK_PX = 10, PICK_PX_TOUCH = 16;
    var Vec3 = globe.camera().position.constructor;
    var pickV = new Vec3(), camV = new Vec3();

    function hideLabel() {
        cloud.label = null;
        updateMarkers();
    }

    function moveLabel() {
        var l = cloud.label;
        if (!l) return;
        var a = groupById(l.group).attr.array, i = l.index * 3;
        var geo = globe.toGeoCoords({ x: a[i], y: a[i + 1], z: a[i + 2] });
        l.lng = l.lng === null ? geo.lng : sky.unwrapNear(geo.lng, l.lng);
        l.lat = geo.lat;
        l.alt = geo.altitude;
    }

    function pick(clientX, clientY, radius) {
        var camera = globe.camera(), rect = el.getBoundingClientRect(), R = globe.getGlobeRadius();
        camV.setFromMatrixPosition(camera.matrixWorld);
        var best = null, bestD = radius * radius;
        GROUPS.forEach(function (g) {
            var pts = pointsOf(g);
            if (!pts || !pts.visible || !g.attr) return;
            var a = g.attr.array;
            for (var i = 0; i < a.length; i += 3) {
                if (a[i] === 0 && a[i + 1] === 0 && a[i + 2] === 0) continue;
                pickV.set(a[i], a[i + 1], a[i + 2]).applyMatrix4(pts.matrixWorld);
                // Behind the Earth: the line from the camera to the dot passes through the globe.
                var dx = pickV.x - camV.x, dy = pickV.y - camV.y, dz = pickV.z - camV.z;
                var t = -(camV.x * dx + camV.y * dy + camV.z * dz) / (dx * dx + dy * dy + dz * dz);
                if (t > 0 && t < 1) {
                    var cx = camV.x + t * dx, cy = camV.y + t * dy, cz = camV.z + t * dz;
                    if (cx * cx + cy * cy + cz * cz < R * R) continue;
                }
                pickV.project(camera);
                if (pickV.z > 1) continue;
                var sx = (pickV.x + 1) / 2 * rect.width + rect.left, sy = (1 - pickV.y) / 2 * rect.height + rect.top;
                var d = (sx - clientX) * (sx - clientX) + (sy - clientY) * (sy - clientY);
                if (d < bestD) { bestD = d; best = { group: g.id, index: i / 3 }; }
            }
        });
        return best;
    }

    var down = null;
    el.addEventListener('pointerdown', function (e) {
        down = e.target.tagName === 'CANVAS' ? { x: e.clientX, y: e.clientY, t: Date.now() } : null;
    });
    el.addEventListener('pointerup', function (e) {
        // A tap, not the end of a drag.
        var tap = down && Math.abs(e.clientX - down.x) <= 5 && Math.abs(e.clientY - down.y) <= 5 && Date.now() - down.t < 600;
        down = null;
        if (!tap || !cloud.loaded) return;
        var hit = pick(e.clientX, e.clientY, e.pointerType === 'touch' ? PICK_PX_TOUCH : PICK_PX);
        if (!hit) { if (cloud.label) hideLabel(); return; }
        var node = document.createElement('div');
        node.className = 'globe-dot-label';
        node.appendChild(document.createElement('span')).textContent = groupById(hit.group).names[hit.index];
        cloud.label = { node: node, group: hit.group, index: hit.index, lat: 0, lng: null, alt: 0 };
        moveLabel();
        updateMarkers();
    });

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
            setLocation(yourSky ? yourSky.location() : null);
            updateOrbits();
            centerOnSelected();
        } else if (type === 'tick') {
            if (sky.state.view !== 'globe') return;
            updatePadLabel();
            updateMarkers();
            if (you.loc) { updateFootprint(); renderPaths(); }
            if (sky.state.following) lookAtSelected(1000);
        } else if (type === 'minute') {
            updateSun();
            updateOrbits();
            updatePass();
            updatePads();
        } else if (type === 'select') {
            updateFootprint();
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

    // The globe loads lazily, so the visitor may already be on the map. Don't render
    // in the background; the 'view' event resizes and resumes it when they switch back.
    if (sky.state.view !== 'globe') globe.pauseAnimation();

    // Groups the visitor turned on last time (after the camera controls exist, since the shells widen the zoom).
    var savedGroups = null;
    try { savedGroups = JSON.parse(localStorage.getItem(STORE_KEY) || 'null'); } catch (e) { savedGroups = null; }
    if (savedGroups && typeof savedGroups === 'object') {
        groupChips.forEach(function (c) {
            var id = c.dataset.group;
            if (typeof savedGroups[id] === 'boolean' && savedGroups[id] !== !!on[id]) setGroup(id, savedGroups[id], false);
        });
        // Saved before the pads had a chip: no key, so they stay on.
        if (padsChip && savedGroups.pads === false) setPads(false);
    }

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
                inView = e.isIntersecting;
                if (e.isIntersecting) globe.resumeAnimation(); else globe.pauseAnimation();
            });
        }).observe(el);
    }
})();
