using System.Security.Cryptography.X509Certificates;

namespace NetZapret.Core.Diagnostics;

/// <summary>Найденный сертификат НУЦ Минцифры.</summary>
/// <param name="Subject">Имя сертификата, как его покажет Windows.</param>
/// <param name="Place">Где лежит — словами окна сертификатов Windows.</param>
/// <param name="ForAllUsers">В хранилище компьютера (удалять — с правами администратора, через certlm.msc).</param>
public sealed record RussianRootCertificate(string Subject, string Place, bool ForAllUsers, string Thumbprint);

/// <summary>
/// Ищет в системе корневой сертификат НУЦ Минцифры (Russian Trusted Root CA).
/// </summary>
/// <remarks>
/// <para>
/// Корень в хранилище доверия позволяет тому, у кого ключ, выпустить
/// сертификат на любой сайт — и браузер примет его без предупреждения.
/// Это путь к перехвату HTTPS, в том числе на уровне ТСПУ (вики Zapret GUI,
/// раздел о сертификатах НУЦ, 2026).
/// </para>
/// <para>
/// Удаление тоже имеет цену: с 3 августа 2026 Сбербанк, ВТБ и другие банки
/// перешли на сертификаты НУЦ, и без корня их сайты в Chrome и Edge
/// не откроются. Поэтому решает человек, а программа только показывает —
/// удалять за него системный сертификат она не вправе.
/// </para>
/// </remarks>
public static class RussianRoot
{
    /// <summary>Приметы в имени: корень и промежуточный центр НУЦ.</summary>
    private static readonly string[] Names = ["Russian Trusted Root CA", "Russian Trusted Sub CA"];

    public static IReadOnlyList<RussianRootCertificate> Find()
    {
        var found = new List<RussianRootCertificate>();

        foreach (var (location, forAll) in new[] { (StoreLocation.CurrentUser, false), (StoreLocation.LocalMachine, true) })
        {
            foreach (var (store, place) in new[]
                     {
                         (StoreName.Root, "Доверенные корневые центры сертификации"),
                         (StoreName.CertificateAuthority, "Промежуточные центры сертификации"),
                     })
            {
                try
                {
                    using var opened = new X509Store(store, location);
                    opened.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                    foreach (var certificate in opened.Certificates)
                    {
                        using (certificate)
                        {
                            if (!IsRussianRoot(certificate.Subject))
                                continue;

                            // Хранилище пользователя показывает и сертификаты компьютера,
                            // поэтому один и тот же встречается дважды. Оставляем
                            // запись компьютера: удалять его придётся там.
                            found.RemoveAll(f => f.Thumbprint == certificate.Thumbprint && f.Place == place);

                            found.Add(new(
                                certificate.GetNameInfo(X509NameType.SimpleName, false),
                                place,
                                forAll,
                                certificate.Thumbprint));
                        }
                    }
                }
                catch (Exception)
                {
                    // Хранилища нет или оно закрыто — искать в нём нечего.
                }
            }
        }

        return found;
    }

    internal static bool IsRussianRoot(string subject) =>
        Names.Any(n => subject.Contains(n, StringComparison.OrdinalIgnoreCase));
}
