using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Сравнение версий.
/// </summary>
/// <remarks>
/// Сеть здесь не участвует: запрос к GitHub проверяется живым запуском,
/// а разбор номера — вот этим. Ошибка тут дороже, чем кажется: неверное
/// сравнение либо не покажет вышедшую починку, либо предложит откат.
/// </remarks>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.3.2", "0.3.1", true)]
    [InlineData("0.4.0", "0.3.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.3.1", "0.3.1", false)]
    [InlineData("0.3.0", "0.3.1", false)]
    public void ComparesByNumber(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateCheck.IsNewer(candidate, current));
    }

    /// <summary>Числами, а не строками.</summary>
    /// <remarks>
    /// Как строка «0.10.0» меньше «0.9.0», и посимвольное сравнение спрятало бы
    /// от человека десятый выпуск подряд — ровно тогда, когда накопится больше
    /// всего починок.
    /// </remarks>
    [Fact]
    public void TenIsNewerThanNine()
    {
        Assert.True(UpdateCheck.IsNewer("0.10.0", "0.9.0"));
        Assert.False(UpdateCheck.IsNewer("0.9.0", "0.10.0"));
    }

    [Fact]
    public void LeadingVeeIsAccepted()
    {
        Assert.True(UpdateCheck.IsNewer("v0.3.2", "0.3.1"));
        Assert.True(UpdateCheck.IsNewer("0.3.2", "v0.3.1"));
    }

    /// <summary>Мусор не считается новее.</summary>
    /// <remarks>
    /// Ошибиться в эту сторону безопаснее: не показать обновление досадно,
    /// а предложить установить неизвестно что — уже опасно.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("не версия")]
    public void UnparsableIsNotNewer(string candidate)
    {
        Assert.False(UpdateCheck.IsNewer(candidate, "0.3.1"));
    }

    /// <summary>Настройки и правила не перезаписываются обновлением.</summary>
    /// <remarks>
    /// Перечислены поимённо, а не оставлены на усмотрение сборщика архива:
    /// обновление, стирающее подписку, хуже отсутствия обновления, и связывать
    /// два сценария молчаливым уговором значит однажды его нарушить.
    /// </remarks>
    [Fact]
    public void SettingsAreNeverOverwritten()
    {
        var names = UpdateInstaller.Preserved.Select(Path.GetFileName).ToList();

        Assert.Contains("netzapret.json", names);
        Assert.Contains("rules.user.yaml", names);
        Assert.Contains("addresses.yaml", names);

        // Каталог адресов забыли при его появлении, и обновление стёрло бы
        // ровно тот файл, в который мы сами зовём дописывать найденные
        // рабочие адреса. Здесь он назван поимённо по той же причине,
        // что и остальные: список должен меняться осознанно.
        Assert.Contains("catalog.yaml", names);
    }

    /// <summary>Каждый сохраняемый файл лежит в config.</summary>
    /// <remarks>
    /// Обновление заменяет всё, кроме перечисленного, и путь вне config
    /// означал бы, что мы обходим стороной часть самой программы.
    /// </remarks>
    [Fact]
    public void PreservedFilesAllLiveInConfig()
    {
        Assert.All(
            UpdateInstaller.Preserved,
            path => Assert.Equal("config", Path.GetDirectoryName(path)));
    }
}

/// <summary>
/// Отсев отечественных выходов из автоподбора.
/// </summary>
/// <remarks>
/// Автоподбор идёт по задержке, а меньше всего она у ближайшего сервера —
/// своего же. Наблюдалось вживую: селектор раз за разом возвращался
/// на российский сервер, и ChatGPT отвечал тем же отказом, что и без
/// туннеля, — адрес-то оставался российским.
/// </remarks>
public class ForeignExitTests
{
    [Theory]
    [InlineData("🇷🇺 Hysteria2 | Россия", true)]
    [InlineData("🇷🇺 Россия", true)]
    [InlineData("Russia VLESS", true)]
    [InlineData("🇸🇪 Hysteria2 | Швеция", false)]
    [InlineData("🇳🇱 Нидерланды", false)]
    [InlineData("🇭🇰 Trojan | Гонконг", false)]
    public void DomesticServersAreRecognisedByName(string tag, bool expected)
    {
        Assert.Equal(expected, NetZapret.Proxy.SingBoxConfigCompiler.LooksDomestic(tag));
    }

    /// <summary>Названный американским, а выходящий дома, отсевом не ловится.</summary>
    /// <remarks>
    /// Признаётся вслух: «США (вход РФ)» по замерам выходит в России, но зовётся
    /// американским, и по названию его не отличить. Отсев — не гарантия,
    /// а отбрасывание очевидного; страну выхода проверка спрашивает отдельно.
    /// </remarks>
    [Fact]
    public void NamingCannotCatchEverything()
    {
        Assert.False(NetZapret.Proxy.SingBoxConfigCompiler.LooksDomestic("🇺🇸 Hysteria2 | США (вход РФ)"));
    }
}
