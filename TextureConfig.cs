using Newtonsoft.Json;

public class TextureConfig
{
    [JsonProperty("input")]             public string Input { get; set; } = "";
    [JsonProperty("output")]            public string Output { get; set; } = "";
    [JsonProperty("assetName")]         public string AssetName { get; set; } = "";
    [JsonProperty("templatePath")]      public string TemplatePath { get; set; } = "";
    [JsonProperty("texconvPath")]       public string TexconvPath { get; set; } = "";
    [JsonProperty("usmapPath")]         public string UsmapPath { get; set; } = "";
    [JsonProperty("targetPixelFormat")] public string TargetPixelFormat { get; set; } = "";
    [JsonProperty("texconvFormat")]     public string TexconvFormat { get; set; } = "";
    [JsonProperty("width")]             public int Width { get; set; }
    [JsonProperty("height")]            public int Height { get; set; }
}
