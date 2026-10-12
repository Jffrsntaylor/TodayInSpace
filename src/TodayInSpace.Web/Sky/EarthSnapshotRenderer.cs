using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using SkiaSharp;
using TodayInSpace.Core.Snapshot;

namespace TodayInSpace.Web.Sky
{
    // Draws an EarthScene as a PNG: the day and night maps blended at the terminator, city lights and aurora
    // on the dark side, a thin atmosphere, then the ISS track and the visitor's pin on top.
    // The globe itself is done pixel by pixel (Skia has no sphere), and the overlays are drawn with Skia so
    // their lines and dots are antialiased. Thread-safe: the textures are read-only and each render has its own buffers.
    public sealed class EarthSnapshotRenderer
    {
        // The disc fills this much of the square, leaving room for the atmosphere's glow around it.
        private const double DiscFraction = 0.92;
        // How far the glow reaches beyond the edge of the disc, as a fraction of its radius.
        private const double HaloWidth = 0.07;
        // In the link-preview card, the globe is this tall (the card is 630).
        private const int CardGlobeSize = 580;

        // Colours from the site: the globe's atmosphere, the ISS's colour, and the pass highlight.
        private static readonly (float R, float G, float B) Atmosphere = (0.48f, 0.64f, 1f);       // #7aa2ff
        private static readonly SKColor IssColor = SKColor.Parse("#b7c0ff");
        private static readonly SKColor PinColor = SKColor.Parse("#ffd166");
        private static readonly SKColor PinRingColor = SKColor.Parse("#fff3c4");
        private static readonly SKColor CardBackground = SKColor.Parse("#04060d");

        private readonly EarthTexture _day;
        private readonly EarthTexture _night;

        public EarthSnapshotRenderer(EarthTexture day, EarthTexture night)
        {
            _day = day;
            _night = night;
        }

        // The same two maps the 3D globe uses.
        public static EarthSnapshotRenderer FromWebRoot(IFileProvider webRoot)
        {
            using var day = webRoot.GetFileInfo("img/earth-blue-marble.jpg").CreateReadStream();
            using var night = webRoot.GetFileInfo("img/earth-night-lights.jpg").CreateReadStream();
            return new EarthSnapshotRenderer(EarthTexture.Decode(day), EarthTexture.Decode(night));
        }

