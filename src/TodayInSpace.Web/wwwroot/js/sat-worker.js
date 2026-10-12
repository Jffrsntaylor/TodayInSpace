// Web Worker for the globe's point cloud: downloads every active satellite's TLE (~16,000), and on each tick
// propagates the groups that are switched on, so the page's main thread never parses or runs SGP4 for them.
// Positions come back as one Float32Array of x/y/z per group, already in globe.gl's scene coordinates,
// so the page can copy them straight into the GPU buffer.
'use strict';

var EARTH_RADIUS_KM = 6371;
var globeRadius = 100;
var groups = {};   // id -> { names: [], tles: [[l1, l2]], recs: null }

self.onmessage = function (e) {
    var m = e.data;
    if (m.type === 'init') init(m);
    else if (m.type === 'tick') tick(m);
};

function init(m) {
    globeRadius = m.globeRadius;
    try {
        importScripts(m.lib);
    } catch (err) {
        self.postMessage({ type: 'error' });
        return;
    }

    fetch(m.url).then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
    }).then(function (data) {
        (data.satellites || []).forEach(function (s) {
            var g = groups[s.g] || (groups[s.g] = { names: [], tles: [], recs: null });
            g.names.push(s.n);
            g.tles.push([s.l1, s.l2]);
        });
        var summary = {};
        Object.keys(groups).forEach(function (id) { summary[id] = groups[id].names; });
        self.postMessage({ type: 'loaded', updated: data.updated, groups: summary });
    }).catch(function () {
        self.postMessage({ type: 'error' });
    });
}

// Satellite records are built the first time a group is switched on, so turning on only Geostationary
// doesn't pay for 10,000 Starlinks.
function records(g) {
    if (!g.recs) {
        g.recs = g.tles.map(function (t) {
            try { return satellite.twoline2satrec(t[0], t[1]); } catch (err) { return null; }
        });
        g.tles = null;
    }
    return g.recs;
}

function tick(m) {
    var date = new Date(m.time);
    var gmst = satellite.gstime(date);
    var k = globeRadius / EARTH_RADIUS_KM;
    var out = {}, transfer = [];

    m.groups.forEach(function (id) {
        var g = groups[id];
        if (!g) return;
        var recs = records(g);
        var xyz = new Float32Array(recs.length * 3);
        for (var i = 0; i < recs.length; i++) {
            if (!recs[i]) continue;
            var pv = satellite.propagate(recs[i], date);
            // A decayed or broken orbit stays at the centre of the Earth, where the globe hides it.
            if (!pv || !pv.position) continue;
            var p = satellite.eciToEcf(pv.position, gmst);
            // Earth-fixed km -> globe.gl's axes: y is north, z points at 0° longitude, x at 90° E.
            xyz[i * 3] = p.y * k;
            xyz[i * 3 + 1] = p.z * k;
            xyz[i * 3 + 2] = p.x * k;
        }
        out[id] = xyz;
        transfer.push(xyz.buffer);
    });

    self.postMessage({ type: 'positions', time: m.time, groups: out }, transfer);
}
