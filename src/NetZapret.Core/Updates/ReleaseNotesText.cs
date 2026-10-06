using System.Text.RegularExpressions;

namespace NetZapret.Core.Updates;

/// <summary>Кусок строки: текст и полужирный ли он.</summary>
public sealed record NotesSpan(string Text, bool Bold);

/// <summary>Что за блок чейнджлога.</summary>
public enum NotesBlockKind
{
    /// <summary>Заголовок раздела: «Новое», «Исправления».</summary>
    Heading,

    /// <summary>Абзац — у нас обычно полужирная фраза и пояснение.</summary>
    Paragraph,

    /// <summary>Пункт списка.</summary>
    Bullet,
}

/// <summary>Один блок чейнджлога.</summary>
public sealed record NotesBlock(NotesBlockKind Kind, IReadOnlyList<NotesSpan> Spans)
{
    /// <summary>Текст без разметки — для проверок и для копирования.</summary>
    public string Plain => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// Разбирает примечания к выпуску — ту малую часть Markdown, что в них бывает.
/// </summary>
/// <remarks>
/// <para>
/// Примечания — это <c>docs/release-notes.md</c> дословно (release.cmd
/// публикует его как есть): заголовок «# Что нового», разделы «## Новое:»
/// и «## Исправления:», абзацы с полужирной фразой в начале. Окно обновления
/// показывает их своим шрифтом, а не звёздочками и решётками.
/// </para>
/// <para>
/// Полноценный разбор Markdown здесь не нужен и вреден: чего мы не пишем,
/// то пусть и показывается как есть — текстом, а не угаданной разметкой.
/// </para>
/// </remarks>
public static class ReleaseNotesText
{
    public static IReadOnlyList<NotesBlock> Parse(string? notes)
    {
        var blocks = new List<NotesBlock>();

        if (string.IsNullOrWhiteSpace(notes))
            return blocks;

        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0)
                return;

            blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, Spans(string.Join(' ', paragraph))));
            paragraph.Clear();
        }

        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith('#'))
            {
                Flush();

                var level = line.TakeWhile(c => c == '#').Count();
                var text = line[level..].Trim().TrimEnd(':').Trim();

                // «# Что нового» — шапка всего файла: окно уже говорит это само.
                if (level >= 2 && text.Length > 0)
                    blocks.Add(new NotesBlock(NotesBlockKind.Heading, [new NotesSpan(text, true)]));

                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new NotesBlock(NotesBlockKind.Bullet, Spans(line[2..].Trim())));
                continue;
            }

            paragraph.Add(line);
        }

        Flush();
        return blocks;
    }

    /// <summary>Строка на куски по <c>**…**</c>; ссылки <c>[текст](адрес)</c> — их текстом.</summary>
    private static IReadOnlyList<NotesSpan> Spans(string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");

        var spans = new List<NotesSpan>();
        var parts = text.Split("**");

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;

            // Нечётные куски — между парой звёздочек. Непарная последняя пара
            // означает, что закрывающих не было: тогда текст обычный.
            bool bold = i % 2 == 1 && i < parts.Length - (parts.Length % 2 == 0 ? 1 : 0);

            spans.Add(new NotesSpan(parts[i], bold));
        }

        return spans;
    }
}
