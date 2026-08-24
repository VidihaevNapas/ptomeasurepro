using CadMeasureDomain.Models;
using CadMeasureDomain.Tools;

namespace CadMeasureDomain.Services;

/// <summary>
/// Номенклатура материалов по умолчанию: отопление, водоснабжение,
/// канализация, вентиляция.
///
/// Этот каталог записывается в materials.json при первом запуске, если файла
/// ещё нет, — чтобы список выбора не был пустым и инженер сразу начал работать.
/// Позиции собраны по типовым сортаментам (ГОСТ 10704, ГОСТ 3262, ГОСТ 617,
/// ряд PP-R/PE-X/ПНД, ряд сечений воздуховодов).
///
/// Наименования намеренно даны так, как их пишут в спецификациях: именно
/// наименование — ключ материала, по нему идут поиск, журнал и слои.
/// Реестр правится дальше руками в materials.json либо кнопками
/// «Добавить материал» / «Удалить материал» в окне выбора.
/// </summary>
public static class MaterialCatalog
{
    private const string PipeUnit = "м.п.";
    private const string PieceUnit = "шт.";

    public static List<Material> CreateDefault()
    {
        var list = new List<Material>();

        AddSteelWeldedPipes(list);
        AddSteelWaterGasPipes(list);
        AddCopperPipes(list);
        AddPolypropylenePipes(list);
        AddCrossLinkedPolyethylenePipes(list);
        AddHdpePipes(list);
        AddSewerPipes(list);
        AddRectangularDucts(list);
        AddRoundDucts(list);
        AddFlexibleDucts(list);
        AddCables(list);

        AddPipeFittings(list);
        AddValves(list);
        AddFlangesAndPlugs(list);
        AddDuctFittings(list);
        AddEquipment(list);

        return list;
    }

    // ======================= ОТОПЛЕНИЕ / ВОДОСНАБЖЕНИЕ: сталь =======================

    /// <summary>Трубы стальные электросварные прямошовные, ГОСТ 10704-91.</summary>
    private static void AddSteelWeldedPipes(List<Material> list)
    {
        (double Diameter, double Wall)[] sizes =
        {
            (57, 3), (57, 3.5), (76, 3), (76, 3.5), (89, 3.5), (89, 4),
            (108, 4), (108, 4.5), (133, 4), (133, 4.5), (159, 4.5), (159, 5),
            (219, 6), (273, 7), (325, 8), (377, 9), (426, 9)
        };

        foreach (var (diameter, wall) in sizes)
        {
            var size = $"⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}";

            list.Add(Pipe($"Труба стальная электросварная прямошовная оцинкованная {size}", diameter, wall));
            list.Add(Pipe($"Труба стальная электросварная прямошовная {size}", diameter, wall));
        }
    }

    /// <summary>Трубы стальные водогазопроводные, ГОСТ 3262-75.</summary>
    private static void AddSteelWaterGasPipes(List<Material> list)
    {
        (int Dn, double Diameter, double Wall)[] sizes =
        {
            (15, 21.3, 2.8), (20, 26.8, 2.8), (25, 33.5, 3.2), (32, 42.3, 3.2),
            (40, 48, 3.5), (50, 60, 3.5), (65, 75.5, 4), (80, 88.5, 4), (100, 114, 4.5)
        };

        foreach (var (dn, diameter, wall) in sizes)
        {
            list.Add(Pipe($"Труба стальная водогазопроводная оцинкованная Dn{dn} (⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)})", diameter, wall));
            list.Add(Pipe($"Труба стальная водогазопроводная Dn{dn} (⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)})", diameter, wall));
        }
    }

