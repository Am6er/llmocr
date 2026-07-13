namespace LlmOcr;

/// <summary>
/// Which of the two log panes a message belongs to.
///   Files  — per-file processing progress (start/done/errors for each document).
///   System — subprocess output, model downloads, and mineru-api server events.
/// </summary>
public enum LogChannel
{
    Files,
    System
}
