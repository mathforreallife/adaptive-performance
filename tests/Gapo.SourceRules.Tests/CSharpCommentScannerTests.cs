using Xunit;

namespace Gapo.SourceRules.Tests;

public sealed class CSharpCommentScannerTests
{
    private static readonly string Slashes = new('/', 2);
    private static readonly string DocSlashes = new('/', 3);
    private static readonly string BlockOpen = "/" + "*";
    private static readonly string BlockClose = "*" + "/";

    private readonly CSharpCommentScanner scanner = new();

    [Fact]
    public void AcceptsCodeWithoutComments()
    {
        var findings = scanner.Scan("Clean.cs", "namespace A;\npublic sealed class B { public int C => 1; }\n");

        Assert.Empty(findings);
    }

    [Fact]
    public void ReportsSingleLineCommentWithItsLineNumber()
    {
        var source = $"namespace A;\npublic sealed class B\n{{\n    {Slashes} explains\n}}\n";

        var finding = Assert.Single(scanner.Scan("B.cs", source));

        Assert.Equal(4, finding.Line);
        Assert.Equal(CSharpCommentScanner.Rule, finding.Rule);
    }

    [Fact]
    public void ReportsBlockComment()
    {
        var source = $"namespace A;\n{BlockOpen} block {BlockClose}\npublic sealed class B;\n";

        Assert.Single(scanner.Scan("B.cs", source));
    }

    [Fact]
    public void ReportsDocumentationComment()
    {
        var source = $"namespace A;\n{DocSlashes} <summary>Doc</summary>\npublic sealed class B;\n";

        Assert.Single(scanner.Scan("B.cs", source));
    }

    [Fact]
    public void ReportsTrailingCommentAfterCode()
    {
        var source = $"namespace A;\npublic sealed class B {{ public int C = 1; {Slashes} why\n}}\n";

        Assert.Single(scanner.Scan("B.cs", source));
    }

    [Fact]
    public void IgnoresCommentMarkersInsideStringLiterals()
    {
        var source = "namespace A;\npublic sealed class B { public string C = \"http://example.test/*not*/\"; }\n";

        Assert.Empty(scanner.Scan("B.cs", source));
    }

    [Fact]
    public void ReportsCommentHiddenInsideDirective()
    {
        var source = $"#pragma warning disable CS0168 {Slashes} reason\nnamespace A;\n";

        Assert.Single(scanner.Scan("B.cs", source));
    }

    [Fact]
    public void HandlesOnlyCSharpFiles()
    {
        Assert.True(scanner.Handles("src/A.cs"));
        Assert.False(scanner.Handles("src/A.xaml"));
    }
}