    /// <summary>Трубы медные, ГОСТ 617-2006 — отопление и водоснабжение.</summary>
    private static void AddCopperPipes(List<Material> list)
    {
        (double Diameter, double Wall)[] sizes =
        {
            (15, 1), (18, 1), (22, 1), (28, 1.5), (35, 1.5), (42, 1.5), (54, 2)
        };

        foreach (var (diameter, wall) in sizes)
            list.Add(Pipe($"Труба медная ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));
    }

    /// <summary>Трубы полипропиленовые PP-R: PN20 — отопление, PN10 — холодная вода.</summary>
    private static void AddPolypropylenePipes(List<Material> list)
    {
        (double Diameter, double Wall)[] pn20 =
        {
            (20, 3.4), (25, 4.2), (32, 5.4), (40, 6.7), (50, 8.3),
            (63, 10.5), (75, 12.5), (90, 15), (110, 18.3)
        };

        foreach (var (diameter, wall) in pn20)
            list.Add(Pipe($"Труба полипропиленовая PP-R PN20 ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));

        (double Diameter, double Wall)[] pn10 =
        {
            (20, 2.3), (25, 2.8), (32, 3.6), (40, 4.5), (50, 5.6), (63, 7.1)
        };

        foreach (var (diameter, wall) in pn10)
            list.Add(Pipe($"Труба полипропиленовая PP-R PN10 ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));

        (double Diameter, double Wall)[] reinforced =
        {
            (20, 3.4), (25, 4.2), (32, 5.4), (40, 6.7), (50, 8.3), (63, 10.5)
        };

        foreach (var (diameter, wall) in reinforced)
            list.Add(Pipe($"Труба полипропиленовая PP-R армированная стекловолокном ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));
    }

    /// <summary>Трубы из сшитого полиэтилена PE-X — тёплые полы, радиаторная разводка.</summary>
    private static void AddCrossLinkedPolyethylenePipes(List<Material> list)
    {
        (double Diameter, double Wall)[] sizes =
        {
            (16, 2), (20, 2), (25, 3.5), (32, 4.4), (40, 5.5)
        };

        foreach (var (diameter, wall) in sizes)
            list.Add(Pipe($"Труба из сшитого полиэтилена PE-X ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));

        foreach (var (diameter, wall) in sizes)
            list.Add(Pipe($"Труба металлопластиковая PE-X/AL/PE-X ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));
    }

    /// <summary>Трубы ПНД ПЭ100 SDR17 — наружное и вводное водоснабжение.</summary>
    private static void AddHdpePipes(List<Material> list)
    {
        (double Diameter, double Wall)[] sizes =
        {
            (32, 2), (40, 2.4), (50, 3), (63, 3.8), (75, 4.5),
            (90, 5.4), (110, 6.6), (160, 9.5), (225, 13.4)
        };

        foreach (var (diameter, wall) in sizes)
            list.Add(Pipe($"Труба ПНД ПЭ100 SDR17 ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));
    }

    // ======================= КАНАЛИЗАЦИЯ =======================

    private static void AddSewerPipes(List<Material> list)
    {
        // Внутренняя канализация, ПВХ/ПП раструбная.
        (double Diameter, double Wall)[] indoor =
        {
            (40, 1.8), (50, 1.8), (75, 1.9), (110, 2.2), (110, 3.2), (160, 4)
        };

        foreach (var (diameter, wall) in indoor)
            list.Add(Pipe($"Труба канализационная ПВХ раструбная ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));

        foreach (var (diameter, wall) in indoor)
            list.Add(Pipe($"Труба канализационная ПП бесшумная ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));

        // Наружная канализация, ПВХ SN4.
        (double Diameter, double Wall)[] outdoor =
        {
            (110, 3.2), (160, 4.7), (200, 5.9), (250, 7.3), (315, 9.2)
        };

        foreach (var (diameter, wall) in outdoor)
            list.Add(Pipe($"Труба канализационная наружная ПВХ SN4 ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));

        // Чугунные безраструбные SML.
        (double Diameter, double Wall)[] castIron =
        {
            (50, 3.5), (80, 3.5), (100, 3.5), (150, 4)
        };

        foreach (var (diameter, wall) in castIron)
            list.Add(Pipe($"Труба чугунная безраструбная SML ⌀{MaterialFormatter.FormatNumberRu(diameter)}x{MaterialFormatter.FormatNumberRu(wall)}", diameter, wall));
    }

    // ======================= ВЕНТИЛЯЦИЯ =======================

    /// <summary>
    /// Воздуховоды прямоугольные из оцинкованной стали класса «В».
    /// Толщина листа назначается по большей стороне — как в СП 60.13330.
    /// </summary>
    private static void AddRectangularDucts(List<Material> list)
    {
        (int Width, int Height)[] sizes =
        {
            (100, 150), (150, 150), (150, 250), (200, 200), (250, 150), (250, 250),
            (300, 150), (300, 200), (300, 300), (400, 200), (400, 250), (400, 400),
            (500, 250), (500, 300), (500, 500), (600, 300), (600, 400), (600, 500),
            (700, 400), (700, 500), (800, 400), (800, 500), (800, 800),
            (900, 500), (1000, 500), (1000, 600), (1000, 800),
            (1200, 600), (1200, 800), (1250, 800), (1400, 800),
            (1600, 800), (1600, 1000), (2000, 1000)
        };

        foreach (var (width, height) in sizes)
        {
            var thickness = RectangularDuctThickness(Math.Max(width, height));
            list.Add(new Material
            {
                Class = MaterialClasses.Duct,
                Name = $"Воздуховод прямоугольный из оцинкованной стали класса В, {MaterialFormatter.FormatNumberRu(thickness)} мм, {width}х{height}",
                Unit = PipeUnit,
                WidthMm = width,
                HeightMm = height,
                SheetThicknessMm = thickness
            });
        }
    }

    /// <summary>Воздуховоды круглые спирально-навивные из оцинкованной стали.</summary>
    private static void AddRoundDucts(List<Material> list)
    {
        int[] diameters =
        {
            100, 125, 140, 160, 180, 200, 225, 250, 280, 315, 355,
            400, 450, 500, 560, 630, 710, 800, 900, 1000, 1120, 1250
        };

        foreach (var diameter in diameters)
        {
            var thickness = RoundDuctThickness(diameter);
            list.Add(new Material
            {
                Class = MaterialClasses.Duct,
                Name = $"Воздуховод круглый спирально-навивной из оцинкованной стали, {MaterialFormatter.FormatNumberRu(thickness)} мм, ⌀{diameter}",
                Unit = PipeUnit,
                DiameterMm = diameter,
                SheetThicknessMm = thickness
            });
        }
    }

    /// <summary>Гибкие воздуховоды — подключение диффузоров.</summary>
    private static void AddFlexibleDucts(List<Material> list)
    {
        int[] diameters = { 100, 125, 160, 200, 250, 315 };

        foreach (var diameter in diameters)
        {
            list.Add(new Material
            {
                Class = MaterialClasses.Duct,
                Name = $"Воздуховод гибкий теплоизолированный ⌀{diameter}",
                Unit = PipeUnit,
                DiameterMm = diameter
            });
        }
    }

    /// <summary>Толщина листа прямоугольного воздуховода по большей стороне, мм.</summary>
    private static double RectangularDuctThickness(int largestSideMm) => largestSideMm switch
    {
        <= 250 => 0.5,
        <= 1000 => 0.7,
        <= 2000 => 0.9,
        _ => 1.0
    };

    /// <summary>Толщина листа круглого воздуховода по диаметру, мм.</summary>
    private static double RoundDuctThickness(int diameterMm) => diameterMm switch
    {
        <= 250 => 0.5,
        <= 900 => 0.7,
        _ => 1.0
    };

    // ======================= КАБЕЛЬНАЯ ПРОДУКЦИЯ =======================

    /// <summary>
    /// Кабели силовые и контрольные. Марки — самые ходовые для инженерных
    /// систем: питание оборудования, щиты, автоматика.
    /// </summary>
    private static void AddCables(List<Material> list)
    {
        // Силовые: (число жил, сечение).
        (int Cores, double Section)[] power =
        {
            (2, 1.5), (2, 2.5), (3, 1.5), (3, 2.5), (3, 4), (3, 6), (3, 10),
            (4, 1.5), (4, 2.5), (4, 4), (4, 6), (4, 10), (4, 16), (4, 25), (4, 35), (4, 50),
            (5, 1.5), (5, 2.5), (5, 4), (5, 6), (5, 10), (5, 16), (5, 25), (5, 35), (5, 50), (5, 70), (5, 95)
        };

        string[] powerBrands =
        {
            "ВВГнг(А)-LS",
            "ВВГнг(А)-FRLS",
            "ПвВГнг(А)-LS",
            "NYM"
        };

        foreach (var brand in powerBrands)
        {
            foreach (var (cores, section) in power)
            {
                // NYM не выпускают в больших сечениях — ограничиваем ряд.
                if (brand == "NYM" && (section > 10 || cores > 5)) continue;

                list.Add(Cable($"Кабель {brand} {cores}х{MaterialFormatter.FormatNumberRu(section)}", cores, section));
            }
        }

        // Контрольные и слаботочные.
        (int Cores, double Section)[] control =
        {
            (2, 0.75), (2, 1), (2, 1.5), (3, 0.75), (3, 1), (3, 1.5),
            (4, 0.75), (4, 1), (4, 1.5), (5, 1), (5, 1.5), (7, 1), (7, 1.5), (10, 1), (10, 1.5)
        };

        foreach (var (cores, section) in control)
            list.Add(Cable($"Кабель контрольный КВВГнг(А)-LS {cores}х{MaterialFormatter.FormatNumberRu(section)}", cores, section));

        foreach (var (cores, section) in new[] { (2, 0.75), (2, 1.5), (4, 0.75), (4, 1.5) })
            list.Add(Cable($"Кабель огнестойкий КПСнг(А)-FRLS {cores}х{MaterialFormatter.FormatNumberRu(section)}", cores, section));
    }

    // ======================= ШТУЧНЫЕ ИЗДЕЛИЯ =======================

    /// <summary>Фасонные изделия трубопроводов: отводы, тройники, переходы, муфты.</summary>
    private static void AddPipeFittings(List<Material> list)
    {
        int[] steel = { 57, 76, 89, 108, 133, 159, 219, 273, 325 };

        foreach (var dn in steel)
        {
            list.Add(Piece($"Отвод стальной крутоизогнутый 90°, ⌀{dn}", dn, PieceKinds.PipeFitting));
            list.Add(Piece($"Отвод стальной крутоизогнутый 45°, ⌀{dn}", dn, PieceKinds.PipeFitting));
            list.Add(Piece($"Тройник стальной равнопроходный, ⌀{dn}", dn, PieceKinds.PipeFitting));
        }

        foreach (var dn in new[] { 76, 89, 108, 133, 159, 219, 273 })
            list.Add(Piece($"Переход стальной концентрический, ⌀{dn}", dn, PieceKinds.PipeFitting));

        int[] threaded = { 15, 20, 25, 32, 40, 50, 65, 80, 100 };

        foreach (var dn in threaded)
        {
            list.Add(Piece($"Отвод резьбовой 90°, Dn{dn}", dn, PieceKinds.PipeFitting));
            list.Add(Piece($"Тройник резьбовой, Dn{dn}", dn, PieceKinds.PipeFitting));
            list.Add(Piece($"Муфта резьбовая, Dn{dn}", dn, PieceKinds.PipeFitting));
            list.Add(Piece($"Сгон резьбовой, Dn{dn}", dn, PieceKinds.PipeFitting));
        }

        foreach (var dn in new[] { 50, 110, 160 })
        {
            list.Add(Piece($"Отвод канализационный 90°, ⌀{dn}", dn, PieceKinds.PipeFitting));
            list.Add(Piece($"Тройник канализационный 87°, ⌀{dn}", dn, PieceKinds.PipeFitting));
            list.Add(Piece($"Ревизия канализационная, ⌀{dn}", dn, PieceKinds.PipeFitting));
        }
    }

    /// <summary>Запорная и регулирующая арматура.</summary>
    private static void AddValves(List<Material> list)
    {
        int[] threaded = { 15, 20, 25, 32, 40, 50 };
        int[] flanged = { 50, 65, 80, 100, 125, 150, 200 };

        foreach (var dn in threaded)
        {
            list.Add(Piece($"Кран шаровой муфтовый, Dn{dn}", dn, PieceKinds.Valve));
            list.Add(Piece($"Фильтр сетчатый муфтовый, Dn{dn}", dn, PieceKinds.Valve));
            list.Add(Piece($"Клапан обратный муфтовый, Dn{dn}", dn, PieceKinds.Valve));
            list.Add(Piece($"Клапан балансировочный ручной, Dn{dn}", dn, PieceKinds.Valve));
        }

        foreach (var dn in flanged)
        {
            list.Add(Piece($"Затвор дисковый поворотный межфланцевый, Dn{dn}", dn, PieceKinds.Valve));
            list.Add(Piece($"Задвижка чугунная фланцевая, Dn{dn}", dn, PieceKinds.Valve));
            list.Add(Piece($"Фильтр сетчатый фланцевый, Dn{dn}", dn, PieceKinds.Valve));
            list.Add(Piece($"Клапан обратный межфланцевый, Dn{dn}", dn, PieceKinds.Valve));
        }

        foreach (var dn in new[] { 15, 20, 25 })
            list.Add(Piece($"Кран шаровой со сгоном (американка), Dn{dn}", dn, PieceKinds.Valve));

        list.Add(Piece("Воздухоотводчик автоматический, Dn15", 15, PieceKinds.Valve));
        list.Add(Piece("Клапан предохранительный, Dn20", 20, PieceKinds.Valve));
    }

    /// <summary>Фланцы и заглушки.</summary>
    private static void AddFlangesAndPlugs(List<Material> list)
    {
        int[] sizes = { 15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300 };

        foreach (var dn in sizes)
        {
            list.Add(Piece($"Фланец стальной плоский приварной Ру16, Dn{dn}", dn, PieceKinds.Flange));
            list.Add(Piece($"Заглушка фланцевая Ру16, Dn{dn}", dn, PieceKinds.Flange));
            list.Add(Piece($"Прокладка паронитовая, Dn{dn}", dn, PieceKinds.Flange));
        }
    }

    /// <summary>Фасонные изделия вентиляции.</summary>
    private static void AddDuctFittings(List<Material> list)
    {
        int[] round = { 100, 125, 160, 200, 250, 315, 400, 500, 630 };

        foreach (var dn in round)
        {
            list.Add(Piece($"Отвод круглый 90°, ⌀{dn}", dn, PieceKinds.DuctFitting));
            list.Add(Piece($"Тройник круглый, ⌀{dn}", dn, PieceKinds.DuctFitting));
            list.Add(Piece($"Переход круглый, ⌀{dn}", dn, PieceKinds.DuctFitting));
            list.Add(Piece($"Клапан воздушный регулирующий круглый, ⌀{dn}", dn, PieceKinds.DuctFitting));
            list.Add(Piece($"Дроссель-клапан круглый, ⌀{dn}", dn, PieceKinds.DuctFitting));
        }

        foreach (var dn in new[] { 100, 125, 160, 200, 250 })
        {
            list.Add(Piece($"Диффузор приточный регулируемый, ⌀{dn}", dn, PieceKinds.DuctFitting));
            list.Add(Piece($"Анемостат вытяжной, ⌀{dn}", dn, PieceKinds.DuctFitting));
        }

        foreach (var dn in new[] { 200, 250, 315, 400, 500 })
            list.Add(Piece($"Клапан противопожарный нормально открытый, ⌀{dn}", dn, PieceKinds.DuctFitting));
    }

    /// <summary>Оборудование инженерных систем.</summary>
    private static void AddEquipment(List<Material> list)
    {
        list.Add(Equipment("Насос циркуляционный с мокрым ротором"));
        list.Add(Equipment("Насос циркуляционный сдвоенный"));
        list.Add(Equipment("Насосная станция повышения давления"));
        list.Add(Equipment("Бак расширительный мембранный"));
        list.Add(Equipment("Теплообменник пластинчатый разборный"));
        list.Add(Equipment("Узел учёта тепловой энергии"));
        list.Add(Equipment("Коллектор распределительный"));
        list.Add(Equipment("Шкаф коллекторный"));
        list.Add(Equipment("Радиатор стальной панельный"));
        list.Add(Equipment("Конвектор внутрипольный"));
        list.Add(Equipment("Установка приточная канальная"));
        list.Add(Equipment("Установка приточно-вытяжная с рекуперацией"));
        list.Add(Equipment("Вентилятор канальный круглый"));
        list.Add(Equipment("Вентилятор крышный"));
        list.Add(Equipment("Вентилятор дымоудаления"));
        list.Add(Equipment("Фанкойл канальный"));
        list.Add(Equipment("Блок наружный VRF"));
        list.Add(Equipment("Блок внутренний VRF"));
        list.Add(Equipment("Водонагреватель электрический накопительный"));
        list.Add(Equipment("Счётчик воды крыльчатый"));
        list.Add(Equipment("Щит автоматики вентиляции"));
    }

    // ======================= Помощники =======================

    private static Material Pipe(string name, double diameterMm, double? wallThicknessMm) => new()
    {
        Class = MaterialClasses.Pipe,
        Name = name,
        Unit = PipeUnit,
        DiameterMm = diameterMm,
        WallThicknessMm = wallThicknessMm
    };

    private static Material Cable(string name, int coreCount, double crossSectionMm2) => new()
    {
        Class = MaterialClasses.Cable,
        Name = name,
        Unit = PipeUnit,
        CoreCount = coreCount,
        CrossSectionMm2 = crossSectionMm2
    };

    private static Material Piece(string name, int nominalDiameterMm, string pieceKind) => new()
    {
        Class = MaterialClasses.Piece,
        Name = name,
        Unit = PieceUnit,
        NominalDiameterMm = nominalDiameterMm,
        PieceKind = pieceKind
    };

    private static Material Equipment(string name) => new()
    {
        Class = MaterialClasses.Piece,
        Name = name,
        Unit = PieceUnit,
        PieceKind = PieceKinds.Equipment
    };
}
