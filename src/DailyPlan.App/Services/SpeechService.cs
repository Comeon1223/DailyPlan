using System.Speech.Synthesis;

namespace DailyPlan.App.Services;

/// <summary>
/// Small wrapper around Windows SAPI text-to-speech. Prefers a Chinese voice,
/// falls back to any installed voice, and degrades to no-op if speech is
/// unavailable.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly object _gate = new();
    private readonly SpeechSynthesizer? _synthesizer;
    private bool _disposed;

    public SpeechService()
    {
        try
        {
            var synthesizer = new SpeechSynthesizer();
            var voices = synthesizer
                .GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .Select(voice => voice.VoiceInfo)
                .ToList();

            VoiceInfo? selected = null;
            foreach (var voice in voices.OrderByDescending(
                         voice => voice.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    synthesizer.SelectVoice(voice.Name);
                    selected = voice;
                    break;
                }
                catch
                {
                    // Try the next installed voice.
                }
            }

            if (selected is null)
            {
                synthesizer.Dispose();
                Description = "未检测到可用的语音";
                return;
            }

            synthesizer.Rate = 0;
            synthesizer.Volume = 100;
            _synthesizer = synthesizer;
            IsAvailable = true;
            Description = $"{selected.Name}（{selected.Culture.DisplayName}）";
        }
        catch (Exception exception)
        {
            Description = $"语音引擎不可用：{exception.Message}";
        }
    }

    public bool IsAvailable { get; }

    public string Description { get; } = "语音引擎不可用";

    public bool TrySpeak(string text)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed || _synthesizer is null)
            {
                return false;
            }

            try
            {
                _synthesizer.SpeakAsync(text);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_synthesizer is not null)
            {
                try
                {
                    _synthesizer.SpeakAsyncCancelAll();
                }
                catch
                {
                    // Ignore shutdown races.
                }
                _synthesizer.Dispose();
            }
        }
    }
}
