using System;
using Xunit;

using ZeroStrap.UI.Theming;

namespace ZeroStrap.Tests
{
    public class ColorPickerLogicTests
    {
        [Theory]
        [InlineData("#FF0000", 0.0, 1.0, 1.0)]
        [InlineData("#00FF00", 1.0 / 3, 1.0, 1.0)]
        [InlineData("#0000FF", 2.0 / 3, 1.0, 1.0)]
        [InlineData("#FFFFFF", 0.0, 0.0, 1.0)]
        [InlineData("#000000", 0.0, 0.0, 0.0)]
        [InlineData("#808080", 0.0, 0.0, 128 / 255.0)]
        public void ToHsvKnownColours(string hex, double h, double s, double v)
        {
            var (hh, ss, vv) = ColorMath.ToHsv(ColorMath.Parse(hex));
            Assert.Equal(h, hh, 3);
            Assert.Equal(s, ss, 3);
            Assert.Equal(v, vv, 3);
        }

        [Fact]
        public void HsvRoundTripsEveryEighthColour()
        {
            for (int r = 0; r < 256; r += 8)
                for (int g = 0; g < 256; g += 8)
                    for (int b = 0; b < 256; b += 8)
                    {
                        var rgb = new Rgb((byte)r, (byte)g, (byte)b);
                        var (h, s, v) = ColorMath.ToHsv(rgb);
                        Assert.Equal(rgb, ColorMath.FromHsv(h, s, v));
                    }
        }

        [Fact]
        public void FromHsvWrapsHueAndClampsRanges()
        {
            Assert.Equal(ColorMath.FromHsv(0, 1, 1), ColorMath.FromHsv(1, 1, 1));
            Assert.Equal(ColorMath.FromHsv(0.25, 1, 1), ColorMath.FromHsv(-0.75, 1, 1));
            Assert.Equal(new Rgb(255, 255, 255), ColorMath.FromHsv(0.3, -2, 5));
            Assert.Equal(new Rgb(0, 0, 0), ColorMath.FromHsv(0.3, 0.5, -1));
        }

        [Theory]
        [InlineData("#a1B2c3", true, "#A1B2C3")]
        [InlineData("a1b2c3", true, "#A1B2C3")]
        [InlineData("  #000000 ", true, "#000000")]
        [InlineData("#FFF", false, null)]
        [InlineData("#GGGGGG", false, null)]
        [InlineData("#1234567", false, null)]
        [InlineData("", false, null)]
        [InlineData(null, false, null)]
        public void LooseHexParsing(string? input, bool ok, string? expected)
        {
            Assert.Equal(ok, ColorMath.TryParseLooseHex(input, out var rgb));
            if (ok)
                Assert.Equal(expected, rgb.ToHex());
        }

        [Theory]
        [InlineData(21, ContrastLevel.Ok)]
        [InlineData(4.5, ContrastLevel.Ok)]
        [InlineData(4.49, ContrastLevel.Low)]
        [InlineData(3.0, ContrastLevel.Low)]
        [InlineData(2.99, ContrastLevel.TooLow)]
        [InlineData(1.0, ContrastLevel.TooLow)]
        public void ContrastClassification(double ratio, ContrastLevel level) =>
            Assert.Equal(level, ContrastNote.Classify(ratio));

        [Fact]
        public void ContrastMessagesTruncateTheRatio()
        {
            Assert.Null(ContrastNote.Message(7));
            Assert.Equal("Low contrast (4.4:1)", ContrastNote.Message(4.49));
            Assert.Equal("Too low to read (2.9:1)", ContrastNote.Message(2.99));
            Assert.Equal("Too low to read (1.0:1)", ContrastNote.Message(1.0));
        }

        [Fact]
        public void RatioUsesWcagAndIgnoresBadInput()
        {
            Assert.Equal(21, ContrastNote.Ratio("#000000", "#FFFFFF"), 1);
            Assert.Equal(1, ContrastNote.Ratio("#777777", "#777777"), 3);
            Assert.Equal(21, ContrastNote.Ratio("nope", "#FFFFFF"));
        }
    }
}
