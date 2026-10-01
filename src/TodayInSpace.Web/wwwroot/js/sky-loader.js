// Loads the heavy Live Sky libraries only when they're needed, so the rest of the homepage
// (above all the picture of the day) isn't waiting on ~2 MB of 3D and map code.
//  - Globe: globe.gl, then sky-globe.js, once Live Sky is about to scroll into view
//  - Map:   Leaflet (css + js), then sky-map.js, the first time the Map view is shown
// File URLs come from data- attributes on .sky, rendered by Razor with cache-busting versions.
(function () {
    'use strict';

    var sky = window.TISSky;
    var section = document.querySelector('.sky');
    if (!sky || !section) return;

    var src = function (name) { return section.getAttribute('data-' + name); };

    // One promise per URL, so a file is requested at most once however often it's asked for.
    var loading = {};

    function loadScript(url) {
        if (!loading[url]) {
            loading[url] = new Promise(function (resolve, reject) {
                var s = document.createElement('script');
                s.src = url;
                s.onload = resolve;
                s.onerror = function () { reject(new Error('Failed to load ' + url)); };
                document.body.appendChild(s);
            });
        }
        return loading[url];
    }

    function loadCss(url) {
        if (!loading[url]) {
            loading[url] = new Promise(function (resolve, reject) {
                var l = document.createElement('link');
                l.rel = 'stylesheet';
                l.href = url;
                l.onload = resolve;
                l.onerror = function () { reject(new Error('Failed to load ' + url)); };
                document.head.appendChild(l);
            });
        }
        return loading[url];
    }

    var mapLoad = null;
    function loadMap() {
        if (!mapLoad) {
            // The stylesheet and Leaflet download in parallel; sky-map.js needs both.
            mapLoad = Promise.all([loadCss(src('leaflet-css')), loadScript(src('leaflet-js'))])
                .then(function () { return loadScript(src('map-src')); })
                .catch(function (e) {
                    console.error(e);
                    sky.setStatus('Couldn’t load the map right now.');
                });
        }
        return mapLoad;
    }

    var globeLoad = null;
    function loadGlobe() {
        if (!globeLoad) {
            globeLoad = loadScript(src('globe-lib'))
                .then(function () { return loadScript(src('globe-src')); })
                .catch(function (e) {
                    // Same fallback as no WebGL: hide the toggle and show the flat map instead.
                    console.error(e);
                    var box = document.getElementById('sky-globe');
                    if (box) box.textContent = '';
                    var toggle = document.querySelector('.view-toggle');
                    if (toggle) toggle.hidden = true;
                    sky.setView('map');
                    sky.setStatus('Couldn’t load the 3D globe, so here’s the flat map.');
                });
        }
        return globeLoad;
    }

    // Covers both the Map tab and sky-globe.js falling back to the map when there's no WebGL.
    sky.on(function (type, state) {
        if (type === 'view' && state.view === 'map') loadMap();
    });

    if ('IntersectionObserver' in window) {
        // Start ~600px early so the globe is usually ready by the time it's on screen.
        var io = new IntersectionObserver(function (entries) {
            if (entries.some(function (e) { return e.isIntersecting; })) {
                io.disconnect();
                loadGlobe();
            }
        }, { rootMargin: '600px 0px' });
        io.observe(section);
    } else {
        loadGlobe();
    }
})();
