namespace Recap.Settings;

public sealed class AppSettings
{
    public string UserName { get; set; } = string.Empty;
    public string RecordingsFolder { get; set; } = string.Empty;
    public string LogsFolder { get; set; } = string.Empty;
    public string SpeechProviderId { get; set; } = "azure-speech";
    public bool SpeechRecognitionPaused { get; set; } = true;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public string LlmProviderId { get; set; } = "openai-compatible";
    public AudioDeviceSettings AudioDevices { get; set; } = new();
    public RecordingProcessingSettings RecordingProcessing { get; set; } = new();
    public Recap.LocalWhisper.LocalWhisperSettings LocalWhisper { get; set; } = new();
    public AzureSpeechSettings AzureSpeech { get; set; } = new();
    public GrokSpeechSettings GrokSpeech { get; set; } = new();
    public OpenAiCompatibleSettings OpenAiCompatible { get; set; } = new();
}

public sealed class AudioDeviceSettings
{
    public string? RemoteDeviceId { get; set; }
    public string? MicrophoneDeviceId { get; set; }
}

public sealed class RecordingProcessingSettings
{
    public bool PreserveRawFiles { get; set; }
}

public sealed class AzureSpeechSettings
{
    public string ResourceName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string SubscriptionKey { get; set; } = string.Empty;
    public string RecognitionLanguage { get; set; } = "en-US";
}

public sealed class GrokSpeechSettings
{
    public const string DefaultModel = "grok-voice-transcribe-2.0";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = DefaultModel;

    public void Validate()
    {
        if (Model != DefaultModel) throw new ArgumentException("Select a supported Grok speech model.");
    }
}

public sealed class OpenAiCompatibleSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelOrDeploymentName { get; set; } = string.Empty;
}
