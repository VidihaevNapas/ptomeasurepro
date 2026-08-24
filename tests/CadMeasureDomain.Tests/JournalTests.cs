using System.ComponentModel;
using CadMeasureDomain.Models;
using CadMeasureDomain.Services;

namespace CadMeasureDomain.Tests;

/// <summary>
/// Строка журнала: приоритет ручного значения, округление итога
/// и ключ «материал + участок + DWG».
/// </summary>
public class MeasurementRecordTests
{
    [Fact]
    public void LengthM_SumsHorizontalAndVertical()
    {
        var record = new MeasurementRecord { HorizontalLengthM = 12.5, VerticalLengthM = 3.2 };

        Assert.Equal(15.7, record.LengthM);
    }

    [Fact]
    public void LengthM_RoundsTotalNotAddends()
    {
        // Каждое слагаемое по отдельности округлилось бы в ноль,
        // а сумма должна дать 0,01.
        var record = new MeasurementRecord { HorizontalLengthM = 0.004, VerticalLengthM = 0.004 };

        Assert.Equal(0.01, record.LengthM);
    }

    [Fact]
    public void ManualLength_OverridesMeasuredValue()
    {
        var record = new MeasurementRecord { HorizontalLengthM = 12.5, VerticalLengthM = 3.2 };

        record.ManualLengthM = 20;

        Assert.Equal(20, record.LengthM);
        Assert.True(record.HasManualValue);
    }

    [Fact]
    public void ManualLength_ResetRestoresMeasuredValue()
    {
        var record = new MeasurementRecord { HorizontalLengthM = 12.5, ManualLengthM = 20 };

        record.ManualLengthM = null;

        Assert.Equal(12.5, record.LengthM);
        Assert.False(record.HasManualValue);
    }

    [Fact]
    public void ManualQuantity_OverridesScannedQuantity()
    {
        var record = new MeasurementRecord { MaterialClass = MaterialClasses.Piece, ScannedQuantity = 5 };

        Assert.Equal(5, record.Quantity);

        record.ManualQuantity = 8;

        Assert.Equal(8, record.Quantity);
        Assert.True(record.HasManualValue);
    }

    [Fact]
    public void StatementUnit_DependsOnClass()
    {
        Assert.Equal("м", new MeasurementRecord { MaterialClass = MaterialClasses.Pipe }.StatementUnit);
        Assert.Equal("шт", new MeasurementRecord { MaterialClass = MaterialClasses.Piece }.StatementUnit);
    }

    [Fact]
    public void QuantityDisplay_ShowsLengthForLinearAndCountForPieces()
    {
        using var _ = new CultureScope("ru-RU");

        var pipe = new MeasurementRecord { MaterialClass = MaterialClasses.Pipe, HorizontalLengthM = 12.5 };
        var piece = new MeasurementRecord { MaterialClass = MaterialClasses.Piece, ScannedQuantity = 7 };

        Assert.Equal("12,50", pipe.QuantityDisplay);
        Assert.Equal("7", piece.QuantityDisplay);
    }

    [Fact]
    public void BuildKey_IgnoresCaseAndSurroundingSpaces()
    {
        Assert.Equal(
            MeasurementRecord.BuildKey("Труба стальная", "Этаж 1", "объект.dwg"),
            MeasurementRecord.BuildKey("  труба СТАЛЬНАЯ  ", " этаж 1 ", "ОБЪЕКТ.DWG"));
    }

    [Fact]
    public void BuildKey_SeparatesPartsUnambiguously()
    {
        // Разделитель — управляющий символ, поэтому «Труба»+«1»
        // и «Труб»+«а1» не схлопываются в один ключ.
        Assert.NotEqual(
            MeasurementRecord.BuildKey("Труба", "1", "объект.dwg"),
            MeasurementRecord.BuildKey("Труб", "а1", "объект.dwg"));
    }

    [Fact]
    public void BuildKey_HandlesNulls()
    {
        Assert.Equal(MeasurementRecord.BuildKey(null, null, null), MeasurementRecord.BuildKey("", "", ""));
    }

    [Fact]
    public void PropertyChanged_FiresForDerivedLengthProperties()
    {
        // Таблица журнала в палитре обновляется по этим уведомлениям:
        // без них пересчёт был бы виден только после пересборки коллекции.
        var record = new MeasurementRecord();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)record).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        record.HorizontalLengthM = 10;

        Assert.Contains(nameof(MeasurementRecord.HorizontalLengthM), changed);
        Assert.Contains(nameof(MeasurementRecord.LengthM), changed);
        Assert.Contains(nameof(MeasurementRecord.QuantityDisplay), changed);
    }

    [Fact]
    public void PropertyChanged_IsSilentWhenValueIsUnchanged()
    {
        var record = new MeasurementRecord { HorizontalLengthM = 10 };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)record).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        record.HorizontalLengthM = 10;

        Assert.Empty(changed);
    }
}

/// <summary>
/// Журнал: одна строка на «материал + участок + DWG».
/// Журнал выводится из чертежа, поэтому повторный пересчёт обязан обновлять
/// существующую строку, а не плодить дубликаты.
/// </summary>
public class MeasurementJournalTests
{
    private const string Drawing = "объект.dwg";