        public byte[] RenderPng(EarthScene scene, EarthSnapshotRequest request)
        {
            if (request.Format == SnapshotFormat.Square)
            {
                using var globe = RenderGlobe(scene, request.Size);
                return Encode(globe);
            }

            // Link-preview card: the globe centred on the site's dark background.
            var info = new SKImageInfo(EarthSnapshotRequest.CardWidth, EarthSnapshotRequest.CardHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var card = new SKBitmap(info);
            using (var canvas = new SKCanvas(card))
            using (var globe = RenderGlobe(scene, CardGlobeSize))
            using (var image = SKImage.FromBitmap(globe))
            {
                canvas.Clear(CardBackground);
                canvas.DrawImage(image, (info.Width - CardGlobeSize) / 2f, (info.Height - CardGlobeSize) / 2f, SKSamplingOptions.Default, null);
            }
            return Encode(card);
        }

        private SKBitmap RenderGlobe(EarthScene scene, int size)
        {
            var view = new OrthographicView(scene.Center.Lat, scene.Center.Lon);
            double radius = size / 2.0 * DiscFraction;
            double cx = size / 2.0, cy = size / 2.0;

            var pixels = new byte[size * size * 4];
            PaintGlobe(pixels, size, view, radius, cx, cy, scene);

            var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

            using var canvas = new SKCanvas(bitmap);
            DrawOverlays(canvas, view, radius, cx, cy, size, scene);
            return bitmap;
        }

        private void PaintGlobe(byte[] pixels, int size, OrthographicView view, double radius, double cx, double cy, EarthScene scene)
        {
            var sun = Vec3.FromLatLon(scene.Subsolar.Lat, scene.Subsolar.Lon);
            var aurora = AuroraGrid.From(scene.Aurora);
            double pixel = 1 / radius;           // one pixel in disc units, for the antialiased edge
            double outer = 1 + HaloWidth;

            for (int y = 0; y < size; y++)
            {
                double v = (cy - (y + 0.5)) / radius;
                // Rows entirely outside the halo stay transparent.
                if (Math.Abs(v) > outer)
                    continue;

                for (int x = 0; x < size; x++)
                {
                    double u = ((x + 0.5) - cx) / radius;
                    double r = Math.Sqrt(u * u + v * v);
                    if (r > outer)
                        continue;

                    // The halo: a soft blue glow just outside the edge, fading out.
                    float haloA = 0;
                    if (r > 1 - pixel)
                    {
                        double t = 1 - (r - 1) / HaloWidth;
                        haloA = (float)(Math.Clamp(t, 0, 1) * Math.Clamp(t, 0, 1) * 0.45);
                    }

                    // How much of this pixel the disc covers, for a smooth edge.
                    float cover = (float)Math.Clamp((1 - r) / pixel + 0.5, 0, 1);
                    float cr = 0, cg = 0, cb = 0;
                    if (cover > 0)
                    {
                        // Pixels straddling the edge sample the limb itself.
                        double su = u, sv = v;
                        if (r > 1)
                        {
                            su /= r;
                            sv /= r;
                        }
                        view.TryUnproject(su, sv, out Vec3 p);
                        Shade(p, sun, aurora, Math.Min(r, 1), out cr, out cg, out cb);
                    }

                    // The disc over the halo, premultiplied.
                    float a = cover + haloA * (1 - cover);
                    float pr = cr * cover + Atmosphere.R * haloA * (1 - cover);
                    float pg = cg * cover + Atmosphere.G * haloA * (1 - cover);
                    float pb = cb * cover + Atmosphere.B * haloA * (1 - cover);

                    int i = (y * size + x) * 4;
                    pixels[i] = ToByte(pr);
                    pixels[i + 1] = ToByte(pg);
                    pixels[i + 2] = ToByte(pb);
                    pixels[i + 3] = ToByte(a);
                }
            }
        }

        // The colour of one point on the globe. r is its distance from the disc's centre (0..1).
        private void Shade(Vec3 p, Vec3 sun, AuroraGrid aurora, double r, out float red, out float green, out float blue)
        {
            var ground = p.ToLatLon();
            double cosSun = p.Dot(sun);
            float night = (float)DayNight.NightFactor(cosSun);

            _day.Sample(ground.Lat, ground.Lon, out float dr, out float dg, out float db);

            // A little shading toward the terminator so the day side looks round.
            float lit = 0.8f + 0.2f * (float)Math.Clamp(cosSun, 0, 1);
            dr *= lit; dg *= lit; db *= lit;

            red = dr; green = dg; blue = db;
            if (night > 0)
            {
                _night.Sample(ground.Lat, ground.Lon, out float lr, out float lg, out float lb);
                // Same treatment as the globe's shader: drop the map's faint background, warm up the lights.
                float lum = 0.299f * lr + 0.587f * lg + 0.114f * lb;
                float lights = (float)DayNight.SmoothStep(0.05, 0.2, lum) * 1.4f;
                float nr = dr * 0.06f + lr * lights;
                float ng = dg * 0.06f + lg * lights * 0.88f;
                float nb = db * 0.06f + lb * lights * 0.7f;

                // Aurora glows over the night side only: green, with red where it's strongest.
                float glow = aurora.Sample(ground.Lat, ground.Lon) * night;
                if (glow > 0)
                {
                    float strong = Math.Max(0, glow - 0.5f);
                    nr += glow * 0.2f + strong * 0.7f;
                    ng += glow * 0.95f;
                    nb += glow * 0.5f + strong * 0.3f;
                }

                red = dr + (nr - dr) * night;
                green = dg + (ng - dg) * night;
                blue = db + (nb - db) * night;
            }

            // Atmosphere: a thin blue haze that thickens toward the limb, fainter on the night side.
            float haze = (float)Math.Pow(r, 8) * (0.55f - 0.35f * night);
            red += (Atmosphere.R - red) * haze;
            green += (Atmosphere.G - green) * haze;
            blue += (Atmosphere.B - blue) * haze;
        }

        private static void DrawOverlays(SKCanvas canvas, OrthographicView view, double radius, double cx, double cy, int size, EarthScene scene)
        {
            SKPoint Screen(double u, double v) => new((float)(cx + u * radius), (float)(cy - v * radius));

            float line = Math.Max(1.5f, size / 220f);
            float dot = Math.Max(2.5f, size / 100f);

            // The ISS's track: the last 20 minutes fading out behind it, the next 10 fainter ahead.
            // Square-ended segments, because round ends overlap at each joint and show as beads when see-through.
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = line, StrokeCap = SKStrokeCap.Butt })
            {
                for (int i = 1; i < scene.Track.Count; i++)
                {
                    var a = scene.Track[i - 1];
                    var b = scene.Track[i];
                    var pa = view.Project(a.Point.Lat, a.Point.Lon);
                    var pb = view.Project(b.Point.Lat, b.Point.Lon);
                    if (!pa.Visible || !pb.Visible)
                        continue;

                    double mid = (a.Minutes + b.Minutes) / 2;
                    double alpha = mid <= 0
                        ? 0.9 * (1 + mid / EarthScene.TrackBehind.TotalMinutes)
                        : 0.45 * (1 - 0.5 * mid / EarthScene.TrackAhead.TotalMinutes);
                    paint.Color = IssColor.WithAlpha((byte)(Math.Clamp(alpha, 0, 1) * 255));
                    canvas.DrawLine(Screen(pa.U, pa.V), Screen(pb.U, pb.V), paint);
                }
            }

