using System.Globalization;

namespace ZeroStrap.UI.Theming
{
    /// <summary>Small WPF-independent colour helpers for theme derivation and validation.</summary>
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
    }

    public static class ColorMath
    {
        public static bool TryParseHex(string? hex, out Rgb rgb)
        {
            rgb = default;

            if (hex is null || hex.Length != 7 || hex[0] != '#')
                return false;

            if (!byte.TryParse(hex.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte r) ||
                !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte g) ||
                !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b))
                return false;

            rgb = new Rgb(r, g, b);
            return true;
        }

        public static Rgb Parse(string hex) =>
            TryParseHex(hex, out var rgb) ? rgb : throw new FormatException($"\"{hex}\" is not a #RRGGBB colour.");

        /// <summary>Linear blend: 0 returns a, 1 returns b.</summary>
        public static Rgb Mix(Rgb a, Rgb b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            byte Ch(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return new Rgb(Ch(a.R, b.R), Ch(a.G, b.G), Ch(a.B, b.B));
        }

        public static (double H, double S, double L) ToHsl(Rgb c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2, h = 0, s = 0;
            double d = max - min;

            if (d > 1e-9)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;

                h /= 6;
            }

            return (h, s, l);
        }

        public static Rgb FromHsl(double h, double s, double l)
        {
            h -= Math.Floor(h);
            s = Math.Clamp(s, 0, 1);
            l = Math.Clamp(l, 0, 1);

            if (s < 1e-9)
            {
                byte v = (byte)Math.Round(l * 255);
                return new Rgb(v, v, v);
            }

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            static double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }

            static byte Ch(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            return new Rgb(Ch(Hue(p, q, h + 1.0 / 3)), Ch(Hue(p, q, h)), Ch(Hue(p, q, h - 1.0 / 3)));
        }

        /// <summary>
        /// Lenient hex input for text fields: surrounding spaces are ignored and the leading '#' is optional.
        /// Exactly six hex digits are required.
        /// </summary>
        public static bool TryParseLooseHex(string? input, out Rgb rgb)
        {
            rgb = default;

            string text = (input ?? "").Trim();
            if (text.Length == 6)
                text = "#" + text;

            return TryParseHex(text, out rgb);
        }

        /// <summary>HSV with H, S and V all in 0..1 (hue wraps).</summary>
        public static (double H, double S, double V) ToHsv(Rgb c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min, h = 0;

            if (d > 1e-9)
            {
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;

                h /= 6;
            }

            return (h, max <= 1e-9 ? 0 : d / max, max);
        }

        public static Rgb FromHsv(double h, double s, double v)
        {
            h -= Math.Floor(h);
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double scaled = h * 6;
            int sector = (int)Math.Floor(scaled) % 6;
            double f = scaled - Math.Floor(scaled);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);

            (double r, double g, double b) = sector switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q)
            };

            static byte Ch(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
            return new Rgb(Ch(r), Ch(g), Ch(b));
        }

        /// <summary>Moves HSL lightness by delta (positive lighter, negative darker), keeping hue and saturation.</summary>
        public static Rgb ShiftLightness(Rgb c, double delta)
        {
            var (h, s, l) = ToHsl(c);
            return FromHsl(h, s, l + delta);
        }

        /// <summary>WCAG 2.x relative luminance.</summary>
        public static double RelativeLuminance(Rgb c)
        {
            static double Lin(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        /// <summary>WCAG contrast ratio, 1 to 21.</summary>
        public static double ContrastRatio(Rgb a, Rgb b)
        {
            double la = RelativeLuminance(a), lb = RelativeLuminance(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }
    }
}