    [Fact]
    public void AddOrUpdateLinear_CreatesRecord()
    {
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();

        var record = journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 2, 3, Drawing);

        Assert.Single(journal.Records);
        Assert.Equal(pipe.Name, record.MaterialName);
        Assert.Equal(MaterialClasses.Pipe, record.MaterialClass);
        Assert.Equal("Этаж 1", record.Section);
        Assert.Equal(12, record.LengthM);
        Assert.Equal(3, record.PolylineCount);
        Assert.Equal(Drawing, record.DrawingFileName);
    }

    [Fact]
    public void AddOrUpdateLinear_UpdatesExistingRecordInsteadOfDuplicating()
    {
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();

        var first = journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);
        var second = journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 25, 0, 4, Drawing);

        Assert.Single(journal.Records);
        Assert.Same(first, second);
        Assert.Equal(25, second.LengthM);
        Assert.Equal(4, second.PolylineCount);
    }

    [Fact]
    public void AddOrUpdateLinear_SeparatesSectionsAndDrawings()
    {
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();

        journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(pipe, "Этаж 2", "PIPE_D89x4_Этаж 2", 20, 0, 1, Drawing);
        journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 30, 0, 1, "другой.dwg");

        Assert.Equal(3, journal.Records.Count);
    }

    [Fact]
    public void AddOrUpdatePiece_CountsQuantity()
    {
        var journal = new MeasurementJournal();
        var piece = TestData.Piece();

        var record = journal.AddOrUpdatePiece(piece, "Этаж 1", "PIECE_Dn15_Этаж 1", 12, Drawing);

        Assert.Equal(12, record.Quantity);
        Assert.True(record.IsPiece);
        Assert.Equal(piece.PieceKind, record.PieceKind);
    }

    [Fact]
    public void Find_LocatesRecordByKey()
    {
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();
        journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);

        Assert.NotNull(journal.Find(pipe.Name, "Этаж 1", Drawing));
        Assert.Null(journal.Find(pipe.Name, "Этаж 2", Drawing));
    }

    [Fact]
    public void FindByLayer_FiltersByDrawing()
    {
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();
        journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 20, 0, 1, "другой.dwg");

        Assert.Equal(2, journal.FindByLayer("PIPE_D89x4_Этаж 1").Count);
        Assert.Single(journal.FindByLayer("PIPE_D89x4_Этаж 1", Drawing));
    }

    [Fact]
    public void GetUsedLayerNames_ReturnsDistinctNonEmptyNames()
    {
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.Pipe("Труба вторая"), "Этаж 1", "PIPE_D89x4_Этаж 1", 5, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.Cable(), "Этаж 1", "CABLE_3x2.5_Этаж 1", 7, 0, 1, Drawing);

        Assert.Equal(2, journal.GetUsedLayerNames(Drawing).Count);
    }

    [Fact]
    public void GetRecordsForDrawing_ReturnsOnlyThatDrawing()
    {
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 20, 0, 1, "другой.dwg");

        Assert.Single(journal.GetRecordsForDrawing(Drawing));
    }

    [Fact]
    public void Remove_DropsRecordFromCollectionAndIndex()
    {
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();
        var record = journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);

        Assert.True(journal.Remove(record));
        Assert.Empty(journal.Records);
        Assert.Null(journal.Find(pipe.Name, "Этаж 1", Drawing));

        // Тот же ключ после удаления должен создавать новую строку.
        var recreated = journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 5, 0, 1, Drawing);
        Assert.NotSame(record, recreated);
    }

    [Fact]
    public void HasConflict_DetectsAnotherRecordWithSameKey()
    {
        var journal = new MeasurementJournal();
        var first = journal.AddOrUpdateLinear(TestData.Pipe("Труба А"), "Этаж 1", "PIPE_1", 10, 0, 1, Drawing);
        var second = journal.AddOrUpdateLinear(TestData.Pipe("Труба Б"), "Этаж 1", "PIPE_2", 10, 0, 1, Drawing);

        // Правка «Трубы Б» на «Трубу А» столкнула бы две строки в один ключ.
        Assert.True(journal.HasConflict("Труба А", "Этаж 1", Drawing, second));

        // Сама с собой строка не конфликтует.
        Assert.False(journal.HasConflict("Труба А", "Этаж 1", Drawing, first));
    }

    [Fact]
    public void Rekey_KeepsRecordFindableAfterEdit()
    {
        var journal = new MeasurementJournal();
        var record = journal.AddOrUpdateLinear(TestData.Pipe(), "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);
        var previousKey = record.Key;

        record.Section = "Этаж 2";
        journal.Rekey(record, previousKey);

        Assert.Null(journal.Find(record.MaterialName, "Этаж 1", Drawing));
        Assert.Same(record, journal.Find(record.MaterialName, "Этаж 2", Drawing));
    }

    [Fact]
    public void Clear_EmptiesJournalAndIndex()
    {
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();
        journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);

        journal.Clear();

        Assert.Empty(journal.Records);
        Assert.Null(journal.Find(pipe.Name, "Этаж 1", Drawing));
    }
}

