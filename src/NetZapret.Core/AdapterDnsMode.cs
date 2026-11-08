namespace NetZapret.Core;

/// <summary>Что программа делает с DNS адаптеров Windows (<see cref="AppSettings.AdapterDns"/>).</summary>
public enum AdapterDnsMode
{
    /// <summary>Не трогать: что вписано в свойствах адаптера, то и стоит.</summary>
    Keep,

    /// <summary>Автоматически: DNS выдаёт сеть — обычно роутер, а за ним провайдер.</summary>
    Automatic,

    /// <summary>Вписать выбранный на вкладке DNS резолвер — его обычные адреса по IPv4.</summary>
    Resolver,
}

/// <summary>Слова для выбора DNS адаптеров — одни в окне и в журнале.</summary>
public static class AdapterDnsModes
{
    public static string Word(AdapterDnsMode mode) => mode switch
    {
        AdapterDnsMode.Automatic => "автоматически",
        AdapterDnsMode.Resolver => "выбранный резолвер",
        _ => "не трогать",
    };
}
