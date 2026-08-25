using CadMeasureDomain.Models;
using CadMeasureDomain.Services;
using ClosedXML.Excel;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Разбор единицы измерения. Единицу пишет человек, поэтому вариантов
/// написания много, а способов замера всего два.
/// </summary>
public class UnitOfMeasureTests
{
    [Theory]
    [InlineData("шт")]
    [InlineData("шт.")]
    [InlineData("ШТ.")]
    [InlineData("штук")]
    [InlineData(" штука ")]
    public void Pieces_AreCounted(string unit)
    {
        Assert.Equal(MeasurementType.Pieces, UnitOfMeasure.Parse(unit));
    }

    [Theory]
    [InlineData("м")]
    [InlineData("м.п.")]
    [InlineData("мп")]
    [InlineData("м.п")]
    [InlineData("пог.м")]
    [InlineData("пог. м")]
    [InlineData("погонный метр")]
    [InlineData("Погонный Метр")]
    public void LinearUnits_AreMeasuredByPolylines(string unit)
    {
        Assert.Equal(MeasurementType.Linear, UnitOfMeasure.Parse(unit));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("кг")]
    [InlineData("компл.")]
    [InlineData("м2")]
    [InlineData("м3")]
    public void UnknownUnits_AreNotSupported(string? unit)
    {
        // Угадывать способ замера для «кг» нельзя — такую позицию
        // помечаем неподдерживаемой и не пытаемся мерить.
        Assert.Equal(MeasurementType.Unsupported, UnitOfMeasure.Parse(unit));
        Assert.False(UnitOfMeasure.IsSupported(unit));
    }
}

/// <summary>Импорт первоначальной спецификации из .xlsx.</summary>
public class SpecificationImporterTests
{
    /// <summary>Спецификация в том виде, в каком её присылает проектировщик: с шапкой.</summary>
    private static string CreateSpecificationFile(TempDirectory temp, params string[][] rows)
    {
        var path = temp.Combine("спецификация.xlsx");

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Спецификация");

        sheet.Cell(1, 1).Value = "п/п";
        sheet.Cell(1, 2).Value = "Наименование материала";
        sheet.Cell(1, 3).Value = "Марка";
        sheet.Cell(1, 4).Value = "Код оборудования";
        sheet.Cell(1, 5).Value = "Изготовитель";
        sheet.Cell(1, 6).Value = "Ед. изм.";
        sheet.Cell(1, 7).Value = "Кол-во";

        var row = 2;
        foreach (var line in rows)
        {
            for (var column = 0; column < line.Length; column++)
                sheet.Cell(row, column + 1).Value = line[column];

            row++;
        }

        workbook.SaveAs(path);
        return path;
    }

    [Fact]
    public void Import_ReadsEveryColumnAndNumbersRows()
    {
        using var temp = new TempDirectory();
        var path = CreateSpecificationFile(
            temp,
            new[] { "1", "Труба стальная Dn80", "ГОСТ 10704-91", "ОБ-01", "ЧТПЗ", "м.п.", "125,5" },
            new[] { "2", "Кран шаровой Dn80", "БП-40", "ОБ-02", "Danfoss", "шт.", "6" });

        var specification = SpecificationImporter.Import(path);

        Assert.Equal("спецификация.xlsx", specification.FileName);
        Assert.Equal(2, specification.Items.Count);

        var pipe = specification.Items[0];
        Assert.Equal(1, pipe.Number);
        Assert.Equal("Труба стальная Dn80", pipe.Name);
        Assert.Equal("ГОСТ 10704-91", pipe.Mark);
        Assert.Equal("ОБ-01", pipe.EquipmentCode);
        Assert.Equal("ЧТПЗ", pipe.Manufacturer);
        Assert.Equal("м.п.", pipe.Unit);
        Assert.Equal(125.5, pipe.Quantity);
        Assert.Equal(MeasurementType.Linear, pipe.MeasurementType);

        var valve = specification.Items[1];
        Assert.Equal(2, valve.Number);
        Assert.Equal(6, valve.Quantity);
        Assert.Equal(MeasurementType.Pieces, valve.MeasurementType);
    }

    [Fact]
    public void Import_NumbersRowsItselfIgnoringFileNumbering()
    {
        // В присланных файлах нумерация бывает с пропусками и буквами,
        // а номер позиции — ключ привязки журнала, он должен быть сплошным.
        using var temp = new TempDirectory();
        var path = CreateSpecificationFile(
            temp,
            new[] { "1а", "Труба", "", "", "", "м", "10" },
            new[] { "7", "Кран", "", "", "", "шт", "2" });

        var specification = SpecificationImporter.Import(path);

        Assert.Equal(new[] { 1, 2 }, specification.Items.Select(i => i.Number).ToArray());
    }

