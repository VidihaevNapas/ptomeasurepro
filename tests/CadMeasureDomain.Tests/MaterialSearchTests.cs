using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Поиск материала по первым буквам слов. Наименования в реестре длинные
/// и точные, набирать их целиком никто не будет.
/// </summary>
public class MaterialSearchTests
{
    private static readonly string[] Registry =
    {
        "Бетон М200",
        "Бетон М300",
        "Бетон М500",
        "Бетон М550",
        "Бетонная смесь",
        "Арматура А500С d12",
        "Арматурная сетка d12",
        "Арматура А240 d8",
        "Труба стальная электросварная Dn80 (⌀88,9x3,5)",
        "Кабель ВВГнг(А)-LS 3х2,5"
    };

    private static string[] Search(string query) => MaterialSearch.FilterNames(Registry, query).ToArray();

    [Fact]
    public void SingleWord_FindsEveryNameStartingWithIt()
    {
        Assert.Equal(
            new[] { "Бетон М200", "Бетон М300", "Бетон М500", "Бетон М550", "Бетонная смесь" },
            Search("бет"));
    }

    [Fact]
    public void SeveralWords_RequireEveryPrefixToMatch()
    {
        Assert.Equal(new[] { "Бетон М500", "Бетон М550" }, Search("бет м5"));
    }

    [Fact]
    public void FragmentGluedToAnotherCharacterIsStillFound()
    {
        // «12» стоит внутри «d12» — по началу слова его не найти,
        // и без запасного правила «арм 12» не нашло бы ничего.
        Assert.Equal(new[] { "Арматура А500С d12", "Арматурная сетка d12" }, Search("арм 12"));
    }

    [Fact]
    public void SearchIgnoresCase()
    {
        Assert.Equal(Search("бет м5"), Search("БЕТ М5"));
        Assert.Equal(Search("бет м5"), Search("Бет м5"));
    }

    [Fact]
    public void WordsAreMatchedInOrder()
    {
        // Порядок слов в наименовании осмысленный: «м5 бет» — это не то же,
        // что «бет м5», и подсказывать пользователю несуществующий порядок
        // не нужно.
        Assert.NotEmpty(Search("бет м5"));
        Assert.Empty(Search("м5 бет"));
    }

    [Fact]
    public void PunctuationInNameDoesNotHideWords()
    {
        // Скобки, запятые и дефисы — разделители, иначе «труба 88»
        // не нашло бы «(⌀88,9x3,5)».
        Assert.Contains("Труба стальная электросварная Dn80 (⌀88,9x3,5)", Search("тру 88"));
        Assert.Contains("Кабель ВВГнг(А)-LS 3х2,5", Search("каб ls"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQueryMatchesEverything(string? query)
    {
        Assert.Equal(Registry.Length, MaterialSearch.FilterNames(Registry, query).Count);
    }

    [Fact]
    public void UnknownWordsFindNothing()
    {
        Assert.Empty(Search("бет кирпич"));
        Assert.Empty(Search("щебень"));
    }

    [Fact]
    public void Filter_WorksOnMaterialsNotOnlyNames()
    {
        var materials = new[]
        {
            TestData.Pipe("Труба стальная Dn80"),
            TestData.Cable("Кабель ВВГнг(А)-LS 3х2,5"),
            TestData.RectDuct("Воздуховод 1250x800")
        };

        var found = MaterialSearch.Filter(materials, "тру dn8");

        Assert.Single(found);
        Assert.Equal("Труба стальная Dn80", found[0].Name);
    }

    [Fact]
    public void Filter_HandlesNullNameWithoutFailing()
    {
        var materials = new[] { new Material { Class = MaterialClasses.Pipe, Name = string.Empty } };

        Assert.Empty(MaterialSearch.Filter(materials, "тру"));
    }
}
