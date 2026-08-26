using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Видимость замерных слоёв. Главное здесь — не трогать проектные слои:
/// выключенный по ошибке слой чужого чертежа пользователь заметит не сразу,
/// а поймёт причину ещё позже.
/// </summary>
public class MeasurementLayerVisibilityTests
{
    /// <summary>Слои чертежа: два замерных плагина и три обычных проектных.</summary>
    private static (LayerNameFactory Factory, string[] Layers, string PipeLayer, string CableLayer) BuildDrawing()
    {
        var factory = new LayerNameFactory();
        var pipe = TestData.Pipe();
        var cable = TestData.Cable();
        factory.SyncWithRegistry(new[] { pipe, cable });

        var pipeLayer = factory.GetLayerName(pipe, "Этаж 1");
        var cableLayer = factory.GetLayerName(cable, "Этаж 1");

        var layers = new[] { "0", "Оси", pipeLayer, "А-Стены", cableLayer };

        return (factory, layers, pipeLayer, cableLayer);
    }

    [Fact]
    public void SelectMeasurementLayers_RecognisesOnlyPluginLayers()
    {
        var (factory, layers, pipeLayer, cableLayer) = BuildDrawing();

        var measurement = MeasurementLayerVisibility.SelectMeasurementLayers(layers, factory);

        Assert.Equal(new[] { pipeLayer, cableLayer }, measurement);
    }

    [Fact]
    public void PlanIsolation_KeepsSelectedLayerAndTurnsOffOtherMeasurementLayers()
    {
        var (factory, layers, pipeLayer, cableLayer) = BuildDrawing();

        var plan = MeasurementLayerVisibility.PlanIsolation(layers, factory, pipeLayer);

        Assert.Equal(new[] { pipeLayer }, plan.TurnOn);
        Assert.Equal(new[] { cableLayer }, plan.TurnOff);
    }

    [Fact]
    public void PlanIsolation_NeverTouchesProjectLayers()
    {
        // Проектные слои не должны попасть ни в один список: план — это
        // исчерпывающий перечень того, что плагин намерен изменить.
        var (factory, layers, pipeLayer, _) = BuildDrawing();

        var plan = MeasurementLayerVisibility.PlanIsolation(layers, factory, pipeLayer);
        var touched = plan.TurnOn.Concat(plan.TurnOff).ToList();

        Assert.DoesNotContain("0", touched);
        Assert.DoesNotContain("Оси", touched);
        Assert.DoesNotContain("А-Стены", touched);
    }

