using Newtonsoft.Json;

public class ForgeConfig
{
    [JsonProperty("buildDir")]  public string BuildDir { get; set; } = "";
    [JsonProperty("modsDir")]   public string ModsDir { get; set; } = "";
    [JsonProperty("outputDir")] public string OutputDir { get; set; } = "";
    [JsonProperty("usmapPath")] public string UsmapPath { get; set; } = "";
    [JsonProperty("repakExe")]  public string RepakExe { get; set; } = "";
    [JsonProperty("pakName")]   public string PakName { get; set; } = "";
    [JsonProperty("userId")]    public string UserId { get; set; } = "";
    [JsonProperty("mods")]      public string[] Mods { get; set; } = [];
}
