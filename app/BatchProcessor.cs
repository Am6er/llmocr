using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlmOcr;

public enum OutputMode
{
    Rag,
    Html
}

public sealed class BatchResult
{
    public int Ok;
    public int Failed;
    public int Skipped;
    public List<string> FailedFiles { get; } = new();
}

/// <summary>
/// Converts a batch of PDF/DJVU files into the chosen output format using a
/// running mineru-api server (connected via --api-url). DJVU is converted to
/// PDF with ddjvu first. Each MinerU markdown result is post-processed by
/// <see cref="MarkdownTools"/>.
/// </summary>
public sealed class BatchProcessor
{
    private static readonly string[] Supported =
        { ".pdf", ".djvu", ".djv", ".docx", ".pptx", ".xlsx" };

    private readonly AppConfig _cfg;
    private readonly Action<string> _log;     // per-file progress  -> left pane
    private readonly Action<string> _logSys;  // subprocess/system  -> right pane

    public BatchProcessor(AppConfig cfg, Action<string> logFiles, Action<string> logSys)
    {
        _cfg = cfg;
        _log = logFiles;
        _logSys = logSys;
    }

    /// <summary>The final output file that a given input would produce — used to skip
    /// files that are already done. Must match where <see cref="RunAsync"/> writes results.</summary>
    private static string ExpectedOutputPath(string outDir, string name, OutputMode mode, bool ragSaveImages)
    {
        if (mode == OutputMode.Html)
            return Path.Combine(outDir, name, name + ".html");
        if (ragSaveImages)
            return Path.Combine(outDir, name, name + ".md");
        return Path.Combine(outDir, name + ".md"); // flat RAG
    }

    public static List<string> CollectInputs(string inputDir, bool recurse)
    {
        var opt = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(inputDir, "*.*", opt)
            .Where(f => Supported.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();
    }

    public async Task<BatchResult> RunAsync(
        IReadOnlyList<string> files,
        string outDir,
        OutputMode mode,
        IProgress<double> progress,
        CancellationToken ct,
        bool ragSaveImages = false)
    {
        var result = new BatchResult();
        Directory.CreateDirectory(outDir);

        // Scratch space lives in %TEMP%, not in the user's output folder.
        string tmpRoot = Path.Combine(Path.GetTempPath(), "llmocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        string pdfStage = Path.Combine(tmpRoot, "pdf");
        Directory.CreateDirectory(pdfStage);

        int i = 0;
        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            string name = Path.GetFileNameWithoutExtension(src);

            // Resume: skip files whose output already exists (non-empty).
            string expected = ExpectedOutputPath(outDir, name, mode, ragSaveImages);
            if (File.Exists(expected) && new FileInfo(expected).Length > 0)
            {
                _log($"[{i}/{files.Count}] {Path.GetFileName(src)} — уже готово, пропускаю");
                result.Skipped++;
                progress.Report((double)i / files.Count);
                continue;
            }

            _log($"[{i}/{files.Count}] {Path.GetFileName(src)}");

            try
            {
                string pdf = src;
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext is ".djvu" or ".djv")
                {
                    pdf = Path.Combine(pdfStage, name + ".pdf");
                    _log($"   DJVU → PDF (ddjvu)…");
                    bool okConv = await DjvuToPdfAsync(src, pdf, ct);
                    if (!okConv || !File.Exists(pdf))
                    {
                        _log("   ✗ конвертация DJVU не удалась");
                        result.Failed++; result.FailedFiles.Add(src);
                        progress.Report((double)i / files.Count);
                        continue;
                    }
                }

                string mineruOut = Path.Combine(tmpRoot, "mineru", name);
                Directory.CreateDirectory(mineruOut);

                int code = await RunMineruAsync(pdf, mineruOut, ct);
                if (code != 0)
                {
                    _log($"   ✗ mineru завершился с кодом {code}");
                    result.Failed++; result.FailedFiles.Add(src);
                    progress.Report((double)i / files.Count);
                    continue;
                }

                string? md = FindMarkdown(mineruOut, name);
                if (md == null)
                {
                    _log("   ✗ markdown на выходе не найден");
                    result.Failed++; result.FailedFiles.Add(src);
                    progress.Report((double)i / files.Count);
                    continue;
                }

                string content = await File.ReadAllTextAsync(md, ct);
                if (mode == OutputMode.Rag)
                {
                    if (ragSaveImages)
                    {
                        // Per-document subfolder (like HTML): keep image refs and copy
                        // the images folder next to the .md so references resolve.
                        string docDir = Path.Combine(outDir, name);
                        Directory.CreateDirectory(docDir);
                        string rag = MarkdownTools.ToRagMarkdown(content, keepImages: true);
                        string dest = Path.Combine(docDir, name + ".md");
                        await File.WriteAllTextAsync(dest, rag, ct);

                        string imagesSrc = Path.Combine(Path.GetDirectoryName(md)!, "images");
                        if (Directory.Exists(imagesSrc))
                            CopyDir(imagesSrc, Path.Combine(docDir, "images"));

                        _log($"   ✓ {dest}");
                    }
                    else
                    {
                        // Flat: all .md dumped into the output folder, images stripped.
                        string rag = MarkdownTools.ToRagMarkdown(content);
                        string dest = Path.Combine(outDir, name + ".md");
                        await File.WriteAllTextAsync(dest, rag, ct);
                        _log($"   ✓ {dest}");
                    }
                }
                else
                {
                    string docDir = Path.Combine(outDir, name);
                    Directory.CreateDirectory(docDir);
                    string html = MarkdownTools.ToHtml(content, name, _cfg);
                    string dest = Path.Combine(docDir, name + ".html");
                    await File.WriteAllTextAsync(dest, html, ct);

                    string imagesSrc = Path.Combine(Path.GetDirectoryName(md)!, "images");
                    if (Directory.Exists(imagesSrc))
                        CopyDir(imagesSrc, Path.Combine(docDir, "images"));

                    _log($"   ✓ {dest}");
                }

                result.Ok++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"   ✗ ошибка: {ex.Message}");
                result.Failed++; result.FailedFiles.Add(src);
            }

            progress.Report((double)i / files.Count);
        }

