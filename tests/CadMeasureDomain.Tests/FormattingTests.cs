using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Форматы характеристик и кратких кодов.
///
/// Здесь проверяется главное разделение: характеристику читает инженер
/// (локаль пользователя, «3,5»), а код слоя должен быть одинаковым на любой
/// машине («3.5»), потому что запятая в имени слоя AutoCAD запрещена.
/// </summary>
public class MaterialFormatterTests
{
    [Fact]
    public void FormatNumber_UsesCurrentCulture()
    {
        using (new CultureScope("ru-RU"))
            Assert.Equal("3,5", MaterialFormatter.FormatNumber(3.5));

        using (new CultureScope("en-US"))
            Assert.Equal("3.5", MaterialFormatter.FormatNumber(3.5));
    }

    [Fact]
    public void FormatNumber_DropsTrailingZeros()
    {
        using var _ = new CultureScope("ru-RU");

        Assert.Equal("4", MaterialFormatter.FormatNumber(4.0));
        Assert.Equal("88,9", MaterialFormatter.FormatNumber(88.9));
    }

    [Fact]
    public void FormatNumberInvariant_IgnoresCulture()
    {
        using var _ = new CultureScope("ru-RU");

        Assert.Equal("3.5", MaterialFormatter.FormatNumberInvariant(3.5));
    }

    [Fact]
    public void FormatNumberRu_AlwaysUsesComma()
    {
        // Наименования каталога — ключи материалов. Они не должны зависеть
        // от локали, в которой сгенерирована номенклатура.
        using var _ = new CultureScope("en-US");

        Assert.Equal("3,5", MaterialFormatter.FormatNumberRu(3.5));
    }

    [Fact]
    public void BuildCharacteristic_Pipe()
    {
        using var _ = new CultureScope("ru-RU");

        Assert.Equal("⌀89x4", MaterialFormatter.BuildCharacteristic(TestData.Pipe()));
        Assert.Equal("⌀88,9x3,5", MaterialFormatter.BuildCharacteristic(TestData.Pipe(diameter: 88.9, wall: 3.5)));
        Assert.Equal("⌀32", MaterialFormatter.BuildCharacteristic(TestData.Pipe(diameter: 32, wall: null)));
        Assert.Equal(string.Empty, MaterialFormatter.BuildCharacteristic(TestData.Pipe(diameter: null, wall: null)));
    }

    [Fact]
    public void BuildCharacteristic_Duct()
    {
        using var _ = new CultureScope("ru-RU");

        Assert.Equal("1250x800, 0,9 мм", MaterialFormatter.BuildCharacteristic(TestData.RectDuct()));
        Assert.Equal("1250x800", MaterialFormatter.BuildCharacteristic(TestData.RectDuct(sheet: null)));
        Assert.Equal("⌀200, 0,7 мм", MaterialFormatter.BuildCharacteristic(TestData.RoundDuct()));
        Assert.Equal(
            string.Empty,
            MaterialFormatter.BuildCharacteristic(TestData.RectDuct(width: null, height: null, sheet: null)));
    }

    [Fact]
    public void BuildCharacteristic_Cable()
    {
        using var _ = new CultureScope("ru-RU");

        // Разделитель здесь — КИРИЛЛИЧЕСКАЯ «х», как в наименованиях каталога
        // («ВВГнг(А)-LS 3х2,5»). В коде слоя на этом же месте стоит латинская «x»:
        // характеристику читает человек, а код слоя разбирает AutoCAD.
        Assert.Equal("3х2,5", MaterialFormatter.BuildCharacteristic(TestData.Cable()));
        Assert.Equal(string.Empty, MaterialFormatter.BuildCharacteristic(TestData.Cable(cores: null)));
    }

    [Fact]
    public void BuildCharacteristic_Piece()
    {
        Assert.Equal("Dn15", MaterialFormatter.BuildCharacteristic(TestData.Piece()));
        Assert.Equal(string.Empty, MaterialFormatter.BuildCharacteristic(TestData.Piece(nominalDiameter: null)));
    }

    [Fact]
    public void BuildCharacteristic_UnknownClassIsEmpty()
    {
        var material = new Material { Class = "НечтоИзJson", Name = "Материал" };

        Assert.Equal(string.Empty, MaterialFormatter.BuildCharacteristic(material));
    }

    [Fact]
    public void BuildShortCode_IsCultureIndependent()
    {
        using var _ = new CultureScope("ru-RU");

        // Даже в русской локали в коде слоя точка, а не запятая.
        Assert.Equal("D88.9x3.5", MaterialFormatter.BuildShortCode(TestData.Pipe(diameter: 88.9, wall: 3.5)));
        Assert.Equal("1250x800_t0.9", MaterialFormatter.BuildShortCode(TestData.RectDuct()));
        Assert.Equal("D200_t0.7", MaterialFormatter.BuildShortCode(TestData.RoundDuct()));
        Assert.Equal("3x2.5", MaterialFormatter.BuildShortCode(TestData.Cable()));
        Assert.Equal("Dn15", MaterialFormatter.BuildShortCode(TestData.Piece()));
    }

    [Fact]
    public void BuildShortCode_NeverReturnsEmpty()
    {
        var withoutCharacteristics = new Material { Class = MaterialClasses.Pipe, Name = "Труба без размеров" };
        var withGarbageClass = new Material { Class = "?*|", Name = "Материал с мусорным классом" };

        Assert.StartsWith("N", MaterialFormatter.BuildShortCode(withoutCharacteristics));
        Assert.NotEmpty(MaterialFormatter.BuildShortCode(withGarbageClass));
    }

