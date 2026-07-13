using System;
using System.IO;
using System.Text.RegularExpressions;
using LlmOcr;

string md = args.Length > 0 ? args[0]
    : @"D:\AI\mineru-test\out\AQ-48_web\auto\AQ-48_web.md";
string outDir = args.Length > 1 ? args[1]
    : @"D:\AI\mineru-test\mdtest";
Directory.CreateDirectory(outDir);

string src = File.ReadAllText(md);
Console.WriteLine($"source md: {src.Length} chars");
Console.WriteLine($"source <table>: {Regex.Matches(src, "<table").Count}");

// RAG
string rag = MarkdownTools.ToRagMarkdown(src);
string ragPath = Path.Combine(outDir, "rag.md");
File.WriteAllText(ragPath, rag);
Console.WriteLine($"RAG md (strip): image refs kept: {Regex.Matches(rag, @"!\[").Count}, " +
                  $"remaining <table>: {Regex.Matches(rag, "<table").Count}, " +
                  $"pipe rows: {Regex.Matches(rag, @"^\|", RegexOptions.Multiline).Count}");

// RAG with keepImages=true (save-images mode): image refs must survive
string ragImg = MarkdownTools.ToRagMarkdown(src, keepImages: true);
File.WriteAllText(Path.Combine(outDir, "rag_keepimg.md"), ragImg);
Console.WriteLine($"RAG md (keepImages): image refs kept: {Regex.Matches(ragImg, @"!\[").Count} (source had {Regex.Matches(src, @"!\[").Count})");

// HTML
var cfg = new AppConfig();
string html = MarkdownTools.ToHtml(src, "AQ-48_web", cfg);
string htmlPath = Path.Combine(outDir, "render.html");
File.WriteAllText(htmlPath, html);
Console.WriteLine($"HTML: {html.Length} chars, <table>: {Regex.Matches(html, "<table").Count}, MathJax: {html.Contains("tex-mml-chtml")}");

Console.WriteLine("OK -> " + outDir);