/// <summary>
/// Ведомость: состав, порядок и нумерация строк.
/// Один и тот же результат идёт и в таблицу чертежа, и в Excel.
/// </summary>
public class StatementBuilderTests
{
    private const string Drawing = "объект.dwg";

    [Fact]
    public void Build_OrdersByClassThenName()
    {
        var journal = new MeasurementJournal();
        journal.AddOrUpdatePiece(TestData.Piece(), "", "PIECE_Dn15", 4, Drawing);
        journal.AddOrUpdateLinear(TestData.Cable(), "", "CABLE_3x2.5", 30, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.RectDuct(), "", "DUCT_1250x800_t0.9", 20, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);

        var rows = StatementBuilder.Build(journal, Drawing);

        Assert.Equal(4, rows.Count);
        Assert.Equal(TestData.Pipe().Name, rows[0].MaterialName);
        Assert.Equal(TestData.RectDuct().Name, rows[1].MaterialName);
        Assert.Equal(TestData.Cable().Name, rows[2].MaterialName);
        Assert.Equal(TestData.Piece().Name, rows[3].MaterialName);
    }

    [Fact]
    public void Build_NumbersRowsFromOne()
    {
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.Cable(), "", "CABLE_3x2.5", 30, 0, 1, Drawing);

        var rows = StatementBuilder.Build(journal, Drawing);

        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Number).ToArray());
    }

    [Fact]
    public void Build_UsesStatementUnitsNotRegistryUnits()
    {
        // В реестре «м.п.» и «шт.», в ведомости — ровно «м» и «шт».
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);
        journal.AddOrUpdatePiece(TestData.Piece(), "", "PIECE_Dn15", 4, Drawing);

        var rows = StatementBuilder.Build(journal, Drawing);

        Assert.Equal("м", rows[0].Unit);
        Assert.Equal("шт", rows[1].Unit);
        Assert.False(rows[0].IsPiece);
        Assert.True(rows[1].IsPiece);
    }

    [Fact]
    public void Build_KeepsSectionsAsSeparateRows()
    {
        // Участок в ведомость не выводится, но входит в ключ группировки:
        // один материал на двух участках — две строки.
        var journal = new MeasurementJournal();
        var pipe = TestData.Pipe();
        journal.AddOrUpdateLinear(pipe, "Этаж 1", "PIPE_D89x4_Этаж 1", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(pipe, "Этаж 2", "PIPE_D89x4_Этаж 2", 20, 0, 1, Drawing);

        var rows = StatementBuilder.Build(journal, Drawing);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(pipe.Name, r.MaterialName));
    }

    [Fact]
    public void Build_SumsRecordsOfOneGroup()
    {
        // Строки с одинаковыми «наименование + единица + участок» складываются.
        // Такие строки попадают в журнал в обход индекса — например, после
        // правки наименования в таблице.
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "Этаж 1", "PIPE_D89x4_Этаж 1", 10.5, 0, 1, Drawing);
        journal.Records.Add(new MeasurementRecord
        {
            MaterialClass = MaterialClasses.Pipe,
            MaterialName = TestData.Pipe().Name,
            Section = "Этаж 1",
            DrawingFileName = Drawing,
            HorizontalLengthM = 4.25
        });

        var rows = StatementBuilder.Build(journal, Drawing);

        Assert.Single(rows);
        Assert.Equal(14.75, rows[0].Quantity);
    }

    [Fact]
    public void Build_IgnoresOtherDrawings()
    {
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);
        journal.AddOrUpdateLinear(TestData.Cable(), "", "CABLE_3x2.5", 30, 0, 1, "другой.dwg");

        var rows = StatementBuilder.Build(journal, Drawing);

        Assert.Single(rows);
        Assert.Equal(TestData.Pipe().Name, rows[0].MaterialName);
    }

    [Fact]
    public void Build_ReturnsEmptyListForUnknownDrawing()
    {
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10, 0, 1, Drawing);

        Assert.Empty(StatementBuilder.Build(journal, "третий.dwg"));
    }

    [Fact]
    public void Build_RoundsGroupTotal()
    {
        var journal = new MeasurementJournal();
        journal.AddOrUpdateLinear(TestData.Pipe(), "", "PIPE_D89x4", 10.004, 0.004, 1, Drawing);

        var rows = StatementBuilder.Build(journal, Drawing);

        Assert.Equal(10.01, rows[0].Quantity);
    }

    [Fact]
    public void StatementRow_FormatsQuantityByKind()
    {
        using var _ = new CultureScope("ru-RU");

        Assert.Equal("12,50", new StatementRow(1, "Труба", "м", 12.5, IsPiece: false).QuantityText);
        Assert.Equal("4", new StatementRow(2, "Отвод", "шт", 4, IsPiece: true).QuantityText);
    }

    [Fact]
    public void ColumnHeaders_MatchAgreedStatementForm()
    {
        Assert.Equal(new[] { "п/п", "Наименование материала", "Ед. изм.", "Кол-во" }, StatementBuilder.ColumnHeaders);
    }
}
