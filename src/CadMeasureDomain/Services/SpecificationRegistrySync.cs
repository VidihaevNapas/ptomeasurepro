using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>Итог сверки спецификации с реестром материалов.</summary>
/// <param name="Created">Материалы, заведённые из спецификации.</param>
/// <param name="Matched">Позиции, которым нашёлся материал в реестре.</param>
/// <param name="Skipped">
/// Позиции, для которых материал не заводился: единица измерения не распознана,
/// поэтому непонятно, чем такую позицию мерить.
/// </param>
/// <param name="Log">Строки журнала событий для командной строки AutoCAD.</param>
public sealed record SpecificationSyncResult(
    IReadOnlyList<Material> Created,
    IReadOnlyList<SpecificationItem> Matched,
    IReadOnlyList<SpecificationItem> Skipped,
    IReadOnlyList<string> Log);

/// <summary>
/// Сверка позиций спецификации с реестром материалов: чего нет — заводится.
///
/// Смысл в том, чтобы спецификацию можно было замерять сразу после импорта.
/// Без этого каждую позицию, которой нет в реестре, пришлось бы заводить
/// руками через окно материалов, а в спецификации их бывают сотни.
///
/// Три правила, которые здесь важнее остальных:
///   • существующий материал НИКОГДА не изменяется — ни класс, ни единица,
///     ни характеристики. Реестр правит человек, и спецификация не вправе
///     затирать его работу;
///   • ключ реестра — наименование, поэтому и сопоставление идёт по нему.
///     Марка и изготовитель у заведённой позиции сохраняются справочно,
///     но собственной позицией реестра не становятся: иначе один и тот же
///     материал от двух поставщиков дал бы два разных слоя;
///   • позиция с нераспознанной единицей измерения пропускается. Завести
///     под неё материал значило бы засорить реестр строкой, которую нечем
///     замерить.
/// </summary>
public static class SpecificationRegistrySync
{
    /// <summary>
    /// Класс для линейной позиции спецификации.
    ///
    /// Спецификация говорит только «погонные метры» и не различает трубу,
    /// воздуховод и кабель. Класс нужен для вкладки в окне материалов, цвета
    /// и префикса слоя, поэтому берётся трубопровод — самый частый случай
    /// в этих спецификациях. Поправить класс можно в реестре, замер от этого
    /// не меняется: длина считается одинаково у всех линейных материалов.
    /// </summary>
    public const string DefaultLinearClass = MaterialClasses.Pipe;

    /// <summary>
    /// Завести в реестре материалы для позиций спецификации, которых там нет.
    /// Реестр сохраняется один раз, в конце.
    /// </summary>
    public static SpecificationSyncResult EnsureMaterials(
        MaterialRepository repository,
        Specification specification)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(specification);

        var created = new List<Material>();
        var matched = new List<SpecificationItem>();
        var skipped = new List<SpecificationItem>();
        var log = new List<string>();

        foreach (var item in specification.Items)
        {
            var existing = repository.FindByName(item.Name);
            if (existing is not null)
            {
                // Материал уже есть — используем как есть, ничего не переписываем.
                item.MaterialName = existing.Name;
                matched.Add(item);
                continue;
            }

            if (!item.IsSupported)
            {
                skipped.Add(item);
                continue;
            }

            // Пачка может содержать две строки с одним наименованием:
            // в спецификациях это встречается (одна позиция в двух разделах).
            var pending = created.FirstOrDefault(m =>
                string.Equals(m.Name, item.Name.Trim(), StringComparison.OrdinalIgnoreCase));

            if (pending is not null)
            {
                item.MaterialName = pending.Name;
                matched.Add(item);
                continue;
            }

            var material = CreateMaterial(item);
            created.Add(material);
            item.MaterialName = material.Name;
            log.Add($"Добавлен материал из спецификации: {material.Name} ({item.Unit})");
        }

        if (created.Count > 0) repository.AddRange(created);

        return new SpecificationSyncResult(created, matched, skipped, log);
    }

    /// <summary>Материал по позиции спецификации: только то, что спецификация знает.</summary>
    private static Material CreateMaterial(SpecificationItem item) => new()
    {
        Class = item.MeasurementType == MeasurementType.Pieces
            ? MaterialClasses.Piece
            : DefaultLinearClass,
        Name = item.Name.Trim(),
        Unit = item.Unit.Trim(),
        Mark = string.IsNullOrWhiteSpace(item.Mark) ? null : item.Mark.Trim(),
        Manufacturer = string.IsNullOrWhiteSpace(item.Manufacturer) ? null : item.Manufacturer.Trim()

        // Характеристики — диаметр, сечение, толщина — спецификация не содержит.
        // Они остаются пустыми, и имя слоя строится по запасному шаблону
        // от наименования; заполнить их можно в окне материалов.
    };
}