    [Fact]
    public void BuildFallbackCode_IsStableAndDistinct()
    {
        var first = new Material { Class = MaterialClasses.Pipe, Name = "Труба А" };
        var second = new Material { Class = MaterialClasses.Pipe, Name = "Труба Б" };

        Assert.Equal(MaterialFormatter.BuildFallbackCode(first), MaterialFormatter.BuildFallbackCode(first));
        Assert.NotEqual(MaterialFormatter.BuildFallbackCode(first), MaterialFormatter.BuildFallbackCode(second));
        Assert.True(LayerNameSanitizer.IsValidLayerName(MaterialFormatter.BuildFallbackCode(first)));
    }

    [Fact]
    public void StableHash_IsDeterministicAndNonNegative()
    {
        // String.GetHashCode рандомизирован между запусками процесса — от него
        // цвета слоёв и запасные коды прыгали бы при каждом старте AutoCAD.
        Assert.Equal(MaterialFormatter.StableHash("Труба стальная ⌀89x4"), MaterialFormatter.StableHash("Труба стальная ⌀89x4"));
        Assert.NotEqual(MaterialFormatter.StableHash("Труба А"), MaterialFormatter.StableHash("Труба Б"));
        Assert.True(MaterialFormatter.StableHash("Труба стальная ⌀89x4") >= 0);
        Assert.Equal(0, MaterialFormatter.StableHash(null));
        Assert.Equal(0, MaterialFormatter.StableHash(string.Empty));
    }

    [Fact]
    public void IsRoundDuct_DistinguishesRoundFromRectangular()
    {
        Assert.True(TestData.RoundDuct().IsRoundDuct);
        Assert.False(TestData.RectDuct().IsRoundDuct);

        // У трубы тоже есть диаметр, но круглым воздуховодом она не становится.
        Assert.False(TestData.Pipe().IsRoundDuct);
    }

    [Theory]
    [InlineData(MaterialClasses.Pipe, true)]
    [InlineData(MaterialClasses.Duct, true)]
    [InlineData(MaterialClasses.Cable, true)]
    [InlineData(MaterialClasses.Piece, false)]
    [InlineData("НечтоИзJson", false)]
    public void IsLinear_DependsOnClass(string materialClass, bool expected)
    {
        Assert.Equal(expected, MaterialClasses.IsLinear(materialClass));
    }
}

/// <summary>
/// Округление длин. Округляется итог, а не слагаемые: на сотне полилиний
/// ошибка округления копилась бы и ведомость перестала бы сходиться.
/// </summary>
public class MeasurementRoundingTests
{
    [Fact]
    public void RoundLength_KeepsTwoDecimals()
    {
        Assert.Equal(2, MeasurementRounding.LengthDecimals);
        Assert.Equal(12.34, MeasurementRounding.RoundLength(12.344));
        Assert.Equal(12.35, MeasurementRounding.RoundLength(12.346));
    }

    [Fact]
    public void RoundLength_RoundsHalfAwayFromZero()
    {
        // 0,125 представимо в double точно, поэтому середина здесь настоящая:
        // банковское округление дало бы 0,12, в спецификации нужно 0,13.
        Assert.Equal(0.13, MeasurementRounding.RoundLength(0.125));
        Assert.Equal(-0.13, MeasurementRounding.RoundLength(-0.125));
    }

    [Fact]
    public void RoundLength_LeavesShortValuesUntouched()
    {
        Assert.Equal(0, MeasurementRounding.RoundLength(0));
        Assert.Equal(2.5, MeasurementRounding.RoundLength(2.5));
    }
}

/// <summary>
/// Цвета слоёв. Диапазоны заданы ТЗ, а детерминированность нужна,
/// чтобы один материал не менял цвет от сеанса к сеансу.
/// </summary>
public class LayerColorServiceTests
{
    [Fact]
    public void GetColorIndex_PipesUseLeftRange()
    {
        var index = LayerColorService.GetColorIndex(TestData.Pipe());

        Assert.InRange(index, 1, 128);
    }

    [Fact]
    public void GetColorIndex_DuctsUseRightRange()
    {
        var index = LayerColorService.GetColorIndex(TestData.RectDuct());

        Assert.InRange(index, 129, 255);
    }

    [Fact]
    public void GetColorIndex_CablesAndPiecesUseLeftRange()
    {
        Assert.InRange(LayerColorService.GetColorIndex(TestData.Cable()), 1, 128);
        Assert.InRange(LayerColorService.GetColorIndex(TestData.Piece()), 1, 128);
    }

    [Fact]
    public void GetColorIndex_IsDeterministic()
    {
        var material = TestData.Pipe();

        Assert.Equal(LayerColorService.GetColorIndex(material), LayerColorService.GetColorIndex(material));
    }

    [Fact]
    public void GetColorIndex_NeverReturnsReservedIndexAcrossWholeCatalog()
    {
        // Индекс 7 — «по фону»: такие линии теряются среди основной графики.
        // Проверяем на всей номенклатуре, а не на паре примеров.
        var catalog = MaterialCatalog.CreateDefault();

        Assert.All(catalog, m => Assert.NotEqual(7, LayerColorService.GetColorIndex(m)));
    }

    [Fact]
    public void GetColorIndex_StaysInClassRangeAcrossWholeCatalog()
    {
        var catalog = MaterialCatalog.CreateDefault();

        Assert.All(catalog, m =>
        {
            var index = LayerColorService.GetColorIndex(m);
            if (m.Class == MaterialClasses.Duct) Assert.InRange(index, 129, 255);
            else Assert.InRange(index, 1, 128);
        });
    }

    [Fact]
    public void GetColorIndex_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => LayerColorService.GetColorIndex(null!));
    }
}