        // Best-effort cleanup of the temp tree.
        try { Directory.Delete(tmpRoot, true); } catch { /* ignore */ }

        return result;
    }

    private async Task<bool> DjvuToPdfAsync(string djvu, string pdf, CancellationToken ct)
    {
        if (!File.Exists(_cfg.DdjvuExe))
        {
            _log($"   ddjvu не найден: {_cfg.DdjvuExe}");
            return false;
        }
        // ddjvu renders at the document's native resolution; -quality applies only to
        // lossy tiff/jpeg output, not pdf, so we omit it.
        string args = $"-format=pdf \"{djvu}\" \"{pdf}\"";
        int code = await RunAsync(_cfg.DdjvuExe, args, Path.GetDirectoryName(_cfg.DdjvuExe), null, ct, prefix: "ddjvu");
        return code == 0;
    }

    private async Task<int> RunMineruAsync(string pdf, string outDir, CancellationToken ct)
    {
        var args = $"-p \"{pdf}\" -o \"{outDir}\" -b {_cfg.Backend} --api-url {_cfg.BaseUrl}";
        if (!string.IsNullOrWhiteSpace(_cfg.Lang))
            args += $" -l {_cfg.Lang}";

        bool isHybrid = _cfg.Backend.StartsWith("hybrid", StringComparison.OrdinalIgnoreCase);
        bool isVlm = _cfg.Backend.StartsWith("vlm", StringComparison.OrdinalIgnoreCase);

        // --effort is honoured only by hybrid-* backends.
        if (isHybrid && !string.IsNullOrWhiteSpace(_cfg.Effort))
            args += $" --effort {_cfg.Effort}";

        // --image-analysis applies to hybrid & vlm; "auto" leaves MinerU's default.
        if (isHybrid || isVlm)
        {
            if (string.Equals(_cfg.ImageAnalysis, "on", StringComparison.OrdinalIgnoreCase))
                args += " --image-analysis true";
            else if (string.Equals(_cfg.ImageAnalysis, "off", StringComparison.OrdinalIgnoreCase))
                args += " --image-analysis false";
        }

        if (!string.IsNullOrWhiteSpace(_cfg.ExtraClientArgs))
            args += " " + _cfg.ExtraClientArgs;

        var env = new Dictionary<string, string> { ["MINERU_MODEL_SOURCE"] = _cfg.ModelSource };
        if (_cfg.HasCuda)
        {
            env["CUDA_PATH"] = _cfg.CudaPath;
            string bin = Path.Combine(_cfg.CudaPath, "bin");
            string nvvp = Path.Combine(_cfg.CudaPath, "libnvvp");
            env["PATH"] = $"{bin};{nvvp};{Environment.GetEnvironmentVariable("PATH")}";
        }
        return await RunAsync(_cfg.CliExePath, args, _cfg.MineruDir, env, ct, prefix: "mineru");
    }

    private static string? FindMarkdown(string dir, string name)
    {
        if (!Directory.Exists(dir)) return null;
        var all = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories);
        return all.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                       .Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault();
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private async Task<int> RunAsync(
        string exe, string args, string? workDir,
        IDictionary<string, string>? env,
        CancellationToken ct, string prefix)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory
        };
        if (env != null)
            foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _logSys($"[{prefix}] {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _logSys($"[{prefix}] {e.Data}"); };

        if (!proc.Start())
            throw new InvalidOperationException($"Не удалось запустить {exe}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
        {
            await proc.WaitForExitAsync(ct);
        }
        return proc.ExitCode;
    }
}
