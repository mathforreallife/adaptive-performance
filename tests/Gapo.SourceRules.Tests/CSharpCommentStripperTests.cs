using Xunit;

namespace Gapo.SourceRules.Tests;

public sealed class CSharpCommentStripperTests
{
    private static readonly string Slashes = new('/', 2);
    private static readonly string DocSlashes = new('/', 3);
    private static readonly string BlockOpen = "/" + "*";
    private static readonly string BlockClose = "*" + "/";

    [Fact]
    public void LeavesCommentFreeCodeUntouched()
    {
        const string Source = "namespace A;\n\npublic sealed class B;\n";

        Assert.Equal(Source, CSharpCommentStripper.Strip(Source));
    }

    [Fact]
    public void RemovesWholeLineCommentTogetherWithItsLine()
    {
        var source = $"namespace A;\n{Slashes} gone\npublic sealed class B;\n";

        Assert.Equal("namespace A;\npublic sealed class B;\n", CSharpCommentStripper.Strip(source));
    }

    [Fact]
    public void RemovesTrailingCommentAndKeepsTheCode()
    {
        var source = $"namespace A;\npublic sealed class B;   {Slashes} gone\n";

        Assert.Equal("namespace A;\npublic sealed class B;\n", CSharpCommentStripper.Strip(source));
    }

    [Fact]
    public void RemovesMultiLineBlockAndDocumentationComments()
    {
        var source = $"namespace A;\n{BlockOpen} one\n two {BlockClose}\n{DocSlashes} <summary>\n{DocSlashes} doc\n{DocSlashes} </summary>\npublic sealed class B;\n";

        Assert.Equal("namespace A;\npublic sealed class B;\n", CSharpCommentStripper.Strip(source));
    }

    [Fact]
    public void KeepsBlankLinesThatWereBlankBefore()
    {
        var source = $"namespace A;\n\n{Slashes} gone\npublic sealed class B;\n";

        Assert.Equal("namespace A;\n\npublic sealed class B;\n", CSharpCommentStripper.Strip(source));
    }

    [Fact]
    public void PreservesWindowsLineEndings()
    {
        var source = $"namespace A;\r\npublic sealed class B; {Slashes} gone\r\n";

        Assert.Equal("namespace A;\r\npublic sealed class B;\r\n", CSharpCommentStripper.Strip(source));
    }

    [Fact]
    public void StrippedOutputPassesTheScanner()
    {
        var source = $"namespace A;\n{DocSlashes} doc\npublic sealed class B {{ {BlockOpen} x {BlockClose} public int C; {Slashes} y\n}}\n";

        var stripped = CSharpCommentStripper.Strip(source);

        Assert.Empty(new CSharpCommentScanner().Scan("B.cs", stripped));
    }
}
