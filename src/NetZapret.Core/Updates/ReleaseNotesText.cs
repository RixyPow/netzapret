using System.Text;
using System.Text.RegularExpressions;

namespace NetZapret.Core.Updates;

/// <summary>Кусок строки: текст, полужирный ли он и код ли это.</summary>
public sealed record NotesSpan(string Text, bool Bold, bool Code = false);

/// <summary>Что за блок чейнджлога.</summary>
public enum NotesBlockKind
{
    /// <summary>Заголовок раздела: «Новое», «Исправления».</summary>
    Heading,

    /// <summary>Абзац — у нас обычно полужирная фраза и пояснение.</summary>
    Paragraph,

    /// <summary>Пункт списка.</summary>
    Bullet,

    /// <summary>Блок кода между тройными обратными кавычками — строки как есть.</summary>
    Code,
}

/// <summary>Один блок чейнджлога.</summary>
public sealed record NotesBlock(NotesBlockKind Kind, IReadOnlyList<NotesSpan> Spans)
{
    /// <summary>Текст без разметки — для проверок и для копирования.</summary>
    public string Plain => string.Concat(Spans.Select(s => s.Text));

    /// <summary>
    /// Полужирная фраза в начале абзаца — заголовок пункта; <c>null</c> — её нет.
    /// </summary>
    /// <remarks>
    /// Так пишется чейнджлог (CLAUDE.md, «Чейнджлог»): «**Что изменилось.** Пояснение».
    /// Окно показывает фразу строкой над пояснением: в Bahnschrift полужирное
    /// от обычного на глаз почти не отличается (владелец 06.10: «добавь
    /// форматирование текста»), а строка — отличается.
    /// </remarks>
    public string? Lead => Kind == NotesBlockKind.Paragraph && Spans.Count > 0 && Spans[0] is { Bold: true, Code: false } first
        ? first.Text.Trim()
        : null;

    /// <summary>Всё после <see cref="Lead"/>; без неё — все куски.</summary>
    public IReadOnlyList<NotesSpan> Body => Lead is null ? Spans : Spans.Skip(1).ToList();
}

/// <summary>
/// Разбирает примечания к выпуску — ту малую часть Markdown, что в них бывает.
/// </summary>
/// <remarks>
/// <para>
/// Примечания — это <c>docs/release-notes.md</c> дословно (release.cmd
/// публикует его как есть), а после него — «Чем это собрано и как сверить»
/// с хэшами и командами в блоках кода. Окно обновления показывает их своим
/// шрифтом, а не звёздочками, решётками и обратными кавычками.
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
        List<string>? code = null;

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

            // Внутри блока кода строки идут как есть, до закрывающих кавычек.
            if (code is not null)
            {
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(new NotesBlock(NotesBlockKind.Code, [new NotesSpan(string.Join('\n', code), false, true)]));
                    code = null;
                }
                else
                {
                    code.Add(raw.TrimEnd());
                }

                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();

                // «```код```» одной строкой — тоже блок.
                var rest = line[3..];

                if (rest.Length > 3 && rest.EndsWith("```", StringComparison.Ordinal))
                    blocks.Add(new NotesBlock(NotesBlockKind.Code, [new NotesSpan(rest[..^3].Trim(), false, true)]));
                else
                    code = [];

                continue;
            }

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

        // Незакрытый блок кода — всё равно код: потерять хэши хуже, чем
        // показать их без закрывающей рамки.
        if (code is { Count: > 0 })
            blocks.Add(new NotesBlock(NotesBlockKind.Code, [new NotesSpan(string.Join('\n', code), false, true)]));

        return blocks;
    }

    /// <summary>
    /// Сколько пунктов в разделах «Новое» и «Исправления» — для ленты версий.
    /// </summary>
    /// <remarks>
    /// Пункт — абзац с полужирной фразой в начале либо строка списка: так
    /// чейнджлог и пишется. Прочие разделы («Чем это собрано…») не считаются.
    /// </remarks>
    public static (int Added, int Fixed) Count(string? notes)
    {
        int added = 0, fixedCount = 0;
        string? section = null;

        foreach (var block in Parse(notes))
        {
            if (block.Kind == NotesBlockKind.Heading)
            {
                var head = block.Plain.Trim();

                section = head.StartsWith("Нов", StringComparison.OrdinalIgnoreCase) ? "added"
                    : head.StartsWith("Исправ", StringComparison.OrdinalIgnoreCase) ? "fixed"
                    : null;

                continue;
            }

            bool item = block.Kind == NotesBlockKind.Bullet || block.Lead is not null;

            if (!item)
                continue;

            if (section == "added")
                added++;
            else if (section == "fixed")
                fixedCount++;
        }

        return (added, fixedCount);
    }

    /// <summary>
    /// Строка на куски: <c>**полужирное**</c>, <c>`код`</c>; ссылки <c>[текст](адрес)</c> — их текстом.
    /// </summary>
    /// <remarks>
    /// Внутри кода звёздочки — просто звёздочки. Незакрытая пара не меняет
    /// ничего: текст после неё остаётся обычным.
    /// </remarks>
    private static IReadOnlyList<NotesSpan> Spans(string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");

        var spans = new List<NotesSpan>();
        var buffer = new StringBuilder();
        bool bold = false, code = false;
        int boldFrom = -1, codeFrom = -1;

        void Emit()
        {
            if (buffer.Length > 0)
                spans.Add(new NotesSpan(buffer.ToString(), bold && !code, code));

            buffer.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                Emit();
                code = !code;
                codeFrom = code ? spans.Count : -1;
                continue;
            }

            if (!code && text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                Emit();
                bold = !bold;
                boldFrom = bold ? spans.Count : -1;
                i++;
                continue;
            }

            buffer.Append(text[i]);
        }

        Emit();

        // Незакрытое — откатываем до обычного текста с того места, где открыли.
        if (code && codeFrom >= 0)
        {
            for (int i = codeFrom; i < spans.Count; i++)
                spans[i] = spans[i] with { Code = false, Text = (i == codeFrom ? "`" : string.Empty) + spans[i].Text };
        }

        if (bold && boldFrom >= 0)
        {
            for (int i = boldFrom; i < spans.Count; i++)
                spans[i] = spans[i] with { Bold = false };
        }

        return spans;
    }
}