    [Fact]
    public void Import_SkipsHeaderAndSectionRows()
    {
        using var temp = new TempDirectory();
        var path = CreateSpecificationFile(
            temp,
            new[] { "", "Раздел 1. Трубопроводы", "", "", "", "", "" },
            new[] { "1", "Труба", "", "", "", "м", "10" },
            new[] { "", "", "", "", "", "", "" });

        var specification = SpecificationImporter.Import(path);

        Assert.Single(specification.Items);

        // Шапка и подзаголовок раздела посчитаны: молча потерянных строк быть
        // не должно. Полностью пустая строка в счёт не идёт — её не существует
        // с точки зрения файла.
        Assert.Equal(2, specification.SkippedRows);
    }

    [Fact]
    public void Import_KeepsUnsupportedUnitsVisible()
    {
        using var temp = new TempDirectory();
        var path = CreateSpecificationFile(
            temp,
            new[] { "1", "Труба", "", "", "", "м", "10" },
            new[] { "2", "Изоляция", "", "", "", "м2", "40" });

        var specification = SpecificationImporter.Import(path);

        // Позиция остаётся в спецификации, но помечена как незамеряемая.
        Assert.Equal(2, specification.Items.Count);
        Assert.Equal("Изоляция", specification.UnsupportedItems.Single().Name);
    }

    [Fact]
    public void Import_RejectsMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() => SpecificationImporter.Import("нет-такого-файла.xlsx"));
        Assert.Throws<ArgumentException>(() => SpecificationImporter.Import("  "));
    }

    [Fact]
    public void FindByName_MatchesRegistryMaterialIgnoringCase()
    {
        using var temp = new TempDirectory();
        var path = CreateSpecificationFile(temp, new[] { "1", "Труба стальная Dn80", "", "", "", "м", "10" });

        var specification = SpecificationImporter.Import(path);

        Assert.NotNull(specification.FindByName("  труба СТАЛЬНАЯ Dn80 "));
        Assert.Null(specification.FindByName("Труба медная"));
    }
}

/// <summary>
/// Свод «спецификация × чертежи»: столбец подсчёта на каждый DWG,
/// в котором позицию замеряли.
/// </summary>
public class SpecificationSummaryTests
{
    private const string FirstDrawing = "Корпус-1.dwg";
    private const string SecondDrawing = "Корпус-2.dwg";

    private static Specification BuildSpecification() => new()
    {
        FileName = "спецификация.xlsx",
        Items = new[]
        {
            new SpecificationItem { Number = 1, Name = "Труба стальная Dn80", Unit = "м.п.", Quantity = 200 },
            new SpecificationItem { Number = 2, Name = "Кран шаровой Dn80", Unit = "шт.", Quantity = 10 },
            new SpecificationItem { Number = 3, Name = "Изоляция", Unit = "м2", Quantity = 40 }
        }
    };

    [Fact]
    public void AddFromSpecification_CreatesRecordBoundToItem()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();

        var record = journal.AddFromSpecification(
            specification.Items[0], specification.FileName, FirstDrawing, material: null);

