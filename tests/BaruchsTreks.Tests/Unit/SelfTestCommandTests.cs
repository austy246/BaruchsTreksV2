using BaruchsTreks.Cli;

namespace BaruchsTreks.Tests.Unit;

public class SelfTestCommandTests
{
    [Fact]
    public void Run_AllChecksPass_OneLinePerCheck()
    {
        var output = new StringWriter();

        var exitCode = SelfTestCommand.Run(output);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(exitCode == 0, output.ToString());
        Assert.Equal(5, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("OK ", line));
    }

    [Fact]
    public void SampleHeic_IsPublishedNextToTheApp()
    {
        Assert.True(File.Exists(SelfTestCommand.SampleHeicPath), SelfTestCommand.SampleHeicPath);
    }

    [Fact]
    public void SampleHeic_MatchesTheTestFixture()
    {
        var fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.heic"));

        Assert.Equal(fixture, File.ReadAllBytes(SelfTestCommand.SampleHeicPath));
    }
}
