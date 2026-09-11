using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetZapret.Core;

namespace NetZapret.Gui;

/// <summary>Одна подписка: имя, ссылка и то, раскрыта ли её папка.</summary>
public sealed class SubscriptionEntry
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Ссылка. Пароль: не печатается, не пишется в журнал, не показывается.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Состояние показа, а не настройка; хранится, чтобы папки не захлопывались при каждом заходе.</summary>
    public bool Open { get; set; } = true;
}

/// <summary>
/// Несколько подписок сразу.
/// </summary>
/// <remarks>
/// <para>
/// Отдельный файл, а не поле в <see cref="AppSettings"/>. Консоль знает одну
/// ссылку — <see cref="AppSettings.SubscriptionUrl"/>, — и по ней собирает
/// конфиг sing-box. Переучивать её ради окна значило бы править то, что
/// работает, поэтому здесь заведён свой список, а <c>SubscriptionUrl</c>
/// остаётся указателем на действующую. Обе стороны читают одно и то же
/// и не расходятся.
/// </para>
/// <para>
/// Отсюда следствие, которое видно в разделе VPN: сервер можно выбрать
/// только из действующей подписки. Выбор сервера из соседней делает
/// действующей её — иначе конфиг собрался бы без этого сервера, а окно
/// уверяло бы, что он выбран.
/// </para>
/// <para>
/// Файл лежит рядом с настройками и содержит пароли в открытом виде — ровно
/// как <c>netzapret.json</c> до него. Шифровать его нечем: ключ пришлось бы
/// хранить тут же, а хранилище учётных данных Windows привязано к пользователю
/// и не переживает переноса папки, ради которого программа и сделана
/// переносной.
/// </para>
/// </remarks>
public sealed class SubscriptionBook
{
    public List<SubscriptionEntry> Entries { get; set; } = [];

    public static string DefaultPath => Path.Combine("config", "subscriptions.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Читает список и подтягивает в него действующую подписку.
    /// </summary>
    /// <remarks>
    /// Подтягивает намеренно: у тех, кто настроился до появления этого файла,
    /// ссылка живёт только в <c>netzapret.json</c>, и без переноса раздел VPN
    /// показал бы пустоту человеку с работающим туннелем.
    /// </remarks>
    public static SubscriptionBook Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        SubscriptionBook book;

        try
        {
            book = File.Exists(target)
                ? JsonSerializer.Deserialize<SubscriptionBook>(File.ReadAllText(target), Options) ?? new()
                : new SubscriptionBook();
        }
        catch (Exception)
        {
            // Испорченный файл не повод не открыть раздел: действующая
            // подписка всё равно приедет из настроек строкой ниже.
            book = new SubscriptionBook();
        }

        book.Entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.Url));

        // WARP успел побыть отдельной строкой списка и оказался в этой роли
        // вреден: конфиг собирается по одной ссылке, и выбор его выхода делал
        // действующим его, отключая рабочую подписку целиком. Теперь это
        // выключатель, а старая строка переносится в него и убирается.
        if (book.Entries.RemoveAll(e => e.Url.StartsWith("warp://", StringComparison.OrdinalIgnoreCase)) > 0)
        {
            try
            {
                var moved = AppSettings.Load(AppSettings.DefaultPath);

                (moved with
                {
                    WarpEnabled = true,

                    // Если действующей была она, указатель повис бы на ссылку,
                    // которой больше нет, и раздел показал бы «ни одна
                    // не действует» при живых подписках.
                    SubscriptionUrl = moved.SubscriptionUrl?.StartsWith("warp://", StringComparison.OrdinalIgnoreCase) == true
                        ? book.Entries.FirstOrDefault()?.Url
                        : moved.SubscriptionUrl,
                }).Save(AppSettings.DefaultPath);

                book.Save(target);
            }
            catch (Exception)
            {
                // Перенос — удобство. Не вышло, значит выключатель просто
                // окажется выключенным, и его включат руками.
            }
        }

        var active = AppSettings.Load(AppSettings.DefaultPath).SubscriptionUrl;

        if (!string.IsNullOrWhiteSpace(active) && !book.Entries.Any(e => Same(e.Url, active)))
        {
            book.Entries.Insert(0, new SubscriptionEntry
            {
                Name = book.Entries.Count == 0 ? "Основная" : "Перенесённая",
                Url = active,
            });
        }

        return book;
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(target, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
    }

    /// <summary>Действующая — та, чью ссылку читает консоль.</summary>
    public SubscriptionEntry? Active(AppSettings settings) =>
        Entries.FirstOrDefault(e => Same(e.Url, settings.SubscriptionUrl));

    /// <summary>
    /// Делает подписку действующей.
    /// </summary>
    /// <remarks>
    /// Пишет её ссылку в настройки: там её ищет консоль, туда же смотрит
    /// сборка конфига sing-box.
    /// </remarks>
    public static void MakeActive(SubscriptionEntry entry)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath) with
        {
            SubscriptionUrl = entry.Url,

            // Прежний выбор сервера принадлежал прошлой подписке, и в новой
            // такого тега может не быть вовсе. Оставленный, он превратился бы
            // в ссылку в никуда: конфиг собрался бы без выбранного выхода,
            // а окно уверяло бы, что выход выбран.
            PreferredServer = null,
        };

        settings.Save(AppSettings.DefaultPath);
    }

    /// <summary>Счёт для показа. Ссылки не разглашает — в том и смысл.</summary>
    public string Describe(AppSettings settings)
    {
        if (Entries.Count == 0)
            return "нет";

        var active = Active(settings);

        return Entries.Count == 1
            ? active is null ? "1, ни одна не действует" : $"1: {active.Name}"
            : active is null
                ? $"{Entries.Count}, ни одна не действует"
                : $"{Entries.Count}, действует «{active.Name}»";
    }

    /// <summary>Имя, которого ещё нет в списке.</summary>
    public string FreeName()
    {
        for (int number = Entries.Count + 1; ; number++)
        {
            var name = $"Подписка {number}";

            if (!Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
}
