using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Чистка строк под требования AutoCAD к именам слоёв.
/// Имя слоя вычисляется из реестра материалов, который правит человек,
/// поэтому сюда попадает любой мусор.
/// </summary>
public class LayerNameSanitizerTests
{
    [Theory]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("\"")]
    [InlineData(":")]
    [InlineData(";")]
    [InlineData("?")]
    [InlineData("*")]
    [InlineData("|")]
    [InlineData(",")]
    [InlineData("=")]
    [InlineData("`")]
    public void Sanitize_ReplacesForbiddenCharacterWithUnderscore(string forbidden)
    {
        var result = LayerNameSanitizer.Sanitize($"PIPE{forbidden}D89");

        Assert.Equal("PIPE_D89", result);
        Assert.True(LayerNameSanitizer.IsValidLayerName(result));
    }

    [Fact]
    public void Sanitize_DropsControlCharacters()
    {
        Assert.Equal("PIPED89", LayerNameSanitizer.Sanitize("PIPE" + (char)1 + "D89" + (char)7));
    }

    [Fact]
    public void Sanitize_TrimsSurroundingWhitespace()
    {
        // Ведущие и замыкающие пробелы AutoCAD отбрасывает молча — два
        // материала получили бы один слой.
        Assert.Equal("PIPE_D89", LayerNameSanitizer.Sanitize("   PIPE_D89   "));
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLayerNameLength()
    {
        var result = LayerNameSanitizer.Sanitize(new string('A', LayerNameSanitizer.MaxLayerNameLength + 50));

        Assert.Equal(LayerNameSanitizer.MaxLayerNameLength, result.Length);
        Assert.True(LayerNameSanitizer.IsValidLayerName(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_ReturnsEmptyForBlankInput(string? input)
    {
        Assert.Equal(string.Empty, LayerNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" PIPE", false)]
    [InlineData("PIPE ", false)]
    [InlineData("PIPE,D89", false)]
    [InlineData("PIPE_D89x4_Этаж 1", true)]
    [InlineData("MEAS_N0A1B2C", true)]
    public void IsValidLayerName_FollowsAutocadRules(string? name, bool expected)
    {
        Assert.Equal(expected, LayerNameSanitizer.IsValidLayerName(name));
    }

    [Fact]
    public void IsValidLayerName_RejectsControlCharacters()
    {
        Assert.False(LayerNameSanitizer.IsValidLayerName("PIPE" + (char)1));
    }

    [Fact]
    public void IsValidLayerName_RejectsNameLongerThanLimit()
    {
        Assert.True(LayerNameSanitizer.IsValidLayerName(new string('A', LayerNameSanitizer.MaxLayerNameLength)));
        Assert.False(LayerNameSanitizer.IsValidLayerName(new string('A', LayerNameSanitizer.MaxLayerNameLength + 1)));
    }
}

/// <summary>
/// Мэппинг «материал + участок ↔ имя слоя».
/// Ключевое требование: журнал восстанавливается из чертежа, поэтому имя
/// слоя должно разбираться обратно в ту же пару, из которой собрано.
/// </summary>
public class LayerNameFactoryTests
{
    [Theory]
    [InlineData(MaterialClasses.Pipe, "PIPE")]
    [InlineData(MaterialClasses.Duct, "DUCT")]
    [InlineData(MaterialClasses.Cable, "CABLE")]
    [InlineData(MaterialClasses.Piece, "PIECE")]
    [InlineData("НечтоИзJson", "MEAS")]
    [InlineData(null, "MEAS")]
    public void GetPrefix_MapsClassToPrefix(string? materialClass, string expected)
    {
        Assert.Equal(expected, LayerNameFactory.GetPrefix(materialClass));
    }

    [Fact]
    public void ComposeBaseName_BuildsCodeFromCharacteristics()
    {
        Assert.Equal("PIPE_D89x4", LayerNameFactory.ComposeBaseName(TestData.Pipe()));
        Assert.Equal("DUCT_1250x800_t0.9", LayerNameFactory.ComposeBaseName(TestData.RectDuct()));
        Assert.Equal("DUCT_D200_t0.7", LayerNameFactory.ComposeBaseName(TestData.RoundDuct()));
        Assert.Equal("CABLE_3x2.5", LayerNameFactory.ComposeBaseName(TestData.Cable()));
        Assert.Equal("PIECE_Dn15", LayerNameFactory.ComposeBaseName(TestData.Piece()));
    }

    [Fact]
    public void ComposeBaseName_FallsBackToHashWhenCharacteristicsAreMissing()
    {
        // Позиция без единой характеристики: имя слоя всё равно обязано получиться.
        var material = new Material { Class = MaterialClasses.Pipe, Name = "Труба без характеристик" };

        var name = LayerNameFactory.ComposeBaseName(material);

        Assert.StartsWith("PIPE_N", name);
        Assert.True(LayerNameSanitizer.IsValidLayerName(name));
    }

    [Fact]
    public void ComposeBaseName_SurvivesGarbageClass()
    {
        var material = new Material { Class = "<:*|>", Name = "Материал с мусорным классом" };

        var name = LayerNameFactory.ComposeBaseName(material);

        Assert.True(LayerNameSanitizer.IsValidLayerName(name));
    }

    [Fact]
    public void ComposeBaseName_IsStableForSameMaterial()
    {
        var material = new Material { Class = MaterialClasses.Pipe, Name = "Труба без характеристик" };

        Assert.Equal(LayerNameFactory.ComposeBaseName(material), LayerNameFactory.ComposeBaseName(material));
    }

    [Fact]
    public void GetLayerName_AppendsSection()
    {
        var factory = new LayerNameFactory();

        Assert.Equal("PIPE_D89x4_Этаж 1", factory.GetLayerName(TestData.Pipe(), "Этаж 1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetLayerName_WithoutSectionReturnsBaseName(string? section)
    {
        var factory = new LayerNameFactory();

        Assert.Equal("PIPE_D89x4", factory.GetLayerName(TestData.Pipe(), section));
    }

    [Fact]
    public void GetLayerName_SanitizesSection()
    {
        var factory = new LayerNameFactory();

        var name = factory.GetLayerName(TestData.Pipe(), "Этаж: 1, ось А");

        Assert.Equal("PIPE_D89x4_Этаж_ 1_ ось А", name);
        Assert.True(LayerNameSanitizer.IsValidLayerName(name));
    }

    [Fact]
    public void GetBaseName_ResolvesCollisionBetweenDifferentMaterials()
    {
        // Две разные позиции реестра с одинаковыми характеристиками дают
        // одинаковый код. Слой у них обязан быть разный, иначе замеры
        // двух материалов сложились бы в один.
        var factory = new LayerNameFactory();
        var first = TestData.Pipe("Труба стальная ⌀89x4");
        var second = TestData.Pipe("Труба оцинкованная ⌀89x4");

        var firstName = factory.GetBaseName(first);
        var secondName = factory.GetBaseName(second);

        Assert.Equal("PIPE_D89x4", firstName);
        Assert.NotEqual(firstName, secondName);
        Assert.True(LayerNameSanitizer.IsValidLayerName(secondName));
    }

    [Fact]
    public void GetBaseName_IsStableAcrossCalls()
    {
        var factory = new LayerNameFactory();
        var material = TestData.Pipe();

        Assert.Equal(factory.GetBaseName(material), factory.GetBaseName(material));
    }

    [Fact]
    public void SyncWithRegistry_AssignsNameToEveryMaterial()
    {
        var factory = new LayerNameFactory();
        var registry = new[] { TestData.Pipe(), TestData.RectDuct(), TestData.Cable(), TestData.Piece() };

        factory.SyncWithRegistry(registry);

        var names = registry.Select(factory.GetBaseName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.True(LayerNameSanitizer.IsValidLayerName(n)));
    }

    [Fact]
    public void TryResolveLayer_RestoresMaterialAndSection()
    {
        var factory = new LayerNameFactory();
        var pipe = TestData.Pipe();
        factory.SyncWithRegistry(new[] { pipe });

        var layerName = factory.GetLayerName(pipe, "Этаж 1");
        var resolved = factory.TryResolveLayer(layerName, out var material, out var section);

        Assert.True(resolved);
        Assert.Same(pipe, material);
        Assert.Equal("Этаж 1", section);
    }

    [Fact]
    public void TryResolveLayer_ReturnsEmptySectionForBaseName()
    {
        var factory = new LayerNameFactory();
        var pipe = TestData.Pipe();
        factory.SyncWithRegistry(new[] { pipe });

        Assert.True(factory.TryResolveLayer("PIPE_D89x4", out var material, out var section));
        Assert.Same(pipe, material);
        Assert.Equal(string.Empty, section);
    }

    [Fact]
    public void TryResolveLayer_PrefersLongestBaseName()
    {
        // «PIPE_D89x4_Этаж 1» не должно разобраться по основе «PIPE_D89»:
        // иначе замер трубы ⌀89x4 попал бы в журнал как труба ⌀89.
        var factory = new LayerNameFactory();
        var thin = TestData.Pipe("Труба ⌀89 без стенки", wall: null);
        var thick = TestData.Pipe("Труба ⌀89x4");
        factory.SyncWithRegistry(new[] { thin, thick });

        Assert.True(factory.TryResolveLayer("PIPE_D89x4_Этаж 1", out var material, out var section));
        Assert.Same(thick, material);
        Assert.Equal("Этаж 1", section);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("СЛУЧАЙНЫЙ_СЛОЙ")]
    [InlineData("0")]
    public void TryResolveLayer_RejectsForeignLayers(string? layerName)
    {
        var factory = new LayerNameFactory();
        factory.SyncWithRegistry(new[] { TestData.Pipe() });

        Assert.False(factory.TryResolveLayer(layerName, out var material, out var section));
        Assert.Null(material);
        Assert.Equal(string.Empty, section);
    }

    [Fact]
    public void Unregister_FreesBaseNameForNextMaterial()
    {
        // После удаления позиции из реестра её имя должно освободиться:
        // иначе новая позиция с теми же характеристиками получила бы слой
        // с индексом коллизии вместо чистого.
        var factory = new LayerNameFactory();
        var original = TestData.Pipe("Труба стальная ⌀89x4");
        factory.SyncWithRegistry(new[] { original });

        factory.Unregister(original.Key);

        var replacement = TestData.Pipe("Труба новая ⌀89x4");
        Assert.Equal("PIPE_D89x4", factory.GetBaseName(replacement));
    }

    [Fact]
    public void Unregister_IgnoresUnknownKey()
    {
        var factory = new LayerNameFactory();

        factory.Unregister("нет такого материала");
        factory.Unregister(string.Empty);
    }

    [Fact]
    public void NormalizeSection_TrimsAndSanitizes()
    {
        Assert.Equal("Этаж_ 1", LayerNameFactory.NormalizeSection("  Этаж: 1  "));
        Assert.Equal(string.Empty, LayerNameFactory.NormalizeSection(null));
    }
}