            if (scene.Pin != null)
            {
                var pin = view.Project(scene.Pin.Lat, scene.Pin.Lon);
                if (pin.Visible)
                {
                    var at = Screen(pin.U, pin.V);
                    float pinR = Math.Max(3f, size / 110f);
                    using var paint = new SKPaint { IsAntialias = true };
                    if (scene.IssAbovePin)
                    {
                        paint.Style = SKPaintStyle.Stroke;
                        paint.StrokeWidth = line;
                        paint.Color = PinRingColor;
                        canvas.DrawCircle(at, pinR * 2.6f, paint);
                    }
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = CardBackground;
                    canvas.DrawCircle(at, pinR + Math.Max(1f, line * 0.7f), paint);
                    paint.Color = PinColor;
                    canvas.DrawCircle(at, pinR, paint);
                }
            }

            if (scene.Iss != null)
            {
                var iss = view.Project(scene.Iss.Lat, scene.Iss.Lon);
                if (iss.Visible)
                {
                    var at = Screen(iss.U, iss.V);
                    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
                    paint.Color = IssColor.WithAlpha(90);
                    canvas.DrawCircle(at, dot * 2.2f, paint);
                    paint.Color = SKColors.White;
                    canvas.DrawCircle(at, dot, paint);
                }
            }
        }

        private static byte[] Encode(SKBitmap bitmap)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static byte ToByte(float value) => (byte)(Math.Clamp(value, 0, 1) * 255 + 0.5f);

        // OVATION's 1° grid as an array, softened once so the glow has no blocky cell edges.
        private sealed class AuroraGrid
        {
            private const int W = 360, H = 181;   // lon -180..179, lat -90..90
            private readonly float[] _cells;
            private readonly bool _empty;

            private AuroraGrid(float[] cells, bool empty)
            {
                _cells = cells;
                _empty = empty;
            }

            public static AuroraGrid From(IReadOnlyList<int[]> points)
            {
                var raw = new float[W * H];
                foreach (var pt in points)
                {
                    if (pt.Length < 3)
                        continue;
                    int x = ((pt[0] + 180) % W + W) % W;
                    int y = Math.Clamp(pt[1] + 90, 0, H - 1);
                    // 50% and up counts as full strength; the forecast rarely goes much higher.
                    raw[y * W + x] = Math.Min(1f, pt[2] / 50f);
                }

                // One 3×3 box blur.
                var soft = new float[W * H];
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        float sum = 0;
                        int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= H)
                                continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                sum += raw[yy * W + ((x + dx + W) % W)];
                                n++;
                            }
                        }
                        soft[y * W + x] = sum / n;
                    }
                }
                return new AuroraGrid(soft, points.Count == 0);
            }

            public float Sample(double lat, double lon)
            {
                if (_empty)
                    return 0;
                double x = lon + 180, y = lat + 90;
                int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                float fx = (float)(x - x0), fy = (float)(y - y0);
                int xa = ((x0 % W) + W) % W, xb = (xa + 1) % W;
                int ya = Math.Clamp(y0, 0, H - 1), yb = Math.Clamp(y0 + 1, 0, H - 1);
                return _cells[ya * W + xa] * (1 - fx) * (1 - fy) + _cells[ya * W + xb] * fx * (1 - fy)
                     + _cells[yb * W + xa] * (1 - fx) * fy + _cells[yb * W + xb] * fx * fy;
            }
        }
    }
}
