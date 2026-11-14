using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

public sealed partial class LocaleTests
{
    [GeneratedRegex(@"\{\{[A-Za-z]+\}\}|\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\.T\(\s*""([^""]+)""")]
    private static partial Regex CodeKey();

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Novalist.Extensions.AiAssistant.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The extension project folder was not found above the test output.");
    }

    private static Dictionary<string, string> Load(string language)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "Locales", language + ".json"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, language + ".json must not start with a BOM.");
        using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes));
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(document.RootElement, string.Empty, flat);
        return flat;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, into);
            return;
        }
        Assert.Equal(JsonValueKind.String, element.ValueKind);
        Assert.True(into.TryAdd(prefix, element.GetString()!), "Duplicate key " + prefix);
    }

    private static string[] Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData("de")]
    [InlineData("ar")]
    public void TranslationHasTheKeysOfTheEnglishFile(string language)
    {
        var english = Load("en");
        var translated = Load(language);
        Assert.Empty(english.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(english.Keys));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("ar")]
    public void TranslationKeepsEveryPlaceholderOfTheEnglishString(string language)
    {
        var english = Load("en");
        var translated = Load(language);
        foreach (var (key, text) in english)
            Assert.True(Placeholders(text).SequenceEqual(Placeholders(translated[key])),
                $"{language}: placeholders differ for {key}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("ar")]
    public void TranslationKeepsTheKeyOrderOfTheEnglishFile(string language)
    {
        Assert.Equal(Load("en").Keys, Load(language).Keys);
    }

    [Fact]
    public void ArabicIsWrittenInArabicLettersWithoutBidiWrappers()
    {
        var arabic = Load("ar");
        var wrappers = new[] { '⁦', '⁧', '⁨', '⁩', '‪', '‫', '‬', '‭', '‮' };
        foreach (var (key, text) in arabic)
        {
            Assert.DoesNotContain(text, character => wrappers.Contains(character));
            Assert.DoesNotContain('�', text);
            if (key.StartsWith("page.", StringComparison.Ordinal)) continue;
            Assert.True(text.Any(character => character is >= '؀' and <= 'ۿ') || !text.Any(char.IsLetter) || Latin(text),
                $"ar: {key} has no Arabic letters");
        }
    }

    private static bool Latin(string text) => Regex.IsMatch(text, @"^[A-Za-z0-9 ./_{}()-]+$");

    [Theory]
    [InlineData("en", "ltr", "en")]
    [InlineData("de", "ltr", "de")]
    [InlineData("ar", "rtl", "ar")]
    public void EachLocaleDeclaresItsReadingDirectionAndLanguage(string language, string direction, string tag)
    {
        var strings = Load(language);
        Assert.Equal(direction, strings["page.direction"]);
        Assert.Equal(tag, strings["page.lang"]);
    }

    [Fact]
    public void GermanKeepsItsUmlauts()
    {
        var german = string.Concat(Load("de").Values);
        foreach (var stem in new[] { "ueber", "fuer", "oeffn", "waehl", "aender", "Loesch" })
            Assert.DoesNotContain(stem, german, StringComparison.OrdinalIgnoreCase);
        Assert.Contains('ä', german);
        Assert.Contains('ü', german);
    }

    [Fact]
    public void EveryLiteralKeyTheCodeLooksUpExistsInTheEnglishFile()
    {
        var english = Load("en");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var folder in new[] { "Services", "ViewModels" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), folder), "*.cs"))
                foreach (Match match in CodeKey().Matches(File.ReadAllText(file)))
                    if (!english.ContainsKey(match.Groups[1].Value)) missing.Add(match.Groups[1].Value);
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryManifestViewTitleIsALocaleKeyPresentInAllLocales()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "extension.json")));
        var titles = manifest.RootElement.GetProperty("contributes").GetProperty("views").EnumerateArray()
            .Select(view => view.GetProperty("title").GetString()!).ToArray();
        Assert.Equal(4, titles.Length);

        var english = Load("en");
        foreach (var title in titles)
        {
            Assert.Matches(@"^[a-z][A-Za-z]*(\.[A-Za-z]+)+$", title);
            foreach (var language in new[] { "en", "de", "ar" })
                Assert.True(Load(language).ContainsKey(title), language + ".json lacks manifest view title key " + title);
        }
        foreach (var language in new[] { "de", "ar" })
            foreach (var title in titles)
                Assert.NotEqual(english[title], Load(language)[title]);
    }
}
