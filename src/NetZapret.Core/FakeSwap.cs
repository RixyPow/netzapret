namespace NetZapret.Core;

/// <summary>
/// Подделка, выбранная вместо пресетовской в одном месте секции.
/// </summary>
/// <param name="Preset">Пресет — по имени файла, как <see cref="AppSettings.PresetName"/>.</param>
/// <param name="Section">Значение <c>--name</c> секции.</param>
/// <param name="Slot">Номер шага с <c>blob=</c> в секции, с нуля.</param>
/// <param name="Blob">Файл из <c>bin\</c> (<c>stun2.bin</c>) или встроенное имя winws2.</param>
public sealed record FakeSwap(string Preset, string Section, int Slot, string Blob);
