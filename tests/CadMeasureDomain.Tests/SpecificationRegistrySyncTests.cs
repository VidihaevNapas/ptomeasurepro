using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Заведение материалов по спецификации.
///
/// Смысл: спецификацию должно быть можно замерять сразу после импорта,
/// не заводя сотни позиций реестра руками. При этом реестр правит человек,
/// и спецификация не вправе затирать его работу.
/// </summary>
public class SpecificationRegistrySyncTests
{
    private static Specification BuildSpecification(params SpecificationItem[] items) => new()
    {
        FileName = "спецификация.xlsx",
        Items = items
    };

    private static SpecificationItem Item(
        int number,
        string name,
        string unit = "м.п.",
        double quantity = 100,
        string mark = "",
        string manufacturer = "") =>
        new()
        {
            Number = number,
            Name = name,
            Unit = unit,
            Quantity = quantity,
            Mark = mark,
            Manufacturer = manufacturer
        };

    private static MaterialRepository LoadEmptyRepository(TempDirectory temp)
    {
        var templatePath = temp.Combine("template.json");
        MaterialRepository.Save(Array.Empty<Material>(), templatePath);

        var repository = new MaterialRepository();
        repository.Load(new MaterialRegistryLocations
        {
            UserDataDirectory = temp.Combine("user"),
            TemplatePath = templatePath
        });

        return repository;
    }

    [Fact]
    public void MissingMaterial_IsCreatedAndUsed()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);
        var specification = BuildSpecification(
            Item(1, "Труба стальная Dn80", "м.п.", 200, "ГОСТ 10704-91", "ЧТПЗ"),
            Item(2, "Кран шаровой Dn80", "шт.", 6));

        var result = SpecificationRegistrySync.EnsureMaterials(repository, specification);

        Assert.Equal(2, result.Created.Count);
        Assert.Equal(2, repository.Materials.Count);

        var pipe = repository.FindByName("Труба стальная Dn80");
        Assert.NotNull(pipe);
        Assert.Equal(MaterialClasses.Pipe, pipe!.Class);
        Assert.Equal("м.п.", pipe.Unit);
        Assert.Equal("ГОСТ 10704-91", pipe.Mark);
        Assert.Equal("ЧТПЗ", pipe.Manufacturer);

        // Штучная позиция определяется по единице измерения.
        Assert.Equal(MaterialClasses.Piece, repository.FindByName("Кран шаровой Dn80")!.Class);

        // Позиции знают свой материал реестра.
        Assert.Equal("Труба стальная Dn80", specification.Items[0].MaterialName);
        Assert.True(specification.Items[1].HasMaterial);
    }

    [Fact]
    public void CreatedMaterials_ArePersistedInOneWrite()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);

        SpecificationRegistrySync.EnsureMaterials(
            repository,
            BuildSpecification(Item(1, "Труба А"), Item(2, "Труба Б")));

        var reloaded = new MaterialRepository();
        reloaded.Load(new MaterialRegistryLocations { UserDataDirectory = temp.Combine("user") });

        Assert.Equal(2, reloaded.Materials.Count);
    }

    [Fact]
    public void ExistingMaterial_IsReusedAndNeverModified()
    {
        // Пользователь завёл трубу руками, с классом и характеристиками.
        // Спецификация обязана взять её как есть.
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);
        var existing = repository.Add(TestData.RectDuct("Труба стальная Dn80"));

        var specification = BuildSpecification(Item(1, "Труба стальная Dn80", "шт.", 5, "другая марка"));
        var result = SpecificationRegistrySync.EnsureMaterials(repository, specification);

        Assert.Empty(result.Created);
        Assert.Single(repository.Materials);

        // Ни класс, ни единица, ни характеристики не изменились.
        Assert.Equal(MaterialClasses.Duct, existing.Class);
        Assert.Equal("м.п.", existing.Unit);
        Assert.Equal(1250, existing.WidthMm);
        Assert.Null(existing.Mark);
    }

    [Fact]
    public void ExistingMaterial_IsMatchedIgnoringCaseAndSpaces()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);
        repository.Add(TestData.Pipe("Труба стальная Dn80"));

        var result = SpecificationRegistrySync.EnsureMaterials(
            repository,
            BuildSpecification(Item(1, "  труба СТАЛЬНАЯ Dn80  ")));

        Assert.Empty(result.Created);
        Assert.Single(result.Matched);
        Assert.Single(repository.Materials);
    }

    [Fact]
    public void UnsupportedUnit_DoesNotPolluteRegistry()
    {
        // «м2» замерить нечем: заводить под него материал — засорять реестр
        // строкой, которой никто не воспользуется.
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);

        var result = SpecificationRegistrySync.EnsureMaterials(
            repository,
            BuildSpecification(Item(1, "Изоляция", "м2", 40)));

        Assert.Empty(result.Created);
        Assert.Single(result.Skipped);
        Assert.Empty(repository.Materials);
        Assert.False(result.Skipped[0].HasMaterial);
    }

    [Fact]
    public void DuplicateNamesInOneFile_CreateSingleMaterial()
    {
        // Одна и та же позиция в двух разделах спецификации — обычное дело.
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);

        var result = SpecificationRegistrySync.EnsureMaterials(
            repository,
            BuildSpecification(Item(1, "Труба стальная Dn80"), Item(2, "Труба стальная Dn80")));

        Assert.Single(result.Created);
        Assert.Single(repository.Materials);
    }

    [Fact]
    public void Log_NamesEveryCreatedMaterial()
    {
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);

        var result = SpecificationRegistrySync.EnsureMaterials(
            repository,
            BuildSpecification(Item(1, "Труба стальная Dn80", "м.п.")));

        Assert.Equal("Добавлен материал из спецификации: Труба стальная Dn80 (м.п.)", result.Log.Single());
    }

    [Fact]
    public void CreatedMaterial_GetsUsableLayerName()
    {
        // Характеристик у такой позиции нет, поэтому имя слоя должно
        // собраться по запасному шаблону — иначе замерять её нечем.
        using var temp = new TempDirectory();
        var repository = LoadEmptyRepository(temp);

        SpecificationRegistrySync.EnsureMaterials(
            repository,
            BuildSpecification(Item(1, "Труба стальная Dn80")));

        var factory = new LayerNameFactory();
        factory.SyncWithRegistry(repository.Materials);

        var layerName = factory.GetLayerName(repository.Materials[0], "Этаж 1");

        Assert.StartsWith("PIPE_N", layerName);
        Assert.True(LayerNameSanitizer.IsValidLayerName(layerName));
    }
}
