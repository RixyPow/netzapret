using System.Xml.Linq;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

public class AutostartTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static AutostartOptions Options(string arguments = "start --proxy-config C:\\p\\singbox.json") => new()
    {
        ExecutablePath = @"C:\Users\rogfa\Projects\netzapret\netzapret.exe",
        Arguments = arguments,
        WorkingDirectory = @"C:\Users\rogfa\Projects\netzapret",
        UserId = @"ARIS\rogfa",
    };

    private static XElement Parse(AutostartOptions options) =>
        XDocument.Parse(AutostartTask.BuildXml(options)).Root!;

    [Fact]
    public void TaskIsRegisteredWithHighestPrivileges()
    {
        // Без этого не поднимутся ни TUN, ни WinDivert.
        var root = Parse(Options());
        var runLevel = root.Descendants(Ns + "RunLevel").Single().Value;

        Assert.Equal("HighestAvailable", runLevel);
    }

    [Fact]
    public void ExecutionTimeIsUnlimited()
    {
        // По умолчанию планировщик убивает задачу через трое суток —
        // для постоянно работающего десинка это неприемлемо.
        var root = Parse(Options());

        Assert.Equal("PT0S", root.Descendants(Ns + "ExecutionTimeLimit").Single().Value);
    }

    [Fact]
    public void BatteryDoesNotStopTheTask()
    {
        // Иначе на ноутбуке без питания десинк просто не запустится.
        var root = Parse(Options());

        Assert.Equal("false", root.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", root.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
    }

    [Fact]
    public void TriggerIsLogonForTheSameUser()
    {
        var root = Parse(Options());
        var trigger = root.Descendants(Ns + "LogonTrigger").Single();

        Assert.Equal(@"ARIS\rogfa", trigger.Element(Ns + "UserId")!.Value);
    }

    [Fact]
    public void WorkingDirectoryIsSetSoRelativePathsResolve()
    {
        var root = Parse(Options());

        Assert.Equal(
            @"C:\Users\rogfa\Projects\netzapret",
            root.Descendants(Ns + "WorkingDirectory").Single().Value);
    }

    [Fact]
    public void SecondInstanceIsNotStarted()
    {
        // Два супервизора подрались бы за WinDivert и за файл состояния.
        var root = Parse(Options());

        Assert.Equal("IgnoreNew", root.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
    }

    [Fact]
    public void ArgumentsWithQuotesAndAmpersandsAreEscaped()
    {
        // Название пресета приходит от пользователя и попадает прямо в XML.
        var root = Parse(Options("start --preset \"Universal V6 & co\""));
        var arguments = root.Descendants(Ns + "Arguments").Single().Value;

        Assert.Equal("start --preset \"Universal V6 & co\"", arguments);
    }

    [Fact]
    public void TaskRunsHiddenWithoutAConsoleWindow()
    {
        var root = Parse(Options());

        Assert.Equal("true", root.Descendants(Ns + "Hidden").Single().Value);
    }

    [Fact]
    public void GeneratedXmlIsWellFormed()
    {
        // Планировщик отвергает описание целиком при малейшей ошибке разметки.
        var exception = Record.Exception(() => Parse(Options("start --preset \"<Тест & 'кавычки'>\"")));

        Assert.Null(exception);
    }
}
