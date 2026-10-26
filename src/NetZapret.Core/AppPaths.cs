namespace NetZapret.Core;

/// <summary>
/// Каталог программы — то, от чего ищутся пресеты, движки и установка.
/// </summary>
/// <remarks>
/// На Windows это каталог сборки (<see cref="AppContext.BaseDirectory"/>),
/// и значение по умолчанию ровно оно. На Android сборки лежат внутри APK:
/// каталога «рядом с программой», куда можно положить presets/ и engines/,
/// нет. Приложение распаковывает их в свою папку данных и ставит её сюда
/// до первого обращения к библиотекам — <c>ZapretPaths.PresetDirectory</c>
/// читает значение один раз.
/// </remarks>
public static class AppPaths
{
    /// <summary>Каталог программы; по умолчанию — каталог сборки.</summary>
    public static string ProgramDirectory { get; set; } = AppContext.BaseDirectory;
}
