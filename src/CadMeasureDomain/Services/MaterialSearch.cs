using CadMeasureDomain.Models;

namespace CadMeasureDomain.Services;

/// <summary>
/// Поиск материала по первым буквам нескольких слов.
///
/// Наименования в реестре длинные и точные — «Труба стальная электросварная
/// Dn80 (⌀88,9x3,5)». Набирать их целиком никто не станет, а искать по одной
/// подстроке неудобно: нужное слово может стоять в конце. Поэтому запрос
/// разбивается на слова, и каждое ищется как начало какого-нибудь слова
/// наименования, в том же порядке:
///
///   «бет»    → «Бетон М200», «Бетон М300», «Бетонная смесь»;
///   «бет м5» → «Бетон М500», но не «Бетон М200»;
///   «тр 88»  → «Труба стальная электросварная Dn80 (⌀88,9x3,5)».
///
/// Если ни одно слово не начинается с фрагмента, он ищется внутри слова:
/// без этого «арм 12» не нашло бы «Арматура А500С d12», где нужное число
/// приклеено к букве диаметра.
/// </summary>
public static class MaterialSearch
{
    private static readonly char[] WordSeparators =
    {
        ' ', '\t', '(', ')', ',', ';', '/', '\\', '«', '»', '"', '-', '—', '+'
    };

    /// <summary>Разбить наименование или запрос на слова.</summary>
    public static string[] SplitWords(string? text) =>
        (text ?? string.Empty).Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Подходит ли наименование под запрос.
    /// Пустой запрос подходит любому наименованию — это «показать всё».
    /// </summary>
    public static bool Matches(string? name, string? query)
    {
        var terms = SplitWords(query);
        if (terms.Length == 0) return true;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var words = SplitWords(name);

        // Слова запроса ищутся по порядку: следующее — только правее
        // предыдущего, иначе «м5 бет» находило бы то же, что «бет м5»,
        // а порядок слов в наименовании осмысленный.
        var from = 0;

        foreach (var term in terms)
        {
            var found = IndexOfWordStartingWith(words, term, from);

            // Запасной вариант — фрагмент внутри слова: «12» в «d12».
            if (found < 0) found = IndexOfWordContaining(words, term, from);
            if (found < 0) return false;

            from = found + 1;
        }

        return true;
    }

    /// <summary>Отобрать материалы, подходящие под запрос. Порядок сохраняется.</summary>
    public static IReadOnlyList<Material> Filter(IEnumerable<Material>? materials, string? query)
    {
        if (materials is null) return Array.Empty<Material>();

        return materials.Where(m => Matches(m?.Name, query)).ToList()!;
    }

    /// <summary>Отобрать наименования, подходящие под запрос.</summary>
    public static IReadOnlyList<string> FilterNames(IEnumerable<string>? names, string? query)
    {
        if (names is null) return Array.Empty<string>();

        return names.Where(name => Matches(name, query)).ToList();
    }

    private static int IndexOfWordStartingWith(string[] words, string term, int from)
    {
        for (var i = from; i < words.Length; i++)
            if (words[i].StartsWith(term, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }

    private static int IndexOfWordContaining(string[] words, string term, int from)
    {
        for (var i = from; i < words.Length; i++)
            if (words[i].Contains(term, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }
}
