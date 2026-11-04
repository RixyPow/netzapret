namespace NetZapret.Core;

/// <summary>Откуда взялся адрес-кандидат для пина.</summary>
/// <remarks>
/// Порядок — порядок предпочтения при равном исходе проверки (PinPicker):
/// без посредника лучше, чем через него, известный про имя — лучше
/// пробуемого наугад.
/// </remarks>
public enum PinSource
{
    /// <summary>Настоящий адрес по DoH: без посредника, никто третий не видит, куда идём.</summary>
    Honest,

    /// <summary>Свой каталог, config/catalog.yaml.</summary>
    Own,

    /// <summary>Каталог Zapret, ответ набора именно для этого имени.</summary>
    Catalog,

    /// <summary>
    /// Посредник из каталога Zapret, про это имя ничего не обещавший.
    /// </summary>
    /// <remarks>
    /// Посредники смотрят на имя в приветствии TLS и сами идут к сайту,
    /// поэтому пропускают и то, чего в каталоге нет. Замер 23.09: XBOX DNS
    /// 87.228.47.195 отдал crunchyroll.com из Стокгольма, хотя в каталоге
    /// crunchyroll нет ни у одного набора.
    /// </remarks>
    Pool,
}

/// <summary>Кандидат: адрес и откуда он.</summary>
/// <param name="Label">Как назвать источник человеку: «XBOX DNS», «честный резолвер».</param>
/// <param name="Via">
/// Ключ посредника, отдавшего адрес сейчас (<c>comss</c>, <c>zapret</c>…), — профиль
/// пина, который пишется в hosts пометкой <c>nz:via=</c>; <c>null</c> — адрес не от
/// посредника. Отдельно от <paramref name="Label"/>: подпись для человека, ключ — для
/// автозамены, которая по нему знает, у кого спрашивать о пине.
/// </param>
public sealed record PinCandidate(string Address, PinSource Source, string Label, string? Via = null);
