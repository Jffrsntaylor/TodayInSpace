// Twinkling starfield with the occasional shooting star, drawn behind the page.
// Static (no animation) for visitors who prefer reduced motion; pauses when the tab is hidden.
(function () {
    'use strict';

    var c = document.getElementById('starfield');
    if (!c || !c.getContext) return;
    var ctx = c.getContext('2d');
    var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    var COLORS = ['#ffffff', '#ffffff', '#ffffff', '#cfd8ff', '#ffe9c7', '#d9c9ff'];
    var stars = [], w = 0, h = 0, raf = null, shooting = null;

    function resize() {
        var dpr = Math.min(window.devicePixelRatio || 1, 2);
        w = window.innerWidth;
        h = window.innerHeight;
        c.width = Math.round(w * dpr);
        c.height = Math.round(h * dpr);
        c.style.width = w + 'px';
        c.style.height = h + 'px';
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        var count = Math.round((w * h) / 5500);
        stars = [];
        for (var i = 0; i < count; i++) {
            stars.push({
                x: Math.random() * w,
                y: Math.random() * h,
                r: Math.random() < 0.08 ? Math.random() * 1.1 + 1 : Math.random() * 0.9 + 0.2,
                a: Math.random() * 0.55 + 0.25,
                speed: Math.random() * 1.6 + 0.4,
                phase: Math.random() * Math.PI * 2,
                color: COLORS[(Math.random() * COLORS.length) | 0]
            });
        }
        if (reduce) draw(0);
    }

    function draw(t) {
        ctx.clearRect(0, 0, w, h);
        for (var i = 0; i < stars.length; i++) {
            var s = stars[i];
            var twinkle = reduce ? 1 : 0.65 + 0.35 * Math.sin(t / 1000 * s.speed + s.phase);
            ctx.globalAlpha = s.a * twinkle;
            ctx.fillStyle = s.color;
            ctx.beginPath();
            ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2);
            ctx.fill();
        }

        if (!reduce) {
            if (!shooting && Math.random() < 0.0025) {
                shooting = {
                    x: w * (0.35 + Math.random() * 0.65),
                    y: h * Math.random() * 0.35,
                    vx: -(7 + Math.random() * 5),
                    vy: 2.5 + Math.random() * 2.5,
                    life: 1
                };
            }
            if (shooting) {
                var tailX = shooting.x - shooting.vx * 12, tailY = shooting.y - shooting.vy * 12;
                var grad = ctx.createLinearGradient(shooting.x, shooting.y, tailX, tailY);
                grad.addColorStop(0, 'rgba(255,255,255,' + (0.9 * shooting.life) + ')');
                grad.addColorStop(1, 'rgba(255,255,255,0)');
                ctx.globalAlpha = 1;
                ctx.strokeStyle = grad;
                ctx.lineWidth = 1.6;
                ctx.beginPath();
                ctx.moveTo(shooting.x, shooting.y);
                ctx.lineTo(tailX, tailY);
                ctx.stroke();
                shooting.x += shooting.vx;
                shooting.y += shooting.vy;
                shooting.life -= 0.018;
                if (shooting.life <= 0 || shooting.x < -100 || shooting.y > h + 100) shooting = null;
            }
        }
        ctx.globalAlpha = 1;
    }

    function loop(t) {
        draw(t);
        raf = window.requestAnimationFrame(loop);
    }

    function start() { if (!reduce && raf === null) raf = window.requestAnimationFrame(loop); }
    function stop() { if (raf !== null) { window.cancelAnimationFrame(raf); raf = null; } }

    var resizeTimer = null;
    window.addEventListener('resize', function () {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(resize, 150);
    });
    document.addEventListener('visibilitychange', function () {
        if (document.hidden) stop(); else start();
    });

    resize();
    start();
})();
