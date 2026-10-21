using LunaTranscribe.Settings;
using SherpaOnnx;

namespace LunaTranscribe.Engine;

/// <summary>
/// NVIDIA Parakeet TDT 0.6B v3 als INT8-ONNX (sherpa-onnx-Export). Klein und schnell, 25 Sprachen.
/// Die Sprache wird vom Modell automatisch erkannt – die Sprachauswahl dient hier nur als Filter,
/// welche Sprachen überhaupt sinnvoll sind.
/// </summary>
public sealed class ParakeetEngine : SherpaEngineBase
{
    public override EngineKind Kind => EngineKind.Parakeet;

    public static readonly ModelSpec Model = new(
        FolderName: "parakeet-tdt-0.6b-v3-int8",
        HuggingFaceRepo: "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
        Files: new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" });

    protected override ModelSpec Spec => Model;

    // TDT-Encoder mit voller Attention: Speicher wächst quadratisch mit der Länge → moderat fenstern.
    protected override double MaxWindowSeconds => 90;

    protected override OfflineRecognizerConfig BuildConfig()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 128;                 // Parakeet v3: 128 Mel-Bänder
        config.ModelConfig.Transducer.Encoder = Model.PathOf("encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Model.PathOf("decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Model.PathOf("joiner.int8.onnx");
        config.ModelConfig.Tokens = Model.PathOf("tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = ModelPaths.NumThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return config;
    }

    protected override bool Supports(AppLanguage language) => language.IsSupported(EngineKind.Parakeet);
}
