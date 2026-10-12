// 3D globe view for Live Sky (globe.gl / three.js): drag to rotate, scroll or pinch to zoom,
// click a satellite to select it. Shows ISS, Hubble and Tiangong at true relative altitude with their orbits,
// the aurora forecast as a glowing shell, city lights on the night side, and sunlight from the Sun's real direction.
// With a location saved in "Your sky", also a pin there, your next visible ISS pass drawn in space, and the
// selected satellite's footprint: where on Earth it's at least 10° above the horizon right now.
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
        if (you.loc) shown.push(you.pin);
        // globe.gl sets each element's z-index by distance, so the label could cover the open card,
        // which says the same thing anyway.
        if (you.passPath && card.hidden) shown.push(you.label);
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
            updateMarkers();
            if (you.loc) { updateFootprint(); renderPaths(); }
            if (sky.state.following) lookAtSelected(1000);
        } else if (type === 'minute') {
            updateSun();
            updateOrbits();
            updatePass();
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
