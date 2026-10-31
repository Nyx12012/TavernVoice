using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool strip = args.Contains("--strip");
        string root = args.First(a => !a.StartsWith("--"));
        int problems = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".cs" && ext != ".md" && ext != ".txt" && ext != ".csproj" && ext != ".ps1" && ext != ".go" && ext != ".py")
                continue;
            string text = File.ReadAllText(file);
            if (strip && ext == ".cs")
            {
                SyntaxNode tree = CSharpSyntaxTree.ParseText(text).GetRoot();
                SyntaxNode cleaned = new CommentStripper().Visit(tree);
                string result = string.Join("\n", cleaned.ToFullString().Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));
                while (result.Contains("\n\n\n")) result = result.Replace("\n\n\n", "\n\n");
                File.WriteAllText(file, result);
                text = result;
            }
            if (text.IndexOf('\u2014') >= 0 || text.IndexOf('\u2013') >= 0)
            {
                Console.WriteLine(file + ": has an em or en dash");
                problems++;
            }
            if (ext == ".cs")
            {
                int comments = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantTrivia()
                    .Count(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                        || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
                if (comments > 0)
                {
                    Console.WriteLine(file + ": " + comments + " comment(s)");
                    problems++;
                }
            }
        }
        Console.WriteLine(problems == 0 ? "style ok" : problems + " file(s) break the public style");
        return problems == 0 ? 0 : 1;
    }
}

internal sealed class CommentStripper : CSharpSyntaxRewriter
{
    internal CommentStripper() : base(true) { }

    public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia)
    {
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            return default;
        return base.VisitTrivia(trivia);
    }
}