    [Fact]
    public void PlanIsolation_IgnoresRequestForNonMeasurementLayer()
    {
        // Просить изолировать проектный слой нельзя: тогда выключились бы
        // все замерные, а показать было бы нечего.
        var (factory, layers, _, _) = BuildDrawing();

        var plan = MeasurementLayerVisibility.PlanIsolation(layers, factory, "А-Стены");

        Assert.Empty(plan.TurnOn);
        Assert.Equal(2, plan.TurnOff.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PlanIsolation_WithoutSelectionDoesNothing(string? selected)
    {
        var (factory, layers, _, _) = BuildDrawing();

        Assert.True(MeasurementLayerVisibility.PlanIsolation(layers, factory, selected).IsEmpty);
    }

    [Fact]
    public void PlanShowAll_TurnsOnEveryMeasurementLayerAndNothingElse()
    {
        var (factory, layers, pipeLayer, cableLayer) = BuildDrawing();

        var plan = MeasurementLayerVisibility.PlanShowAll(layers, factory);

        Assert.Equal(new[] { pipeLayer, cableLayer }, plan.TurnOn);
        Assert.Empty(plan.TurnOff);
    }

    [Fact]
    public void EnableOnlyMeasurement_TurnsOffProjectLayersAndShowsPluginLayers()
    {
        // Режим галочки: в отличие от изоляции, проектные слои здесь гасятся —
        // в этом и смысл, — а все замерные остаются видимыми.
        var (factory, layers, pipeLayer, cableLayer) = BuildDrawing();

        var plan = MeasurementLayerVisibility.PlanEnableOnlyMeasurement(layers, Array.Empty<string>(), factory);

        Assert.Equal(new[] { pipeLayer, cableLayer }, plan.TurnOn);
        Assert.Equal(new[] { "0", "Оси", "А-Стены" }, plan.TurnOff);
    }

    [Fact]
    public void EnableOnlyMeasurement_DoesNotClaimLayersUserAlreadyHidUnaided()
    {
        // Слой, выключенный пользователем до режима, режим не считает своим —
        // иначе выход из режима «чинил» бы чертёж по-своему.
        var (factory, layers, _, _) = BuildDrawing();

        var plan = MeasurementLayerVisibility.PlanEnableOnlyMeasurement(layers, new[] { "Оси" }, factory);

        Assert.Equal(new[] { "0", "А-Стены" }, plan.TurnOff);
    }

    [Fact]
    public void DisableMode_TurnsBackOnExactlyWhatModeHid()
    {
        var (factory, layers, _, _) = BuildDrawing();
        var enable = MeasurementLayerVisibility.PlanEnableOnlyMeasurement(layers, Array.Empty<string>(), factory);

        var disable = MeasurementLayerVisibility.PlanDisableMode(enable.TurnOff);

        Assert.Equal(enable.TurnOff, disable.TurnOn);
        Assert.Empty(disable.TurnOff);
    }

    [Fact]
    public void EnableCurrentLayerOnly_HidesOtherMeasurementLayersOnly()
    {
        var (factory, layers, pipeLayer, cableLayer) = BuildDrawing();

        var plan = MeasurementLayerVisibility.PlanEnableCurrentLayerOnly(
            layers, Array.Empty<string>(), factory, pipeLayer);

        Assert.Equal(new[] { pipeLayer }, plan.TurnOn);
        Assert.Equal(new[] { cableLayer }, plan.TurnOff);

        // Проектные слои — забота другой галочки.
        Assert.DoesNotContain("0", plan.TurnOff);
        Assert.DoesNotContain("А-Стены", plan.TurnOff);
    }

    [Fact]
    public void EnableCurrentLayerOnly_WithoutSelectedLayerDoesNothing()
    {
        var (factory, layers, _, _) = BuildDrawing();

        Assert.True(MeasurementLayerVisibility
            .PlanEnableCurrentLayerOnly(layers, Array.Empty<string>(), factory, null)
            .IsEmpty);
    }

    [Fact]
    public void BothModesTogether_LeaveExactlyOneLayerVisible()
    {
        // Обе галочки: проектные слои гасит первая, замерные — вторая.
        // Приоритет у «только слой текущего замера»: виден ровно один слой.
        var (factory, layers, pipeLayer, cableLayer) = BuildDrawing();

        var measurementOnly = MeasurementLayerVisibility.PlanEnableOnlyMeasurement(
            layers, Array.Empty<string>(), factory);

        var currentOnly = MeasurementLayerVisibility.PlanEnableCurrentLayerOnly(
            layers, measurementOnly.TurnOff, factory, pipeLayer);

        var hidden = measurementOnly.TurnOff.Concat(currentOnly.TurnOff).ToList();

        Assert.Contains(cableLayer, hidden);
        Assert.Contains("0", hidden);
        Assert.DoesNotContain(pipeLayer, hidden);
        Assert.Equal(new[] { pipeLayer }, currentOnly.TurnOn);

        // Снятие второй галочки возвращает замерные слои, проектные остаются
        // выключенными первой — режимы не мешают друг другу.
        var back = MeasurementLayerVisibility.PlanDisableMode(currentOnly.TurnOff);
        Assert.Equal(new[] { cableLayer }, back.TurnOn);
        Assert.DoesNotContain("0", back.TurnOn);
    }

    [Fact]
    public void PlanShowAll_OnDrawingWithoutMeasurementLayersIsEmpty()
    {
        var factory = new LayerNameFactory();
        factory.SyncWithRegistry(new[] { TestData.Pipe() });

        Assert.True(MeasurementLayerVisibility.PlanShowAll(new[] { "0", "Оси" }, factory).IsEmpty);
    }
}

/// <summary>
/// Удаление спецификации: связь с проектом снимается, работа остаётся.
/// </summary>
public class SpecificationRemovalTests
{
    private const string Drawing = "Корпус-1.dwg";

    private static (MeasurementJournal Journal, Specification Specification, MeasurementRecord Measured) BuildCase()
    {
        var specification = new Specification
        {
            FileName = "спецификация.xlsx",
            Items = new[]
            {
                new SpecificationItem
                {
                    Number = 7,
                    Name = "Труба стальная Dn80",
                    Mark = "ГОСТ 10704",
                    EquipmentCode = "ОВ-12",
                    Manufacturer = "ЧТПЗ",
                    Unit = "м.п.",
                    Quantity = 200
                }
            }
        };

        var journal = new MeasurementJournal();
        var measured = journal.AddOrUpdateLinear(
            TestData.Pipe("Труба стальная Dn80"), "Этаж 1", "PIPE_D89x4_Этаж 1", 120, 5, 4, Drawing);

        MeasurementJournal.BindToSpecification(measured, specification.Items[0], specification.FileName);

        return (journal, specification, measured);
    }

    [Fact]
    public void ClearSpecificationBindings_KeepsRecordsAndMeasurements()
    {
        var (journal, _, measured) = BuildCase();

        var unbound = journal.ClearSpecificationBindings();

        Assert.Equal(1, unbound);
        Assert.Single(journal.Records);

        // Замер, материал, участок, слой и чертёж — всё на месте.
        Assert.Equal("Труба стальная Dn80", measured.MaterialName);
        Assert.Equal(125, measured.LengthM);
        Assert.Equal(4, measured.PolylineCount);
        Assert.Equal("Этаж 1", measured.Section);
        Assert.Equal("PIPE_D89x4_Этаж 1", measured.LayerName);
        Assert.Equal(Drawing, measured.DrawingFileName);
    }

    [Fact]
    public void ClearSpecificationBindings_ClearsOnlySpecificationFields()
    {
        var (journal, _, measured) = BuildCase();
        measured.SpecificationEditedManually = true;
        measured.MaterialMissing = true;

        journal.ClearSpecificationBindings();

        Assert.False(measured.IsFromSpecification);
        Assert.Null(measured.SpecificationItemId);
        Assert.Null(measured.SpecificationQuantity);
        Assert.Null(measured.SpecificationDifference);
        Assert.Equal(string.Empty, measured.SpecificationFileName);
        Assert.Equal(string.Empty, measured.Mark);
        Assert.Equal(string.Empty, measured.EquipmentCode);
        Assert.Equal(string.Empty, measured.Manufacturer);
        Assert.False(measured.SpecificationEditedManually);
        Assert.False(measured.MaterialMissing);

        // Единица измерения — поле журнала, а не спецификации: она заполняется
        // из реестра при каждом пересчёте и обнулению не подлежит.
        Assert.Equal("м.п.", measured.Unit);
    }

    [Fact]
    public void JournalWithoutSpecification_KeepsMeasuring()
    {
        var (journal, _, measured) = BuildCase();
        journal.ClearSpecificationBindings();

        // Пересчёт по чертежу обновляет ту же строку, а не плодит новую.
        var again = journal.AddOrUpdateLinear(
            TestData.Pipe("Труба стальная Dn80"), "Этаж 1", "PIPE_D89x4_Этаж 1", 140, 0, 6, Drawing);

        Assert.Same(measured, again);
        Assert.Equal(140, again.LengthM);

        // И новые записи создаются как обычно.
        journal.AddOrUpdateLinear(TestData.Cable(), "Этаж 1", "CABLE_3x2.5_Этаж 1", 30, 0, 1, Drawing);
        Assert.Equal(2, journal.Records.Count);
    }

    [Fact]
    public void LoadingAnotherSpecification_AfterClearRebindsByMaterialName()
    {
        // Так работает единственная команда «Загрузить спецификацию»: сначала
        // снимаются старые привязки, потом связывается новый файл.
        var (journal, _, measured) = BuildCase();
        journal.ClearSpecificationBindings();

        var replacement = new Specification
        {
            FileName = "спецификация-ред2.xlsx",
            Items = new[]
            {
                new SpecificationItem { Number = 3, Name = "Труба стальная Dn80", Unit = "м.п.", Quantity = 260 }
            }
        };

        var item = replacement.FindByName(measured.MaterialName);
        Assert.NotNull(item);
        MeasurementJournal.BindToSpecification(measured, item!, replacement.FileName);

        // Замер пережил замену файла, а номер позиции стал новым.
        Assert.Equal(125, measured.LengthM);
        Assert.Equal(3, measured.SpecificationItemId);
        Assert.Equal(260, measured.SpecificationQuantity);
        Assert.Equal("спецификация-ред2.xlsx", measured.SpecificationFileName);
    }
}
