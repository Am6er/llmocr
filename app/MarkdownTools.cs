using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Markdig;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace LlmOcr;

/// <summary>
/// Post-processing of MinerU markdown output into the two target formats:
///   - RAG markdown: HTML tables -> GFM pipe tables, image refs stripped,
///     LaTeX ($...$ / $$...$$) kept as text (embedders/LLMs read it fine).
///   - Standalone HTML: rendered via Markdig + MathJax, raw HTML tables kept,
///     images referenced relatively.
/// </summary>
public static class MarkdownTools
{
    private static readonly Regex TableRegex =
        new(@"<table\b[^>]*>.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex ImageMdRegex =
        new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);

    private static readonly Regex HtmlImgRegex =
        new(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MultiBlankRegex =
        new(@"\n{3,}", RegexOptions.Compiled);

    // \textmu is a text-mode command (textcomp); inside math mode ($...$ / $$...$$)
    // it is undefined and breaks the whole equation. MinerU emits it for the micro
    // sign µ. Replace with \mu (valid in math), e.g. \mathrm{\textmu m} -> \mathrm{\mu m}.
    private static readonly Regex TextMuRegex =
        new(@"\\textmu(?![A-Za-z])", RegexOptions.Compiled);

    // Other times OCR drops the backslash of \text and the letters "text" leak in
    // front of \mu, often space-spread by MinerU: "2 . 5 t e x t \mu \mathrm{m}".
    // Strip that stray "text" (the \mu that follows is the real micro sign).
    private static readonly Regex StrayTextBeforeMuRegex =
        new(@"(?<![A-Za-z])t\s*e\s*x\s*t\s*(?=\\mu(?![A-Za-z]))", RegexOptions.Compiled);

    // MinerU emits math with MathJax/LaTeX delimiters in some runs: display \[ … \]
    // and inline \( … \). OpenCode's KaTeX only understands $ … $ (inline) and a
    // block $$ on its own line … $$ on its own line. Normalise to that so equations
    // render. (\left[ / \bigl( etc. never match — the bracket there isn't preceded
    // by a backslash.)
    private static readonly Regex DisplayBracketRegex =
        new(@"\\\[(.+?)\\\]", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineParenRegex =
        new(@"\\\((.+?)\\\)", RegexOptions.Compiled);
    // A $$…$$ that sits entirely on one line (both delimiters + content, no newline
    // between) — MinerU normally puts them on their own lines, but enforce it so the
    // block always renders. The leading [^\n$] stops it matching an already-split
    // "$$\n…\n$$" (newline right after the opener) or an inline $x$.
    private static readonly Regex OneLineBlockRegex =
        new(@"\$\$[ \t]*([^\n$][^\n]*?)[ \t]*\$\$", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites math delimiters to what OpenCode's KaTeX accepts: inline <c>\(…\)</c> → <c>$…$</c>,
    /// display <c>\[…\]</c> and any one-line <c>$$…$$</c> → a block with <c>$$</c> alone on its own
    /// line above and below the formula. Idempotent: already-correct <c>$…$</c> / block <c>$$</c> pass through.
    /// </summary>
    public static string NormalizeMathForKatex(string md)
    {
        md = DisplayBracketRegex.Replace(md, m => "\n$$\n" + m.Groups[1].Value.Trim() + "\n$$\n");
        md = InlineParenRegex.Replace(md, m => "$" + m.Groups[1].Value.Trim() + "$");
        md = OneLineBlockRegex.Replace(md, m => "\n$$\n" + m.Groups[1].Value.Trim() + "\n$$\n");
        return md;
    }

    /// <summary>
    /// Repairs micro-sign (µ) LaTeX artifacts from OCR so equations render:
    /// <c>\textmu</c> (invalid in math mode) and a leaked "text" fragment before
    /// <c>\mu</c> both collapse to a plain <c>\mu</c>. Safe to run on the whole
    /// document — the patterns only fire next to LaTeX micro-sign markup.
    /// </summary>
    public static string FixLatexArtifacts(string md)
    {
        md = TextMuRegex.Replace(md, @"\mu");
        md = StrayTextBeforeMuRegex.Replace(md, "");
        return md;
    }

    // ---------- RAG markdown ----------

    public static string ToRagMarkdown(string md, bool keepImages = false)
    {
        // 0) Repair LaTeX artifacts (broken micro-sign µ) before anything else.
        md = FixLatexArtifacts(md);

        // 0b) Normalise math delimiters to KaTeX form (\(..\)->$..$, \[..\]/$$..$$->block).
        md = NormalizeMathForKatex(md);

        // 1) Convert every HTML <table> to a markdown pipe table.
        string result = TableRegex.Replace(md, m =>
        {
            try
            {
                var pipe = HtmlTableToPipe(m.Value);
                return string.IsNullOrWhiteSpace(pipe) ? m.Value : "\n" + pipe + "\n";
            }
            catch
            {
                return m.Value; // keep original on failure — never lose data
            }
        });

        // 2) Strip image references unless the caller wants them kept (images saved
        //    alongside the .md in a per-document subfolder).
        if (!keepImages)
        {
            result = ImageMdRegex.Replace(result, "");
            result = HtmlImgRegex.Replace(result, "");
        }

        // 3) Tidy excessive blank lines.
        result = MultiBlankRegex.Replace(result, "\n\n");
        return result.Trim() + "\n";
    }

    /// <summary>
    /// Expands an HTML table (honouring rowspan/colspan by duplicating text into
    /// spanned cells) into a rectangular GFM pipe table. First row is the header.
    /// </summary>
    public static string HtmlTableToPipe(string tableHtml)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(tableHtml);
        var table = doc.DocumentNode.SelectSingleNode("//table");
        if (table == null) return "";

        var trNodes = table.SelectNodes(".//tr");
        if (trNodes == null || trNodes.Count == 0) return "";

        // grid[row] -> dict(col -> text); we place cells accounting for spans.
        // Rowspans from earlier rows are pre-filled into later grid rows, so a
        // simple "skip already-filled columns" pass handles carried spans.
        var grid = new List<Dictionary<int, string>>();

        int r = 0;
        foreach (var tr in trNodes)
        {
            var cells = tr.SelectNodes("./td|./th");
            while (grid.Count <= r) grid.Add(new Dictionary<int, string>());
            var rowMap = grid[r];

            int col = 0;
            if (cells != null)
            {
                foreach (var cell in cells)
                {
                    // Skip columns already filled by a carried rowspan.
                    while (rowMap.ContainsKey(col)) col++;

                    int colspan = ParseSpan(cell.GetAttributeValue("colspan", "1"));
                    int rowspan = ParseSpan(cell.GetAttributeValue("rowspan", "1"));
                    string text = CleanCell(cell.InnerText);

                    for (int c = 0; c < colspan; c++)
                    {
                        int targetCol = col + c;
                        rowMap[targetCol] = text;
                        // propagate rowspan downward
                        for (int rr = 1; rr < rowspan; rr++)
                        {
                            while (grid.Count <= r + rr) grid.Add(new Dictionary<int, string>());
                            grid[r + rr][targetCol] = text;
                        }
                    }
                    col += colspan;
                }
            }
            r++;
        }

        int ncols = grid.Where(row => row.Count > 0).Select(row => row.Keys.Max() + 1).DefaultIfEmpty(0).Max();
        if (ncols == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < grid.Count; i++)
        {
            var row = grid[i];
            var cellsOut = new List<string>();
            for (int c = 0; c < ncols; c++)
                cellsOut.Add(row.TryGetValue(c, out var v) ? v : "");

            sb.Append("| ").Append(string.Join(" | ", cellsOut)).Append(" |\n");

            if (i == 0) // header separator
            {
                sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", ncols))).Append(" |\n");
            }
        }
        return sb.ToString();
    }

    private static int ParseSpan(string s) =>
        int.TryParse(s, out var v) && v > 0 ? v : 1;

    private static string CleanCell(string raw)
    {
        var t = HtmlEntity.DeEntitize(raw ?? "");
        t = t.Replace("\r", " ").Replace("\n", " ");
        t = t.Replace("|", "\\|"); // escape pipes so they don't break the table
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    // ---------- Standalone HTML ----------

    private static readonly MarkdownPipeline HtmlPipeline =
        new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseMathematics()
            .UseAutoLinks()
            .Build();

    public static string ToHtml(string md, string title, AppConfig cfg)
    {
        md = FixLatexArtifacts(md);
        string body = Markdown.ToHtml(md, HtmlPipeline);
        string safeTitle = System.Net.WebUtility.HtmlEncode(title);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{safeTitle}}</title>
<script>
window.MathJax = {
  tex: {
    inlineMath: [['\\(','\\)'], ['$','$']],
    displayMath: [['\\[','\\]'], ['$$','$$']],
    processEscapes: true
  },
  options: { ignoreHtmlClass: 'tex2jax_ignore', processHtmlClass: 'tex2jax_process' },
  svg: { fontCache: 'global' }
};
</script>
<script async src="{{cfg.MathJaxUrl}}"></script>
<style>
  body { max-width: 900px; margin: 24px auto; padding: 0 16px;
         font-family: -apple-system, Segoe UI, Roboto, Arial, sans-serif;
         line-height: 1.5; color: #1a1a1a; }
  h1,h2,h3 { line-height: 1.25; }
  img { max-width: 100%; height: auto; display: block; margin: 12px 0; }
  table { border-collapse: collapse; margin: 14px 0; width: auto; max-width: 100%;
          table-layout: auto; font-size: 0.92em; }
  th, td { border: 1px solid #888; padding: 5px 8px; text-align: left; vertical-align: top; }
  th { background: #f2f2f2; }
  tr:nth-child(even) td { background: #fafafa; }
  mjx-container { overflow-x: auto; overflow-y: hidden; }
  code, pre { background: #f5f5f5; border-radius: 4px; }
</style>
</head>
<body>
{{body}}
</body>
</html>
""";
    }
}
