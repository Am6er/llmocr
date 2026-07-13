using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmOcr;

/// <summary>
/// Configuration loaded from config.json placed next to the executable.
/// Holds all paths and command parameters used to launch MinerU.
/// </summary>
public class AppConfig
{
    public string MineruDir { get; set; } = @"D:\Claude\mineru\.venv\Scripts";
    public string ApiExe { get; set; } = "mineru-api.exe";
    public string CliExe { get; set; } = "mineru.exe";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8000;

    /// <summary>pipeline | hybrid-engine | vlm-engine</summary>
    public string Backend { get; set; } = "hybrid-engine";

    /// <summary>Output mode remembered across runs: "rag" | "html".</summary>
    public string OutputModeName { get; set; } = "rag";

    /// <summary>RAG mode only: keep images and lay each document out in its own
    /// subfolder (like HTML), instead of dumping all .md flat into the output folder.</summary>
    public bool RagSaveImages { get; set; } = false;

    /// <summary>OCR language hint (pipeline). cyrillic covers Russian + Latin (mixed RU/EN).
    /// Empty = MinerU default (ch). e.g. cyrillic / east_slavic / ch.</summary>
    public string Lang { get; set; } = "cyrillic";

    /// <summary>Parse method (pipeline &amp; hybrid backends): "auto" | "txt" | "ocr".
    /// auto = MinerU picks (txt if a text layer exists, else ocr); txt = use the PDF text layer;
    /// ocr = ignore the text layer and read the rendered pixels (needed for broken-font PDFs).</summary>
    public string Method { get; set; } = "auto";

    /// <summary>Hybrid parsing effort: "medium" | "high". Applies ONLY to hybrid-* backends.
    /// medium = fast, image/chart analysis off; high = higher accuracy + image/chart analysis, slower.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Image/chart analysis toggle for hybrid & vlm backends: "auto" | "on" | "off".
    /// "auto" = don't pass the flag (MinerU default = on, but hybrid-medium force-disables it).</summary>
    public string ImageAnalysis { get; set; } = "auto";

    /// <summary>Target max GPU temperature (°C), 50..90. When the GPU exceeds this while
    /// a batch runs, the mineru process tree is suspended until it cools back down
    /// (closed-loop thermal throttle). 90 = no limit.</summary>
    public int TargetTempC { get; set; } = 75;

    /// <summary>Path/command for nvidia-smi (used to read GPU temperature).</summary>
    public string NvidiaSmiPath { get; set; } = "nvidia-smi";

    /// <summary>MinerU internal PDF page-render wall-clock timeout (seconds), raised well above
    /// the 300 s default so GPU-cooldown pauses don't trip it. 0 = leave MinerU's default.</summary>
    public int PdfRenderTimeoutSec { get; set; } = 1800;

    /// <summary>Currently selected / default model mirror (value of MINERU_MODEL_SOURCE).
    /// Must be one of <see cref="ModelSources"/>.</summary>
    public string ModelSource { get; set; } = "huggingface";

    /// <summary>All mirrors offered in the UI dropdown. Edit here to add/remove.
    /// MinerU understands: huggingface, modelscope, local.</summary>
    public string[] ModelSources { get; set; } = { "huggingface", "modelscope", "local" };

    /// <summary>CUDA Toolkit dir. Injected as CUDA_PATH (+ its bin on PATH) into the
    /// mineru/mineru-api process env so the lmdeploy/vLLM GPU engines start even when
    /// the parent process didn't inherit the machine-wide CUDA_PATH. Empty/missing = skip.</summary>
    public string CudaPath { get; set; } = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8";

    [JsonIgnore]
    public bool HasCuda => !string.IsNullOrWhiteSpace(CudaPath) && Directory.Exists(CudaPath);

    public string DdjvuExe { get; set; } = @"D:\Claude\llmocr\DjVuLibre\ddjvu.exe";
    public int DdjvuDpi { get; set; } = 300;

    public int HealthTimeoutSec { get; set; } = 240;

    public string MathJaxUrl { get; set; } = "https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-mml-chtml.js";

    public string ExtraServerArgs { get; set; } = "";
    public string ExtraClientArgs { get; set; } = "";

    [JsonIgnore]
    public string ApiExePath => Path.Combine(MineruDir, ApiExe);
    [JsonIgnore]
    public string CliExePath => Path.Combine(MineruDir, CliExe);
    [JsonIgnore]
    public string BaseUrl => $"http://{Host}:{Port}";
    [JsonIgnore]
    public string HealthUrl => $"{BaseUrl}/health";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // fall back to defaults on any parse error
        }

        var def = new AppConfig();
        try { def.Save(); } catch { /* ignore */ }
        return def;
    }

    public void Save()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
