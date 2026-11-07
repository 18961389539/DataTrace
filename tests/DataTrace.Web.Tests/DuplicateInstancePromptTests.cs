using DataTrace.Web.Services;

namespace DataTrace.Web.Tests;

public class DuplicateInstancePromptTests
{
    [Fact]
    public void MessageNamesTheDataDirectoryAndHolder()
    {
        var message = DuplicateInstancePrompt.FormatMessage(@"D:\DataTrace\data", 4321);

        Assert.Contains(@"D:\DataTrace\data", message);
        Assert.Contains("4321", message);
        Assert.Contains("不再启动", message);
    }

    [Fact]
    public void MessageSaysTheHolderIsUnknownWhenThePidCannotBeRead()
    {
        var message = DuplicateInstancePrompt.FormatMessage(@"D:\DataTrace\data", null);

        Assert.Contains("未知", message);
    }
}
