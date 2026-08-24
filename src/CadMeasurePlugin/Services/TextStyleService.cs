using Autodesk.AutoCAD.DatabaseServices;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace CadMeasurePlugin.Services;

/// <summary>
/// Поиск и создание текстовых стилей плагина.
///
/// Стили нужны в двух местах — подписи замеров и таблица ведомости, — поэтому
/// логика вынесена сюда: иначе один и тот же стиль создавался бы по-разному
/// в двух файлах.
///
/// Существующие стили не изменяются никогда: если стиль с таким именем уже
/// есть в чертеже, он берётся как есть. Пользователь мог настроить его под
/// себя, и молча перезаписывать чужие настройки нельзя.
/// </summary>
public sealed class TextStyleService
{
    /// <summary>
    /// Найти стиль по имени, при отсутствии — создать с указанной гарнитурой.
    ///
    /// Возвращает ObjectId стиля; если создать не удалось (нет шрифта, чертёж
    /// только для чтения) — текущий стиль чертежа, чтобы текст всё равно
    /// появился. Надпись важнее гарнитуры.
    /// </summary>
    /// <param name="tr">Открытая транзакция.</param>
    /// <param name="db">База чертежа.</param>
    /// <param name="styleName">Имя текстового стиля.</param>
    /// <param name="typeface">Гарнитура TrueType.</param>
    /// <param name="fontFileName">Файл шрифта — подсказка для AutoCAD.</param>
    public ObjectId EnsureTextStyle(
        Transaction tr,
        Database db,
        string styleName,
        string typeface,
        string fontFileName)
    {
        try
        {
            var styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

            // Стиль уже есть — берём как есть, ничего не трогаем.
            if (styles.Has(styleName)) return styles[styleName];

            styles.UpgradeOpen();

            var style = new TextStyleTableRecord
            {
                Name = styleName,
                FileName = fontFileName,

                // Для TrueType решает именно FontDescriptor: имя файла AutoCAD
                // использует лишь как подсказку.
                Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(typeface, false, false, 0, 0),

                // Высота 0 = «переменная»: конкретную высоту задаёт сам текст
                // либо ячейка таблицы.
                TextSize = 0.0
            };

            var id = styles.Add(style);
            tr.AddNewlyCreatedDBObject(style, true);
            return id;
        }
        catch (AcadException)
        {
            return db.Textstyle;
        }
    }

    /// <summary>Стиль подписей замеров (длина над полилинией, номер в круге).</summary>
    public ObjectId EnsureLabelStyle(Transaction tr, Database db) =>
        EnsureTextStyle(
            tr,
            db,
            PluginSettings.LabelTextStyleName,
            PluginSettings.LabelFontTypeface,
            PluginSettings.LabelFontFileName);

    /// <summary>Стиль таблицы ведомости.</summary>
    public ObjectId EnsureTableStyle(Transaction tr, Database db) =>
        EnsureTextStyle(
            tr,
            db,
            PluginSettings.TableTextStyleName,
            PluginSettings.LabelFontTypeface,
            PluginSettings.LabelFontFileName);
}
