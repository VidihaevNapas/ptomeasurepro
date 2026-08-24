using System.Text.Json;
using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Чтение дробных характеристик из materials.json.
/// Файл правят руками в русской локали, поэтому «3,5» встречается постоянно
/// и не должно молча превращаться в потерянную характеристику.
/// </summary>
public class FlexibleNullableDoubleConverterTests
{
    [Theory]
    [InlineData("3,5", 3.5)]
    [InlineData("3.5", 3.5)]
    [InlineData("  88,9  ", 88.9)]
    [InlineData("0,75", 0.75)]
    [InlineData("12", 12)]
    public void TryParse_AcceptsCommaAndDot(string text, double expected)
    {
        Assert.True(FlexibleNullableDoubleConverter.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не число")]
    public void TryParse_RejectsGarbage(string? text)
    {
        Assert.False(FlexibleNullableDoubleConverter.TryParse(text, out _));
    }

    [Theory]
    [InlineData("\"3,5\"", 3.5)]
    [InlineData("\"3.5\"", 3.5)]
    [InlineData("3.5", 3.5)]
    public void Read_AcceptsNumberAndString(string json, double expected)
    {
        var material = JsonSerializer.Deserialize<Material>($"{{\"Class\":\"Pipe\",\"Name\":\"Труба\",\"WallThicknessMm\":{json}}}");

        Assert.NotNull(material);
        Assert.Equal(expected, material!.WallThicknessMm);
    }

    [Fact]
    public void Read_KeepsNullAndUnparsableAsNull()
    {
        var withNull = JsonSerializer.Deserialize<Material>("{\"Name\":\"Труба\",\"WallThicknessMm\":null}");
        var withGarbage = JsonSerializer.Deserialize<Material>("{\"Name\":\"Труба\",\"WallThicknessMm\":\"толстая\"}");

        Assert.Null(withNull!.WallThicknessMm);
        Assert.Null(withGarbage!.WallThicknessMm);
    }

    [Fact]
    public void Write_AlwaysUsesInvariantForm()
    {
        // В json точка обязательна: запятая сделала бы файл нечитаемым
        // для любого другого парсера.
        using var _ = new CultureScope("ru-RU");

        var json = JsonSerializer.Serialize(TestData.Pipe(diameter: 88.9, wall: 3.5));

        Assert.Contains("88.9", json);
        Assert.Contains("3.5", json);
        Assert.DoesNotContain("88,9", json);
    }
}

/// <summary>
/// Реестр материалов: где ищется, откуда разворачивается и как защищён
/// от порчи. Тесты работают с настоящими файлами во временной папке.
/// </summary>
public class MaterialRepositoryTests
{
    [Fact]
    public void Load_SeedsUserRegistryFromCatalogWhenNothingExists()
    {
        using var temp = new TempDirectory();
        var repository = new MaterialRepository();

        repository.Load(new MaterialRegistryLocations { UserDataDirectory = temp.Path });

        Assert.Equal(MaterialRegistrySource.SeededFromCatalog, repository.Source);
        Assert.True(repository.WasCreatedFromSample);
        Assert.True(File.Exists(temp.Combine(MaterialRepository.FileName)));
        Assert.NotEmpty(repository.Materials);
    }

    [Fact]
    public void Load_SeedsFromTemplateWithoutTouchingIt()
    {
        // Шаблон лежит внутри bundle: плагин обязан его только читать,
        // иначе правки пользователя исчезли бы при обновлении версии.
        using var temp = new TempDirectory();
        var templatePath = temp.Combine("template.json");
        MaterialRepository.Save(new[] { TestData.Pipe() }, templatePath);
        var templateBefore = File.ReadAllText(templatePath);

        var userDirectory = temp.Combine("user");
        var repository = new MaterialRepository();
        repository.Load(new MaterialRegistryLocations
        {
            UserDataDirectory = userDirectory,
            TemplatePath = templatePath
        });

        Assert.Equal(MaterialRegistrySource.SeededFromTemplate, repository.Source);
        Assert.Single(repository.Materials);
        Assert.Equal(templateBefore, File.ReadAllText(templatePath));

        repository.Add(TestData.Cable());
        Assert.Equal(templateBefore, File.ReadAllText(templatePath));
    }

    [Fact]
    public void Load_PrefersRegistryNextToDrawing()
    {
        using var temp = new TempDirectory();
        var drawingDirectory = temp.Combine("dwg");
        var userDirectory = temp.Combine("user");
        MaterialRepository.Save(new[] { TestData.Pipe("Труба объекта") }, Path.Combine(drawingDirectory, MaterialRepository.FileName));
        MaterialRepository.Save(new[] { TestData.Pipe("Труба пользователя") }, Path.Combine(userDirectory, MaterialRepository.FileName));

        var repository = new MaterialRepository();
        repository.Load(new MaterialRegistryLocations
        {
            DrawingDirectory = drawingDirectory,
            UserDataDirectory = userDirectory
        });

        Assert.Equal(MaterialRegistrySource.DrawingFolder, repository.Source);
        Assert.Equal("Труба объекта", repository.Materials.Single().Name);
    }

    [Fact]
    public void Load_UsesUserRegistryWhenDrawingHasNone()
    {
        using var temp = new TempDirectory();
        var userDirectory = temp.Combine("user");
        MaterialRepository.Save(new[] { TestData.Pipe("Труба пользователя") }, Path.Combine(userDirectory, MaterialRepository.FileName));

        var repository = new MaterialRepository();
        repository.Load(new MaterialRegistryLocations
        {
            DrawingDirectory = temp.Combine("пустая папка чертежа"),
            UserDataDirectory = userDirectory
        });

        Assert.Equal(MaterialRegistrySource.UserData, repository.Source);
        Assert.False(repository.WasCreatedFromSample);
    }

    [Fact]
    public void Load_SkipsEntriesWithoutName()
    {
        // По наименованию строится вся навигация: журнал, слои, поиск.
        using var temp = new TempDirectory();
        File.WriteAllText(
            temp.Combine(MaterialRepository.FileName),
            "[{\"Class\":\"Pipe\",\"Name\":\"Труба\"},{\"Class\":\"Pipe\",\"Name\":\"\"}]");

        var repository = new MaterialRepository();
        repository.Load(new MaterialRegistryLocations { UserDataDirectory = temp.Path });

        Assert.Single(repository.Materials);
    }

    [Fact]
    public void Load_RequiresUserDataDirectory()
    {
        var repository = new MaterialRepository();

        Assert.Throws<ArgumentException>(() =>
            repository.Load(new MaterialRegistryLocations { UserDataDirectory = "   " }));
    }

    [Fact]
    public void Add_WritesMaterialToDisk()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);

        repository.Add(TestData.Cable());

        var reloaded = new MaterialRepository();
        reloaded.Load(new MaterialRegistryLocations { UserDataDirectory = temp.Path });

        var cable = reloaded.Materials.Single();
        Assert.Equal(TestData.Cable().Name, cable.Name);
        Assert.Equal(3, cable.CoreCount);
        Assert.Equal(2.5, cable.CrossSectionMm2);
    }

