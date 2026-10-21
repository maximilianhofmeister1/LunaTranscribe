using LunaTranscribe.Settings;
using SherpaOnnx;

namespace LunaTranscribe.Engine;

/// <summary>
/// Cohere Transcribe (2B, März 2026) als INT8-ONNX (sherpa-onnx-Export). Beste Qualität, 14 Sprachen,
/// aber ~3 GB Modell. Die Sprache muss explizit angegeben werden.
/// </summary>
public sealed class CohereEngine : SherpaEngineBase
{
    public override EngineKind Kind => EngineKind.Cohere;

    public static readonly ModelSpec Model = new(
        FolderName: "cohere-transcribe-14-lang-int8",
        HuggingFaceRepo: "csukuangfj2/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01",
        // encoder.int8.onnx.data enthält die Gewichte (external data) und muss neben encoder.int8.onnx liegen.
        Files: new[] { "encoder.int8.onnx", "encoder.int8.onnx.data", "decoder.int8.onnx", "tokens.txt" });

    protected override ModelSpec Spec => Model;

    // Cohere-Encoder hat ein festes 35-s-Fenster → wie in der macOS-Version in 30-s-Stücke schneiden.
    protected override double MaxWindowSeconds => 30;

    protected override OfflineRecognizerConfig BuildConfig()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.ModelConfig.CohereTranscribe.Encoder = Model.PathOf("encoder.int8.onnx");
        config.ModelConfig.CohereTranscribe.Decoder = Model.PathOf("decoder.int8.onnx");
        config.ModelConfig.CohereTranscribe.UsePunct = 1;   // Satzzeichen
        config.ModelConfig.CohereTranscribe.UseItn = 1;     // Zahlen als Ziffern („dreißig“ → „30“)
        config.ModelConfig.Tokens = Model.PathOf("tokens.txt");
        config.ModelConfig.NumThreads = ModelPaths.NumThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        return config;
    }

    protected override void ApplyLanguage(OfflineStream stream, AppLanguage language) =>
        stream.SetOption("language", language.Code());

    protected override bool Supports(AppLanguage language) => language.IsSupported(EngineKind.Cohere);
}
