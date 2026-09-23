namespace NetZapret.Core;

/// <summary>Одна запись каталога Zapret: имя, адрес и чей это ответ.</summary>
/// <param name="Service">Сервис каталога: «ChatGPT», «Spotify».</param>
/// <param name="Source">Набор: «XBOX DNS», «Comss DNS»; у зашитых — «каталог Zapret».</param>
/// <param name="Names">Сколько имён в каталоге стоит за этим адресом — посредник или сайт.</param>
public sealed record CatalogRecord(string Service, string Host, string Address, string Source, int Names);
