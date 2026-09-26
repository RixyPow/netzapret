using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подменён ли ответ DNS.
/// </summary>
/// <remarks>
/// <para>
/// Половина проверок здесь — о том, чего говорить НЕЛЬЗЯ. Ложное «подмена»
/// дороже пропущенной: оно уводит человека чинить то, что не сломано,
/// и уводит убедительно — вердикт-то звучит точно.
/// </para>
/// <para>
/// Сертификаты выписываются на месте, а не берутся из файлов. Так проверка
/// не зависит ни от сроков — самоподписанный протухает, и через год тест
/// покраснел бы сам собой, — ни от корней в системе.
/// </para>
/// </remarks>
public sealed class DnsSpoofTests
{
    /// <summary>Самоподписанный сертификат на перечисленные имена.</summary>
    private static X509Certificate2 CertificateFor(params string[] names)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={names[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var alt = new SubjectAlternativeNameBuilder();

        foreach (var name in names)
            alt.AddDnsName(name);

        request.CertificateExtensions.Add(alt.Build());

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static IPAddress[] Real => [IPAddress.Parse("104.18.32.7")];

    [Fact]
    public void A_certificate_for_the_name_is_clean()
    {
        using var cert = CertificateFor("example.com");

        Assert.Equal(DnsVerdict.Clean, DnsSpoof.Judge("example.com", Real, cert).Verdict);
    }

    [Fact]
    public void A_wildcard_certificate_covers_its_subdomains()
    {
        // Сертификаты крупных сайтов почти всегда со звёздочкой.
        // Не понять её значило бы объявлять подменой половину интернета.
        using var cert = CertificateFor("*.example.com", "example.com");

        Assert.Equal(DnsVerdict.Clean, DnsSpoof.Judge("cdn.example.com", Real, cert).Verdict);
        Assert.Equal(DnsVerdict.Clean, DnsSpoof.Judge("example.com", Real, cert).Verdict);
    }

    [Fact]
    public void A_certificate_with_many_names_is_read_whole()
    {
        // Один сертификат на десяток имён — обычное дело у сетей доставки.
        // Своя сверка, смотрящая только на CN, ошиблась бы здесь.
        using var cert = CertificateFor("first.example.com", "second.example.com", "third.example.com");

        Assert.Equal(DnsVerdict.Clean, DnsSpoof.Judge("third.example.com", Real, cert).Verdict);
    }

    [Fact]
    public void A_certificate_for_somebody_else_is_a_substitution()
    {
        // Так выглядит заглушка оператора: соединение встаёт, рукопожатие
        // проходит, а отвечает не тот. Подтвердить чужое имя нельзя —
        // для этого нужен закрытый ключ владельца.
        using var cert = CertificateFor("blockpage.provider.ru");

        var result = DnsSpoof.Judge("example.com", Real, cert);

        Assert.Equal(DnsVerdict.ForeignCertificate, result.Verdict);
        Assert.True(result.Spoofed);
        Assert.Contains("blockpage.provider.ru", result.Detail);
    }

    [Fact]
    public void Trust_is_not_the_question_here()
    {
        // Сертификаты в этой проверке самоподписанные, и все предыдущие
        // проверки зелены. Это и есть утверждение: вопрос здесь «тот ли
        // это сайт», а не «доверяем ли мы ему». Сайт с просроченной или
        // собственной подписью подменой не является.
        using var cert = CertificateFor("example.com");

        Assert.False(DnsSpoof.Judge("example.com", Real, cert).Spoofed);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("127.55.1.9")]
    public void Addresses_where_nobody_can_answer_are_a_substitution(string address)
    {
        var result = DnsSpoof.Judge("example.com", [IPAddress.Parse(address)], certificate: null);

        Assert.Equal(DnsVerdict.Sinkhole, result.Verdict);
        Assert.Contains(address, result.Detail);
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.3.4")]
    [InlineData("169.254.7.7")]
    public void Home_addresses_are_told_apart_from_dead_ones(string address)
    {
        // Мягкий признак: публичный сайт здесь жить не может, но тут же
        // стоят роутер и домашний сервер.
        var ip = IPAddress.Parse(address);

        Assert.False(DnsSpoof.IsSinkhole(ip));
        Assert.True(DnsSpoof.IsPrivate(ip));
    }

    [Theory]
    [InlineData("104.18.32.7")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("173.194.73.100")]
    public void Ordinary_addresses_are_not_accused(string address)
    {
        var ip = IPAddress.Parse(address);

        Assert.False(DnsSpoof.IsSinkhole(ip));
        Assert.False(DnsSpoof.IsPrivate(ip));
    }

    [Fact]
    public void Different_addresses_for_one_name_are_not_a_substitution()
    {
        // Сеть доставки отдаёт ближайший узел, у крупных имён адресов
        // десятки, и расхождение двух ответов само по себе не значит
        // ничего. Признак — «по адресу живёт не тот», а не «ответы разошлись».
        using var cert = CertificateFor("*.example.com");

        var result = DnsSpoof.Judge(
            "cdn.example.com",
            [IPAddress.Parse("104.18.32.7"), IPAddress.Parse("172.67.1.2")],
            cert);

        Assert.Equal(DnsVerdict.Clean, result.Verdict);
    }

    [Fact]
    public void Without_a_handshake_nothing_is_claimed()
    {
        // Адреса настоящие, рукопожатия не было. Чужой сертификат
        // предъявляют именно в нём, и его отсутствие означает незнание,
        // а не отсутствие подмены.
        var result = DnsSpoof.Judge("example.com", Real, certificate: null);

        Assert.Equal(DnsVerdict.Unknown, result.Verdict);
        Assert.False(result.Spoofed);
    }

    [Fact]
    public void Without_addresses_nothing_is_claimed_either()
    {
        Assert.Equal(DnsVerdict.Unknown, DnsSpoof.Judge("example.com", null, null).Verdict);
        Assert.Equal(DnsVerdict.Unknown, DnsSpoof.Judge("example.com", [], null).Verdict);
    }

    [Fact]
    public void A_dead_address_outweighs_a_matching_certificate()
    {
        // Порядок именно такой: служебный адрес виден до всякого соединения
        // и объясняет заодно, почему соединения не вышло.
        using var cert = CertificateFor("example.com");

        var result = DnsSpoof.Judge("example.com", [IPAddress.Parse("127.0.0.1")], cert);

        Assert.Equal(DnsVerdict.Sinkhole, result.Verdict);
    }

    [Fact]
    public void The_probe_and_the_check_agree_on_what_a_dead_address_is()
    {
        // Вопрос один — может ли по адресу кто-нибудь ответить, — и два
        // ответа на него дали бы отчёт, спорящий сам с собой.
        foreach (var address in new[] { "0.0.0.0", "127.0.0.1", "8.8.8.8", "192.168.1.1" })
        {
            var ip = IPAddress.Parse(address);

            Assert.Equal(DnsSpoof.IsSinkhole(ip), BlockCheck.IsStub(ip));
        }
    }

    /// <summary>
    /// Подмену устроил пин в hosts — совет называет его, а не зовёт прибивать.
    /// </summary>
    /// <remarks>
    /// 26.09: jetbrains.com был прибит чужой записью к 72.56.93.144, где
    /// отвечает чужой сервер. Совет «прибейте настоящий адрес» уводил
    /// в сторону — прибито было, и именно это и мешало.
    /// </remarks>
    [Fact]
    public void A_pinned_spoof_points_at_the_pin()
    {
        var result = new DnsSpoofResult
        {
            Verdict = DnsVerdict.ForeignCertificate,
            Detail = "по адресу отвечает не «jetbrains.com», а «instaposts.halahala.ru»",
            PinnedTo = "72.56.93.144",
            Honest = "63.33.88.220",
        };

        Assert.Contains("прибито в hosts к 72.56.93.144", result.Advice);
        Assert.Contains("удалите эту запись", result.Advice);
        Assert.Contains("63.33.88.220", result.Advice);
        Assert.DoesNotContain("прибейте", result.Advice);
    }

    /// <summary>Без пина — прежний совет, с честным адресом, если он найден.</summary>
    [Fact]
    public void An_unpinned_spoof_suggests_pinning_the_honest_address()
    {
        var result = new DnsSpoofResult
        {
            Verdict = DnsVerdict.ForeignCertificate,
            Detail = "по адресу отвечает не «a.example», а «b.example»",
            Honest = "1.2.3.4",
        };

        Assert.Contains("прибейте настоящий адрес (1.2.3.4)", result.Advice);
        Assert.Contains("VPN", result.Advice);
    }
}
