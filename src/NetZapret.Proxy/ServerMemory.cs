using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>
/// Память замеров серверов: забывать старые проверки и стирать всё кнопкой.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 07.10: «добавь возможность очистить память работы серверов (то что
/// показывает через раз и т.д.), а так же настраиваемое автоочищение …
/// с умолчанием раз в неделю». Память — <see cref="ServerHealthCache"/>:
/// задержки и исходы последних десяти проверок. По ней сервер становится
/// «нестабильным» и уходит из автоподбора, «мёртвым» — тоже, а починившись,
/// сам оттуда почти не выбирается: мимо автоподбора его проверяют редко.
/// </para>
/// <para>
/// Сперва автоочистка стирала память целиком раз в срок; в тот же день
/// владелец: «можно сделать так чтобы подчищало именно старые замеры а не всю
/// память?». Теперь по сроку забываются только проверки старше него — у сервера
/// остаются свежие, и давние промахи перестают держать его «нестабильным».
/// Целиком память стирает только кнопка.
/// </para>
/// </remarks>
public static class ServerMemory
{
    /// <summary>Срок по умолчанию — неделя.</summary>
    public const int DefaultDays = 7;

    /// <summary>
    /// Забывает проверки старше срока из настроек; срок 0 — ничего.
    /// </summary>
    /// <returns>Сколько проверок забыто.</returns>
    public static int ForgetOld(DateTimeOffset now, string? settingsPath = null, string? healthPath = null)
    {
        var settings = AppSettings.TryLoad(settingsPath ?? AppSettings.DefaultPath, out var read);

        // Не прочитались настройки — срока не знаем, и забывать не по чему.
        if (read == AppSettings.ReadResult.Unreadable || settings.ServerMemoryDays <= 0)
            return 0;

        return ServerHealthCache.Prune(now - TimeSpan.FromDays(settings.ServerMemoryDays), healthPath);
    }

    /// <summary>Стирает всю память замеров — кнопка «Очистить».</summary>
    public static void ClearAll(string? healthPath = null) => ServerHealthCache.Clear(healthPath);
}