    [Fact]
    public void Add_RejectsDuplicateNameIgnoringCase()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);
        repository.Add(TestData.Pipe("Труба стальная ⌀89x4"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            repository.Add(TestData.Pipe("труба СТАЛЬНАЯ ⌀89x4")));

        Assert.Contains("уже есть в реестре", exception.Message);
        Assert.Single(repository.Materials);
    }

    [Fact]
    public void Add_RejectsEmptyNameOrUnit()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);

        Assert.Throws<InvalidOperationException>(() => repository.Add(TestData.Pipe(name: "   ")));
        Assert.Throws<InvalidOperationException>(() =>
            repository.Add(new Material { Class = MaterialClasses.Pipe, Name = "Труба", Unit = " " }));
    }

    [Fact]
    public void Add_TrimsNameBeforeStoring()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);

        var material = repository.Add(TestData.Pipe("  Труба с пробелами  "));

        Assert.Equal("Труба с пробелами", material.Name);
    }

    [Fact]
    public void Add_RequiresLoadedRegistry()
    {
        var repository = new MaterialRepository();

        Assert.Throws<InvalidOperationException>(() => repository.Add(TestData.Pipe()));
    }

    [Fact]
    public void Remove_DeletesMaterialFromFile()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);
        var pipe = repository.Add(TestData.Pipe());
        repository.Add(TestData.Cable());

        Assert.True(repository.Remove(pipe));

        var reloaded = new MaterialRepository();
        reloaded.Load(new MaterialRegistryLocations { UserDataDirectory = temp.Path });

        Assert.Equal(TestData.Cable().Name, reloaded.Materials.Single().Name);
    }

    [Fact]
    public void Remove_ReturnsFalseForUnknownMaterial()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);

        Assert.False(repository.Remove(TestData.Pipe("Материала нет в реестре")));
    }

    [Fact]
    public void Reload_PicksUpExternalChanges()
    {
        // Кнопка «Обновить реестр материалов» нужна как раз для случая,
        // когда materials.json правили в блокноте при открытом AutoCAD.
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);
        repository.Add(TestData.Pipe());

        MaterialRepository.Save(new[] { TestData.Pipe(), TestData.Cable() }, temp.Combine(MaterialRepository.FileName));
        repository.Reload();

        Assert.Equal(2, repository.Materials.Count);
    }

    [Fact]
    public void Changed_FiresOnLoadAddAndRemove()
    {
        using var temp = new TempDirectory();
        var repository = new MaterialRepository();
        var count = 0;
        repository.Changed += (_, _) => count++;

        repository.Load(new MaterialRegistryLocations { UserDataDirectory = temp.Path, TemplatePath = EmptyTemplate(temp) });
        var pipe = repository.Add(TestData.Pipe());
        repository.Remove(pipe);

        Assert.Equal(3, count);
    }

    [Fact]
    public void GetByClass_ReturnsOnlyThatClassSortedByName()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);
        repository.Add(TestData.Pipe("Труба Я"));
        repository.Add(TestData.Pipe("Труба А"));
        repository.Add(TestData.Cable());

        var pipes = repository.GetByClass(MaterialClasses.Pipe);

        Assert.Equal(new[] { "Труба А", "Труба Я" }, pipes.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void FindByName_IgnoresCaseAndSpaces()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);
        repository.Add(TestData.Pipe("Труба стальная ⌀89x4"));

        Assert.NotNull(repository.FindByName("  труба СТАЛЬНАЯ ⌀89x4  "));
        Assert.Null(repository.FindByName("Труба алюминиевая"));
        Assert.True(repository.NameExists("труба стальная ⌀89x4"));
    }

    [Fact]
    public void NameExistsExcept_AllowsMaterialToKeepItsOwnName()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmpty(temp);
        var pipe = repository.Add(TestData.Pipe());

        Assert.False(repository.NameExistsExcept(pipe.Name, pipe));
        Assert.True(repository.NameExistsExcept(pipe.Name, TestData.Cable()));
    }

    [Fact]
    public void Duplicate_CopiesEveryCharacteristic()
    {
        // Копия — заготовка для новой позиции: потеря характеристик означала бы
        // молча испорченный материал (и другой слой у копии).
        var cable = TestData.Cable();
        var cableCopy = MaterialRepository.Duplicate(cable);

        Assert.Equal(cable.Class, cableCopy.Class);
        Assert.Equal(cable.Name, cableCopy.Name);
        Assert.Equal(cable.Unit, cableCopy.Unit);
        Assert.Equal(cable.CoreCount, cableCopy.CoreCount);
        Assert.Equal(cable.CrossSectionMm2, cableCopy.CrossSectionMm2);

        var piece = TestData.Piece();
        var pieceCopy = MaterialRepository.Duplicate(piece);

        Assert.Equal(piece.NominalDiameterMm, pieceCopy.NominalDiameterMm);
        Assert.Equal(piece.PieceKind, pieceCopy.PieceKind);

        var duct = TestData.RectDuct();
        var ductCopy = MaterialRepository.Duplicate(duct);

        Assert.Equal(duct.WidthMm, ductCopy.WidthMm);
        Assert.Equal(duct.HeightMm, ductCopy.HeightMm);
        Assert.Equal(duct.SheetThicknessMm, ductCopy.SheetThicknessMm);
    }

    [Fact]
    public void Duplicate_ProducesIndependentObject()
    {
        var pipe = TestData.Pipe();
        var copy = MaterialRepository.Duplicate(pipe);

        copy.Name = "Другая труба";

        Assert.NotSame(pipe, copy);
        Assert.Equal("Труба стальная ⌀89x4", pipe.Name);
    }

    private static MaterialRepository LoadEmpty(TempDirectory temp)
    {
        var repository = new MaterialRepository();
        repository.Load(new MaterialRegistryLocations
        {
            UserDataDirectory = temp.Path,
            TemplatePath = EmptyTemplate(temp)
        });

        return repository;
    }

    /// <summary>Пустой шаблон: иначе каждый тест разворачивал бы каталог на 558 позиций.</summary>
    private static string EmptyTemplate(TempDirectory temp)
    {
        var path = temp.Combine("empty-template.json");
        if (!File.Exists(path)) MaterialRepository.Save(Array.Empty<Material>(), path);

        return path;
    }
}

