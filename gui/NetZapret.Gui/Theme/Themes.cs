using System.Windows;

namespace NetZapret.Gui;

/// <summary>Какая палитра сейчас применена.</summary>
public enum ThemeKind
{
    Dark,
    Light,
}

/// <summary>
/// Переключает палитру окна.
/// </summary>
/// <remarks>
/// <para>
/// Подменяется ровно один словарь из двух. Ключи в тёмной и светлой палитрах
/// совпадают до последнего, а <c>Controls.xaml</c> опирается только на них
/// и про тему не знает вовсе — поэтому смена темы это замена палитры,
/// а не второй набор стилей, который пришлось бы править парами.
/// </para>
/// <para>
/// Применяется сразу и целиком: разметка ссылается на кисти через
/// <c>StaticResource</c>, но подмена словаря в <c>Application.Resources</c>
/// перестраивает дерево ресурсов, и уже созданные окна перечитывают его сами.
/// Перезапуск не нужен.
/// </para>
/// </remarks>
public static class Themes
{
    private const string DarkPath = "Theme/Palette.xaml";
    private const string LightPath = "Theme/Light.xaml";

    /// <summary>Применяет палитру; безопасно вызывать до появления окон.</summary>
    public static void Apply(ThemeKind kind)
    {
        var application = Application.Current;

        if (application is null)
            return;

        var wanted = new Uri(kind == ThemeKind.Light ? LightPath : DarkPath, UriKind.Relative);
        var merged = application.Resources.MergedDictionaries;

        // Палитра узнаётся по ключу, а не по месту в списке: словари могут
        // переставить, и подмена по индексу однажды заменила бы стили.
        for (int i = 0; i < merged.Count; i++)
        {
            if (!merged[i].Contains("BackdropColor"))
                continue;

            if (merged[i].Source == wanted)
                return;

            merged[i] = new ResourceDictionary { Source = wanted };
            return;
        }
    }

    /// <summary>Разбирает значение из настроек; неизвестное — тёмная.</summary>
    public static ThemeKind Parse(string? value) =>
        string.Equals(value?.Trim(), "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeKind.Light
            : ThemeKind.Dark;

    /// <summary>Как тема записывается в настройки.</summary>
    public static string Describe(ThemeKind kind) => kind == ThemeKind.Light ? "light" : "dark";
}
