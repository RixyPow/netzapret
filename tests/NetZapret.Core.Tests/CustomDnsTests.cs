using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Свои резолверы: запись, выбор для туннеля, отказ на кривом вводе.
/// </summary>
/// <remarks>
/// Владелец 26.09, после отзыва в обсуждении #7: выбрать можно было только
/// из вписанных в код. Апстрим туннеля — только DoH, и имя для сертификата
/// обязательно: без него резолвер годится лишь в обзор.
/// </remarks>
public sealed class CustomDnsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"netzapret-dns-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var f in new[] { _file, _file + ".broken" })
        {
            if (File.Exists(f))
                File.Delete(f);
        }
    }

    [Fact]
    public void A_resolver_with_doh_can_be_chosen_for_the_tunnel()
    {
        var (provider, problem) = CustomDns.Build("Мой", "111.88.96.50", "https://xbox-dns.ru/dns-query", null, "111.88.96.51");

        Assert.Null(problem);
        Assert.True(provider!.Choosable);
        Assert.True(provider.Own);
        Assert.Equal("xbox-dns.ru", provider.TlsName);
        Assert.Equal("/dns-query", provider.DohPath);
        Assert.Equal(["111.88.96.50", "111.88.96.51"], provider.Udp);
    }

    /// <summary>Без имени DoH — только в обзор: туннелю сертификат проверить нечем.</summary>
    [Fact]
    public void Without_doh_it_is_for_the_survey_only()
    {
        var (provider, _) = CustomDns.Build("Мой", "62.76.76.62", null, null);

        Assert.False(provider!.Choosable);
        Assert.Contains("без DoH", provider.Note);
    }

    [Theory]
    [InlineData("dns.example.com")]
    [InlineData("не-адрес")]
    [InlineData("")]
    public void An_address_must_be_digits(string address)
    {
        var (provider, problem) = CustomDns.Build("Мой", address, null, null);

        Assert.Null(provider);
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_bad_doh_name_is_refused()
    {
        Assert.Null(CustomDns.Build("Мой", "1.2.3.4", "localhost", null).Provider);
        Assert.Null(CustomDns.Build("Мой", "1.2.3.4", "dns example.com", null).Provider);
    }

    /// <summary>Записанный читается обратно; тот же адрес — заменяет, не дублирует.</summary>
    [Fact]
    public void Saved_resolvers_come_back_and_the_same_address_replaces()
    {
        CustomDns.Save(CustomDns.Build("Первый", "1.2.3.4", "dns.one.example", null).Provider!, _file);
        CustomDns.Save(CustomDns.Build("Второй", "5.6.7.8", null, null).Provider!, _file);
        CustomDns.Save(CustomDns.Build("Первый заново", "1.2.3.4", "dns.one.example", "q").Provider!, _file);

        var loaded = CustomDns.Load(_file);

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, p => p.Name == "Первый заново" && p.DohPath == "/q");
        Assert.All(loaded, p => Assert.True(p.Own));
    }

    [Fact]
    public void Removing_takes_only_that_address()
    {
        CustomDns.Save(CustomDns.Build("А", "1.2.3.4", null, null).Provider!, _file);
        CustomDns.Save(CustomDns.Build("Б", "5.6.7.8", null, null).Provider!, _file);

        Assert.True(CustomDns.Remove("1.2.3.4", _file));
        Assert.False(CustomDns.Remove("9.9.9.9", _file));

        Assert.Equal(["5.6.7.8"], CustomDns.Load(_file).Select(p => p.Udp[0]));
    }

    /// <summary>Испорченный файл не роняет ни чтение, ни следующую запись — и не затирается.</summary>
    [Fact]
    public void A_broken_file_is_set_aside_not_lost()
    {
        File.WriteAllText(_file, "{ это не json");

        Assert.Empty(CustomDns.Load(_file));

        CustomDns.Save(CustomDns.Build("А", "1.2.3.4", null, null).Provider!, _file);

        Assert.True(File.Exists(_file + ".broken"));
        Assert.Single(CustomDns.Load(_file));
    }

    /// <summary>Встроенные остаются первыми: выбор, записанный до своих, читается как прежде.</summary>
    [Fact]
    public void Built_in_providers_come_first()
    {
        Assert.Equal(DnsSurvey.Providers.Count, DnsSurvey.All.Take(DnsSurvey.Providers.Count).Count());
        Assert.Equal("Google", DnsSurvey.All[0].Name);
    }
}
