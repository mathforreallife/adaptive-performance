using Xunit;

namespace Gapo.SourceRules.Tests;

public sealed class OtherScannerTests
{
    private const string Hash = "#";
    private static readonly string MarkupOpen = "<!" + "--";
    private static readonly string Slashes = new('/', 2);

    [Fact]
    public void MarkupScannerReportsCommentWithLineNumber()
    {
        var markup = $"<Project>\n  {MarkupOpen} note -->\n</Project>\n";

        var finding = Assert.Single(new MarkupCommentScanner().Scan("A.csproj", markup));

        Assert.Equal(2, finding.Line);
    }

    [Fact]
    public void MarkupScannerAcceptsCleanMarkup()
    {
        Assert.Empty(new MarkupCommentScanner().Scan("A.xaml", "<Page>\n  <Grid />\n</Page>\n"));
    }

    [Theory]
    [InlineData("A.xaml")]
    [InlineData("A.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("app.manifest")]
    public void MarkupScannerHandlesMarkupFiles(string path)
    {
        Assert.True(new MarkupCommentScanner().Handles(path));
    }

    [Fact]
    public void ScriptScannerReportsWholeLineComment()
    {
        var script = $"name: build\n{Hash} explains\njobs:\n";

        var finding = Assert.Single(new ScriptCommentScanner().Scan("ci.yml", script));

        Assert.Equal(2, finding.Line);
    }

    [Fact]
    public void ScriptScannerReportsTrailingComment()
    {
        Assert.Single(new ScriptCommentScanner().Scan("ci.yml", $"timeout-minutes: 20 {Hash} generous\n"));
    }

    [Fact]
    public void ScriptScannerReportsPowerShellBlockComment()
    {
        Assert.Single(new ScriptCommentScanner().Scan("a.ps1", $"<{Hash} block {Hash}>\n"));
    }

    [Fact]
    public void ScriptScannerIgnoresMarkerInsideQuotes()
    {
        Assert.Empty(new ScriptCommentScanner().Scan("ci.yml", $"colour: \"{Hash}F5F6F8\"\nother: '{Hash}1C1D21'\n"));
    }

    [Fact]
    public void ScriptScannerIgnoresMarkerInsideAWord()
    {
        Assert.Empty(new ScriptCommentScanner().Scan("ci.yml", $"run: dotnet build C{Hash}Project\n"));
    }

    [Fact]
    public void ScriptScannerSeesCommentAfterAnApostropheInsideAWord()
    {
        Assert.Single(new ScriptCommentScanner().Scan("ci.yml", $"name: don't stop {Hash} really\n"));
    }

    [Fact]
    public void JsonScannerAcceptsStrictJson()
    {
        Assert.Empty(new JsonStrictnessScanner().Scan("a.json", "{ \"a\": [1, 2] }"));
    }

    [Fact]
    public void JsonScannerReportsComment()
    {
        var finding = Assert.Single(new JsonStrictnessScanner().Scan("a.json", $"{{\n  {Slashes} note\n  \"a\": 1\n}}"));

        Assert.Equal(JsonStrictnessScanner.Rule, finding.Rule);
    }

    [Fact]
    public void JsonScannerReportsTrailingComma()
    {
        Assert.Single(new JsonStrictnessScanner().Scan("a.json", "{ \"a\": 1, }"));
    }
}