        Assert.True(record.IsFromSpecification);
        Assert.Equal(1, record.SpecificationItemId);
        Assert.Equal("спецификация.xlsx", record.SpecificationFileName);
        Assert.Equal(200, record.SpecificationQuantity);
        Assert.Equal("м.п.", record.Unit);
        Assert.Equal(-200, record.SpecificationDifference);
    }

    [Fact]
    public void AddFromSpecification_TakesMeasurementTypeFromUnitWhenMaterialIsUnknown()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();

        var pipe = journal.AddFromSpecification(specification.Items[0], specification.FileName, FirstDrawing, null);
        var valve = journal.AddFromSpecification(specification.Items[1], specification.FileName, FirstDrawing, null);

        Assert.False(pipe.IsPiece);
        Assert.True(valve.IsPiece);
    }

    [Fact]
    public void AddFromSpecification_UsesRegistryMaterialWhenItMatched()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var material = TestData.RectDuct("Труба стальная Dn80");

        var record = journal.AddFromSpecification(
            specification.Items[0], specification.FileName, FirstDrawing, material);

        Assert.Equal(MaterialClasses.Duct, record.MaterialClass);
        Assert.Equal(material.Characteristic, record.Characteristic);
    }

    [Fact]
    public void SecondDrawing_AddsItsOwnCountColumn()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        var first = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 120, 0, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(first, specification.Items[0], specification.FileName);

        Assert.Equal(new[] { FirstDrawing }, SpecificationSummaryBuilder.GetDrawingColumns(journal));

        // Переход в другой чертёж без закрытия AutoCAD — столбец появляется сам.
        var second = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 55, 0, 2, SecondDrawing);
        MeasurementJournal.BindToSpecification(second, specification.Items[0], specification.FileName);

        Assert.Equal(new[] { FirstDrawing, SecondDrawing }, SpecificationSummaryBuilder.GetDrawingColumns(journal));

        var row = SpecificationSummaryBuilder.Build(journal, specification).First();
        Assert.Equal(120, row.ByDrawing[FirstDrawing]);
        Assert.Equal(55, row.ByDrawing[SecondDrawing]);
        Assert.Equal(175, row.Total);
        Assert.Equal(-25, row.Difference);
    }

    [Fact]
    public void RepeatedMeasurement_UpdatesItsOwnColumnOnly()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        var first = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 120, 0, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(first, specification.Items[0], specification.FileName);
        var second = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 55, 0, 2, SecondDrawing);
        MeasurementJournal.BindToSpecification(second, specification.Items[0], specification.FileName);

        // Дочертили во втором чертеже: меняется только его столбец.
        journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 70, 0, 3, SecondDrawing);

        var row = SpecificationSummaryBuilder.Build(journal, specification).First();
        Assert.Equal(120, row.ByDrawing[FirstDrawing]);
        Assert.Equal(70, row.ByDrawing[SecondDrawing]);
        Assert.Equal(190, row.Total);
    }

    [Fact]
    public void Build_KeepsUnmeasuredItemsWithZeroes()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();

        var rows = SpecificationSummaryBuilder.Build(journal, specification);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsMeasured));
        Assert.All(rows, r => Assert.Equal(0, r.Total));
        Assert.Equal(-200, rows[0].Difference);
    }

    [Fact]
    public void Build_SumsSectionsOfOneItemWithinDrawing()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        foreach (var section in new[] { "Этаж 1", "Этаж 2" })
        {
            var record = journal.AddOrUpdateLinear(pipe, section, $"PIPE_D89x4_{section}", 60.5, 0, 2, FirstDrawing);
            MeasurementJournal.BindToSpecification(record, specification.Items[0], specification.FileName);
        }

        var row = SpecificationSummaryBuilder.Build(journal, specification).First();

        Assert.Equal(121, row.ByDrawing[FirstDrawing]);
    }

    [Fact]
    public void Export_WritesSpecificationSheetWithColumnPerDrawing()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        var first = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 120, 0, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(first, specification.Items[0], specification.FileName);
        var second = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 55, 0, 2, SecondDrawing);
        MeasurementJournal.BindToSpecification(second, specification.Items[0], specification.FileName);

        var path = new ExcelExportService().Export(
            journal, FirstDrawing, temp.Combine("книга.xlsx"), specification);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SpecificationSheetName);

        Assert.Equal("Кол-во по спецификации", sheet.Cell(2, 7).GetString());
        Assert.Equal(SpecificationSummaryBuilder.CountColumnPrefix + FirstDrawing, sheet.Cell(2, 8).GetString());
        Assert.Equal(SpecificationSummaryBuilder.CountColumnPrefix + SecondDrawing, sheet.Cell(2, 9).GetString());
        Assert.Equal("Всего подсчитано", sheet.Cell(2, 10).GetString());
        Assert.Equal("Расхождение", sheet.Cell(2, 11).GetString());

        // Строка трубы: проект, два подсчёта, итог и расхождение.
        Assert.Equal("Труба стальная Dn80", sheet.Cell(3, 2).GetString());
        Assert.Equal(200, sheet.Cell(3, 7).GetValue<double>());
        Assert.Equal(120, sheet.Cell(3, 8).GetValue<double>());
        Assert.Equal(55, sheet.Cell(3, 9).GetValue<double>());
        Assert.Equal(175, sheet.Cell(3, 10).GetValue<double>());
        Assert.Equal(-25, sheet.Cell(3, 11).GetValue<double>());

        // Незамеренные позиции остаются в своде с нулями.
        Assert.Equal(0, sheet.Cell(4, 10).GetValue<double>());
        Assert.Equal(3, sheet.Cell(5, 1).GetValue<int>());
    }

    [Fact]
    public void ResetMeasuredValues_KeepsSpecificationRowButZeroesTheMeasurement()
    {
        // Сценарий: замерили, потом стёрли геометрию под позицией.
        // Строка спецификации — это план работ, она остаётся; замер обнуляется.
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        var record = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 120, 5, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(record, specification.Items[0], specification.FileName);

        record.ResetMeasuredValues();

        Assert.Contains(record, journal.Records);
        Assert.True(record.IsFromSpecification);
        Assert.Equal(0, record.MeasuredQuantity);
        Assert.Equal(0, record.PolylineCount);

        // Расхождение считается от нуля: вся проектная величина не подтверждена.
        Assert.Equal(-200, record.SpecificationDifference);

        var row = SpecificationSummaryBuilder.Build(journal, specification).First();
        Assert.Equal(0, row.Total);
        Assert.Equal(0, row.ByDrawing[FirstDrawing]);
    }

    [Fact]
    public void ResetMeasuredValues_KeepsManualValue()
    {
        // Ручное значение введено человеком и по правилам журнала
        // переживает пересчёты — обнуление геометрии его не трогает.
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var record = journal.AddOrUpdateLinear(TestData.Pipe("Труба стальная Dn80"), "", "PIPE_D89x4", 120, 0, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(record, specification.Items[0], specification.FileName);
        record.ManualLengthM = 90;

        record.ResetMeasuredValues();

        Assert.Equal(90, record.MeasuredQuantity);
        Assert.True(record.HasManualValue);
    }

    [Fact]
    public void ZeroedRow_IsWrittenAsZeroInExcelNotAsOldValue()
    {
        using var temp = new TempDirectory();
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var record = journal.AddOrUpdateLinear(TestData.Pipe("Труба стальная Dn80"), "", "PIPE_D89x4", 120, 0, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(record, specification.Items[0], specification.FileName);

        record.ResetMeasuredValues();

        var path = new ExcelExportService().Export(journal, FirstDrawing, temp.Combine("книга.xlsx"), specification);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(ExcelExportService.SpecificationSheetName);

        // Чертёж один, поэтому столбцы: 8 — подсчёт по нему, 9 — всего, 10 — расхождение.
        Assert.Equal(0, sheet.Cell(3, 8).GetValue<double>());
        Assert.Equal(0, sheet.Cell(3, 9).GetValue<double>());
        Assert.Equal(-200, sheet.Cell(3, 10).GetValue<double>());
    }

    [Fact]
    public void RenamedDrawing_GetsItsOwnColumnAndOldOneStopsUpdating()
    {
        // Ключ столбца — имя файла: переименование в AutoCAD даёт новый столбец,
        // а прежний остаётся с последними известными числами. Перемещение файла
        // в другую папку имя не меняет, поэтому столбец не раздваивается.
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        var before = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 120, 0, 4, "Корпус-1.dwg");
        MeasurementJournal.BindToSpecification(before, specification.Items[0], specification.FileName);

        var after = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 130, 0, 5, "Корпус-1-испр.dwg");
        MeasurementJournal.BindToSpecification(after, specification.Items[0], specification.FileName);

        var row = SpecificationSummaryBuilder.Build(journal, specification).First();

        Assert.Equal(new[] { "Корпус-1.dwg", "Корпус-1-испр.dwg" },
            SpecificationSummaryBuilder.GetDrawingColumns(journal));
        Assert.Equal(120, row.ByDrawing["Корпус-1.dwg"]);
        Assert.Equal(130, row.ByDrawing["Корпус-1-испр.dwg"]);
    }

    [Fact]
    public void AddSelectedItems_TransfersOnlyChosenPositions()
    {
        // Выборочный импорт: в журнал уходят только отмеченные позиции,
        // а спецификация остаётся целой — по ней строится свод.
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();

        var chosen = specification.Items.Where(i => i.Number != 2).ToList();
        foreach (var item in chosen)
            journal.AddFromSpecification(item, specification.FileName, FirstDrawing, material: null);

        Assert.Equal(2, journal.Records.Count);
        Assert.DoesNotContain(journal.Records, r => r.SpecificationItemId == 2);

        // Невыбранная позиция никуда не пропала: в своде она есть с нулём.
        var rows = SpecificationSummaryBuilder.Build(journal, specification);
        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[1].Total);
    }

    [Fact]
    public void FindBySpecificationItem_CombinesMaterialAndPositionForFiltering()
    {
        // Фильтр палитры сводится к этой паре условий: материал и позиция.
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        var first = journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 60, 0, 2, FirstDrawing);
        MeasurementJournal.BindToSpecification(first, specification.Items[0], specification.FileName);
        var second = journal.AddOrUpdateLinear(pipe, "Этаж 2", "PIPE_D89x4_Этаж 2", 40, 0, 1, FirstDrawing);
        MeasurementJournal.BindToSpecification(second, specification.Items[0], specification.FileName);
        journal.AddOrUpdateLinear(TestData.Cable(), "Этаж 1", "CABLE_3x2.5_Этаж 1", 25, 0, 1, FirstDrawing);

        var byPosition = journal.FindBySpecificationItem(1);
        Assert.Equal(2, byPosition.Count);

        var byBoth = journal.Records
            .Where(r => r.MaterialName == pipe.Name && r.SpecificationItemId == 1)
            .ToList();

        Assert.Equal(2, byBoth.Count);
        Assert.All(byBoth, r => Assert.Equal("Труба стальная Dn80", r.MaterialName));
    }

    [Fact]
    public void Rebind_MovesRecordsToNewSpecificationByMaterialName()
    {
        // Перезагрузка спецификации: замеры сделаны по чертежу и остаются
        // верными, поэтому записи сохраняются, а привязка пересобирается.
        var journal = new MeasurementJournal();
        var oldSpecification = BuildSpecification();
        var pipe = TestData.Pipe("Труба стальная Dn80");

        var record = journal.AddOrUpdateLinear(pipe, "", "PIPE_D89x4", 120, 0, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(record, oldSpecification.Items[0], oldSpecification.FileName);

        // В новой редакции та же труба идёт под другим номером и с другим объёмом.
        var newSpecification = new Specification
        {
            FileName = "спецификация-ред2.xlsx",
            Items = new[]
            {
                new SpecificationItem { Number = 1, Name = "Кран шаровой Dn80", Unit = "шт.", Quantity = 12 },
                new SpecificationItem { Number = 2, Name = "Труба стальная Dn80", Unit = "м.п.", Quantity = 260 }
            }
        };

        var (rebound, unbound) = journal.RebindToSpecification(newSpecification);

        Assert.Equal(1, rebound);
        Assert.Equal(0, unbound);
        Assert.Equal(2, record.SpecificationItemId);
        Assert.Equal("спецификация-ред2.xlsx", record.SpecificationFileName);
        Assert.Equal(260, record.SpecificationQuantity);

        // Сам замер не тронут.
        Assert.Equal(120, record.MeasuredQuantity);
        Assert.Equal(-140, record.SpecificationDifference);
    }

    [Fact]
    public void Rebind_KeepsRecordButDropsBindingWhenItemDisappeared()
    {
        var journal = new MeasurementJournal();
        var oldSpecification = BuildSpecification();
        var record = journal.AddOrUpdateLinear(TestData.Pipe("Труба стальная Dn80"), "", "PIPE_D89x4", 120, 0, 4, FirstDrawing);
        MeasurementJournal.BindToSpecification(record, oldSpecification.Items[0], oldSpecification.FileName);

        var newSpecification = new Specification
        {
            FileName = "другая.xlsx",
            Items = new[] { new SpecificationItem { Number = 1, Name = "Кран шаровой Dn80", Unit = "шт.", Quantity = 5 } }
        };

        var (rebound, unbound) = journal.RebindToSpecification(newSpecification);

        Assert.Equal(0, rebound);
        Assert.Equal(1, unbound);

        // Запись осталась вместе с замером, но спецификации больше не соответствует.
        Assert.Contains(record, journal.Records);
        Assert.False(record.IsFromSpecification);
        Assert.Null(record.SpecificationQuantity);
        Assert.Equal(120, record.MeasuredQuantity);
        Assert.Empty(record.SpecificationFileName);
    }

    [Fact]
    public void Rebind_WithoutSpecificationClearsEveryBinding()
    {
        var journal = new MeasurementJournal();
        var specification = BuildSpecification();
        var record = journal.AddOrUpdateLinear(TestData.Pipe("Труба стальная Dn80"), "", "PIPE_D89x4", 10, 0, 1, FirstDrawing);
        MeasurementJournal.BindToSpecification(record, specification.Items[0], specification.FileName);

        var (rebound, unbound) = journal.RebindToSpecification(null);

        Assert.Equal(0, rebound);
        Assert.Equal(1, unbound);
        Assert.Single(journal.Records);
        Assert.False(record.IsFromSpecification);
    }

    [Fact]
    public void Export_WithoutSpecificationKeepsThreeSheets()
    {
        using var temp = new TempDirectory();

        var path = new ExcelExportService().Export(
            new MeasurementJournal(), FirstDrawing, temp.Combine("книга.xlsx"));

        using var workbook = new XLWorkbook(path);

        Assert.Equal(3, workbook.Worksheets.Count);
        Assert.DoesNotContain(
            workbook.Worksheets,
            s => s.Name == ExcelExportService.SpecificationSheetName);
    }
}
