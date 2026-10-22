namespace Knowledge.Embeddings;

/// <summary>SHA-256 pins for the approved upstream BGE revision.</summary>
public static class BgeAssetManifest
{
    public const int VocabularySize = 30522;
    public const string Revision = "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a";
    public const string ModelPath = "onnx/model.onnx";
    public const string ModelSha256 = "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35";
    public const string VocabularyPath = "vocab.txt";
    public const string VocabularySha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";
    public const string TokenizerJsonSha256 = "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66";
    public const string TokenizerConfigSha256 = "9261e7d79b44c8195c1cada2b453e55b00aeb81e907a6664974b4d7776172ab3";
    public const string SpecialTokensMapSha256 = "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3";
    public const string ModelConfigSha256 = "094f8e891b932f2000c92cfc663bac4c62069f5d8af5b5278c4306aef3084750";
    public const string PoolingConfigSha256 = "d1caf60c96f5fba2157c0c26b76d80818fad6cf0b8eb5e73ec372ff9818eba5c";
}
