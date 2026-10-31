using ZeroStrap.Utility;
using Xunit;

namespace ZeroStrap.Tests
{
    public class PathValidatorTests
    {
        [Theory]
        [InlineData("MyTheme")]
        [InlineData("My Theme")]
        [InlineData("Theme-1_v2")]
        [InlineData("console")]
        [InlineData("foo.con")]
        [InlineData(".con")]
        [InlineData("theme.v2")]
        [InlineData("Ünïcødé")]
        public void IsFileNameValid_Ok(string name)
        {
            Assert.Equal(PathValidator.ValidationResult.Ok, PathValidator.IsFileNameValid(name));
        }

        [Fact]
        public void IsFileNameValid_MaxLengthIsOk()
        {
            Assert.Equal(PathValidator.ValidationResult.Ok, PathValidator.IsFileNameValid(new string('a', 100)));
        }

        [Theory]
        [InlineData("a/b")]
        [InlineData("a:b")]
        [InlineData("a?b")]
        [InlineData("a\u0001b")]
        [InlineData("MyTheme.")]
        [InlineData("MyTheme ")]
        [InlineData("con.")]
        public void IsFileNameValid_IllegalCharacter(string name)
        {
            Assert.Equal(PathValidator.ValidationResult.IllegalCharacter, PathValidator.IsFileNameValid(name));
        }

        [Theory]
        [InlineData("   ")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("CON")]
        [InlineData("con")]
        [InlineData("con.txt")]
        [InlineData("com1.tar.gz")]
        [InlineData("CON .txt")]
        [InlineData("LPT9")]
        [InlineData("NUL")]
        [InlineData("COM¹")]
        [InlineData("CONIN$")]
        public void IsFileNameValid_ReservedFileName(string name)
        {
            Assert.Equal(PathValidator.ValidationResult.ReservedFileName, PathValidator.IsFileNameValid(name));
        }

        [Fact]
        public void IsFileNameValid_TooLongIsReserved()
        {
            Assert.Equal(PathValidator.ValidationResult.ReservedFileName, PathValidator.IsFileNameValid(new string('a', 101)));
        }
    }
}
