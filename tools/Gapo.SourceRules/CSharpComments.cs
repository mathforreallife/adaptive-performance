using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Gapo.SourceRules;

public static class CSharpComments
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.Parse);

    public static SyntaxTree Parse(string text) => CSharpSyntaxTree.ParseText(text, ParseOptions);

    public static IEnumerable<SyntaxTrivia> FindIn(SyntaxTree tree) =>
        tree.GetRoot().DescendantTrivia(descendIntoTrivia: true).Where(IsComment);

    public static IReadOnlyList<TextSpan> SpansIn(SyntaxTree tree) =>
        FindIn(tree).Select(trivia => trivia.FullSpan).ToList();

    private static bool IsComment(SyntaxTrivia trivia) => trivia.Kind() switch
    {
        SyntaxKind.SingleLineCommentTrivia => true,
        SyntaxKind.MultiLineCommentTrivia => true,
        SyntaxKind.SingleLineDocumentationCommentTrivia => true,
        SyntaxKind.MultiLineDocumentationCommentTrivia => true,
        _ => false,
    };
}