/// <summary>
/// Встроенная номенклатура. Реестр разворачивается из неё при первом запуске,
/// поэтому её состав — часть поведения продукта.
/// </summary>
public class MaterialCatalogTests
{
    [Fact]
    public void CreateDefault_ContainsDocumentedNumberOfPositions()
    {
        Assert.Equal(558, MaterialCatalog.CreateDefault().Count);
    }

    [Theory]
    [InlineData(MaterialClasses.Pipe, 120)]
    [InlineData(MaterialClasses.Duct, 62)]
    [InlineData(MaterialClasses.Cable, 117)]
    [InlineData(MaterialClasses.Piece, 259)]
    public void CreateDefault_CoversEveryClass(string materialClass, int expectedCount)
    {
        var catalog = MaterialCatalog.CreateDefault();

        Assert.Equal(expectedCount, catalog.Count(m => m.Class == materialClass));
    }

    [Fact]
    public void CreateDefault_HasUniqueNames()
    {
        // Наименование — ключ материала: дубликат означал бы две позиции,
        // делящие одну строку журнала.
        var names = MaterialCatalog.CreateDefault().Select(m => m.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CreateDefault_FillsNameAndUnitEverywhere()
    {
        Assert.All(MaterialCatalog.CreateDefault(), m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.False(string.IsNullOrWhiteSpace(m.Unit));
        });
    }

    [Fact]
    public void CreateDefault_GivesEveryMaterialItsOwnValidLayerName()
    {
        // Самая ценная проверка каталога: 558 позиций должны разойтись
        // по 558 разным слоям, и каждое имя обязано быть валидным для AutoCAD.
        var catalog = MaterialCatalog.CreateDefault();
        var factory = new LayerNameFactory();
        factory.SyncWithRegistry(catalog);

        var layerNames = catalog.Select(factory.GetBaseName).ToList();

        Assert.All(layerNames, n => Assert.True(LayerNameSanitizer.IsValidLayerName(n), n));
        Assert.Equal(catalog.Count, layerNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CreateDefault_LayerNamesResolveBackToMaterial()
    {
        var catalog = MaterialCatalog.CreateDefault();
        var factory = new LayerNameFactory();
        factory.SyncWithRegistry(catalog);

        Assert.All(catalog, material =>
        {
            var layerName = factory.GetLayerName(material, "Этаж 1");

            Assert.True(factory.TryResolveLayer(layerName, out var resolved, out var section));
            Assert.Same(material, resolved);
            Assert.Equal("Этаж 1", section);
        });
    }

    [Fact]
    public void CreateDefault_PiecesCarryKind()
    {
        var pieces = MaterialCatalog.CreateDefault().Where(m => m.Class == MaterialClasses.Piece);

        Assert.All(pieces, p => Assert.False(string.IsNullOrWhiteSpace(p.PieceKind)));
    }
}
