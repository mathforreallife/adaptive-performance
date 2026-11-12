using Xunit;

namespace Gapo.SourceRules.Tests;

public sealed class SourceRuleSetTests : IDisposable
{
    private static readonly string Slashes = new('/', 2);

    private readonly string root = Directory.CreateTempSubdirectory("gapo-source-rules-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void PassesTreeWithoutComments()
    {
        Write("src/A.cs", "namespace A;\n");
        Write("src/A.csproj", "<Project />\n");

        var result = SourceRuleSet.Default.Check(new SourceTree(root, []));

        Assert.True(result.IsClean);
        Assert.Equal(2, result.CheckedFileCount);
    }

    [Fact]
    public void ReportsRelativePathWithForwardSlashes()
    {
        Write("src/Deep/A.cs", $"{Slashes} note\nnamespace A;\n");

        var result = SourceRuleSet.Default.Check(new SourceTree(root, []));

        Assert.Equal("src/Deep/A.cs", Assert.Single(result.Findings).Path);
    }

    [Fact]
    public void SkipsExcludedDirectories()
    {
        Write("demo/Old.cs", $"{Slashes} prototype\nnamespace A;\n");
        Write("src/A.cs", "namespace A;\n");

        var result = SourceRuleSet.Default.Check(new SourceTree(root, ["demo"]));

        Assert.True(result.IsClean);
        Assert.Equal(1, result.CheckedFileCount);
    }

    [Fact]
    public void SkipsBuildOutputAndGeneratedFiles()
    {
        Write("src/obj/Generated.cs", $"{Slashes} generated\n");
        Write("src/bin/Copy.cs", $"{Slashes} copied\n");
        Write("src/View.g.cs", $"{Slashes} generated\n");

        var result = SourceRuleSet.Default.Check(new SourceTree(root, []));

        Assert.True(result.IsClean);
        Assert.Equal(0, result.CheckedFileCount);
    }

    [Fact]
    public void IgnoresFilesNoScannerHandles()
    {
        Write("docs/readme.md", "<!" + "-- allowed in documentation -->\n");

        var result = SourceRuleSet.Default.Check(new SourceTree(root, []));

        Assert.True(result.IsClean);
        Assert.Equal(0, result.CheckedFileCount);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
