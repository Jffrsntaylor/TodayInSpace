using SkiaSharp;

namespace TodayInSpace.Web.Sky
{
    // An equirectangular world map (longitude -180..180 left to right, latitude 90..-90 top to bottom),
    // decoded once and kept as plain RGB bytes so sampling it is just array reads.
    public sealed class EarthTexture
    {
        public int Width { get; }
        public int Height { get; }
        private readonly byte[] _rgb;

        public EarthTexture(int width, int height, byte[] rgb)
        {
            if (rgb.Length != width * height * 3)
                throw new ArgumentException("Expected 3 bytes per pixel.", nameof(rgb));
            Width = width;
            Height = height;
            _rgb = rgb;
        }

        public static EarthTexture Decode(Stream image)
        {
            using var decoded = SKBitmap.Decode(image) ?? throw new InvalidDataException("Not an image Skia can read.");
            // The decoder picks the platform's byte order, so convert to a known one first.
            using var rgba = decoded.Copy(SKColorType.Rgba8888) ?? throw new InvalidDataException("Couldn't convert the image.");
            int w = rgba.Width, h = rgba.Height, stride = rgba.RowBytes;
            byte[] src = rgba.Bytes;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = y * stride + x * 4, d = (y * w + x) * 3;
                    rgb[d] = src[s];
                    rgb[d + 1] = src[s + 1];
                    rgb[d + 2] = src[s + 2];
                }
            }
            return new EarthTexture(w, h, rgb);
        }

        // Bilinear sample at a lat/lon, 0..1 per channel. Wraps around in longitude so there's no seam
        // at the antimeridian.
        public void Sample(double lat, double lon, out float r, out float g, out float b)
        {
            double x = (lon + 180) / 360 * Width - 0.5;
            double y = (90 - lat) / 180 * Height - 0.5;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = (float)(x - x0), fy = (float)(y - y0);

            int xa = ((x0 % Width) + Width) % Width, xb = (xa + 1) % Width;
            int ya = Math.Clamp(y0, 0, Height - 1), yb = Math.Clamp(y0 + 1, 0, Height - 1);

            int i00 = (ya * Width + xa) * 3, i10 = (ya * Width + xb) * 3;
            int i01 = (yb * Width + xa) * 3, i11 = (yb * Width + xb) * 3;

            float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
            const float Scale = 1f / 255;
            r = (_rgb[i00] * w00 + _rgb[i10] * w10 + _rgb[i01] * w01 + _rgb[i11] * w11) * Scale;
            g = (_rgb[i00 + 1] * w00 + _rgb[i10 + 1] * w10 + _rgb[i01 + 1] * w01 + _rgb[i11 + 1] * w11) * Scale;
            b = (_rgb[i00 + 2] * w00 + _rgb[i10 + 2] * w10 + _rgb[i01 + 2] * w01 + _rgb[i11 + 2] * w11) * Scale;
        }
    }
}
