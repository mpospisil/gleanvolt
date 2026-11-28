using System.Globalization;
using Gleanvolt.Chargers;
using Gleanvolt.Plugins;
using Gleanvolt.Providers.Tests.Resources;
using Xunit;

namespace Gleanvolt.Providers.Tests;

public sealed class WordsTests
{
    [Fact]
    public void BothLanguagesComeFromTheResxWhateverTheThreadsCulture()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            var words = Words.From(TestText.ResourceManager, "Both");
            Assert.Equal("Both", words.En);
            Assert.Equal("Obojí", words.Cs);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void AMissingTranslationIsEnglish() =>
        Assert.Equal("Only 3", Words.From(TestText.ResourceManager, "EnglishOnly").Format(3).Cs);

    [Fact]
    public void AnUnknownKeyIsRefused() =>
        Assert.Contains("Nope", Assert.Throws<ArgumentException>(() => Words.From(TestText.ResourceManager, "Nope")).Message);
}
