using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

// ── Subcommand routing ───────────────────────────────────────────────────────
if (args.Length > 0 && args[0] == "generate")
{
    var configIdx = Array.IndexOf(args, "--config");
    var configPath = configIdx >= 0 && configIdx + 1 < args.Length
        ? args[configIdx + 1]
        : "forge.config.json";

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"Config not found: {configPath}");
        Console.Error.WriteLine("Usage: DataTableExtractor generate --config <path>");
        return 1;
    }

    Console.WriteLine($"[DataTableForge] Config: {configPath}");
    var forgeConfig = JsonConvert.DeserializeObject<ForgeConfig>(File.ReadAllText(configPath))
        ?? throw new Exception("Failed to parse forge config");
    return ForgeGenerator.Generate(forgeConfig);
}

// ── Extraction mode (original behavior) ──────────────────────────────────────
// ── CLI args or config file ───────────────────────────────────────────────────
string pakPath, usmapPath, outputDir, repakExe;

if (args.Length >= 4)
{
    pakPath   = Path.GetFullPath(args[0]);
    usmapPath = Path.GetFullPath(args[1]);
    outputDir = Path.GetFullPath(args[2]);
    repakExe  = Path.GetFullPath(args[3]);
}
else
{
    // Look for config file: next to .exe, or in current directory
    var exeDir = AppContext.BaseDirectory;
    var configPath = Path.Combine(exeDir, "extractor.config.json");
    if (!File.Exists(configPath))
        configPath = Path.Combine(Directory.GetCurrentDirectory(), "extractor.config.json");

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine("Usage: DataTableExtractor <pak-path> <usmap-path> <output-dir> <repak-exe>");
        Console.Error.WriteLine("   or: place an extractor.config.json next to the .exe");
        Console.Error.WriteLine();
        Console.Error.WriteLine("extractor.config.json format:");
        Console.Error.WriteLine("  {");
        Console.Error.WriteLine("    \"pakPath\":   \"D:/Games/WildLifeC/Content/Paks/WildLifeC-Windows.pak\",");
        Console.Error.WriteLine("    \"usmapPath\": \"./DataTables/2026.03.20_Shipping_Test_Build_1.usmap\",");
        Console.Error.WriteLine("    \"outputDir\": \"./DataTables/2026.03.20_Shipping_Test_Build_1\",");
        Console.Error.WriteLine("    \"repakExe\":  \"../repak/repak.exe\"");
        Console.Error.WriteLine("  }");
        return 1;
    }

    Console.WriteLine($"Using config: {configPath}");
    var configJson = JObject.Parse(File.ReadAllText(configPath));
    var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

    string ResolvePath(string key)
    {
        var val = configJson[key]?.ToString()
            ?? throw new Exception($"Missing \"{key}\" in extractor.config.json");
        return Path.GetFullPath(Path.Combine(configDir, val));
    }

    pakPath   = ResolvePath("pakPath");
    usmapPath = ResolvePath("usmapPath");
    outputDir = ResolvePath("outputDir");
    repakExe  = ResolvePath("repakExe");
}

if (!File.Exists(pakPath))    { Console.Error.WriteLine($"pak not found: {pakPath}"); return 1; }
if (!File.Exists(usmapPath))  { Console.Error.WriteLine($"usmap not found: {usmapPath}"); return 1; }
if (!File.Exists(repakExe))   { Console.Error.WriteLine($"repak not found: {repakExe}"); return 1; }

var utf8NoBom = new UTF8Encoding(false);
Directory.CreateDirectory(outputDir);

// ── DataTables to extract ────────────────────────────────────────────────────
var dataTables = new (string pakEntry, string friendlyName)[]
{
    ("WildLifeC/Content/DataTables/DT_ClothesOutfit",                   "DT_ClothesOutfit"),
    ("WildLifeC/Content/DataTables/DT_GFur",                            "DT_GFur"),
    ("WildLifeC/Content/DataTables/NPC/DT_GameCharacterOutfits",        "DT_GameCharacterOutfits"),
    ("WildLifeC/Content/DataTables/NPC/DT_GameCharacterCustomization",  "DT_GameCharacterCustomization"),
    ("WildLifeC/Content/DataTables/Sandbox/DT_SandboxProps",            "DT_SandboxProps"),
};

// Only these DTs need NameMap txt files (used by DataTableGenerator)
var needsNameMap = new HashSet<string> { "DT_ClothesOutfit", "DT_GameCharacterOutfits", "DT_GameCharacterCustomization", "DT_SandboxProps" };

// ── Step 1: Extract .uasset/.uexp from .pak via repak ────────────────────────
var tempDir = Path.Combine(Path.GetTempPath(), "DTExtractor_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tempDir);

try
{
    Console.WriteLine("[1/4] Extracting DataTables from pak...");

    var includes = new List<string>();
    foreach (var (entry, _) in dataTables)
    {
        includes.Add(entry + ".uasset");
        includes.Add(entry + ".uexp");
    }

    var includeArgs = string.Join(" ", includes.Select(i => $"-i \"{i}\""));
    var repakArgs = $"unpack -o \"{tempDir}\" -f {includeArgs} \"{pakPath}\"";

    var psi = new ProcessStartInfo(repakExe, repakArgs)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();

    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine($"repak failed (exit {proc.ExitCode}):\n{stderr}\n{stdout}");
        return 1;
    }

    Console.WriteLine($"  Extracted to temp: {tempDir}");

    // ── Step 2: Load .usmap mappings ─────────────────────────────────────────
    Console.WriteLine("[2/4] Loading .usmap mappings...");
    var mappings = new Usmap(usmapPath);

    // ── Step 3: Convert each .uasset to JSON + inject placeholders ───────────
    Console.WriteLine("[3/4] Converting .uasset -> JSON...");

    var characterCleanNames = new List<string>();
    var customizerCharacters = new List<string>();
    var parsedJsons = new Dictionary<string, JToken>();

    foreach (var (pakEntry, name) in dataTables)
    {
        var uassetPath = Path.Combine(tempDir, pakEntry + ".uasset");
        if (!File.Exists(uassetPath))
        {
            Console.WriteLine($"  SKIP {name} (not found in pak)");
            continue;
        }

        Console.Write($"  {name}...");

        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_4, mappings);
        var jsonRaw = asset.SerializeJson(Formatting.Indented);

        if (jsonRaw.Length > 0 && jsonRaw[0] == '\uFEFF')
            jsonRaw = jsonRaw[1..];

        // Parse (lenient for NaN floats)
        JToken jsonObj;
        using (var reader = new JsonTextReader(new StringReader(jsonRaw)) { FloatParseHandling = FloatParseHandling.Double })
            jsonObj = JToken.Load(reader);

        // Check if data was parsed properly (not RawExport)
        var exportType = jsonObj["Exports"]?[0]?["$type"]?.ToString() ?? "";
        if (exportType.Contains("RawExport"))
        {
            Console.WriteLine(" WARNING: RawExport (usmap mismatch?)");
            continue;
        }

        parsedJsons[name] = jsonObj;

        // Collect character names from DT_GameCharacterOutfits
        if (name == "DT_GameCharacterOutfits")
        {
            var data = jsonObj["Exports"]?[0]?["Table"]?["Data"] as JArray;
            if (data != null)
            {
                foreach (var row in data)
                {
                    var rowName = row["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(rowName))
                        characterCleanNames.Add(CleanCharacterName(rowName));
                }
            }
        }

        // Collect customizer character names from DT_GameCharacterCustomization
        if (name == "DT_GameCharacterCustomization")
        {
            var data = jsonObj["Exports"]?[0]?["Table"]?["Data"] as JArray;
            if (data != null)
            {
                foreach (var row in data)
                {
                    var rowName = row["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(rowName))
                        customizerCharacters.Add(rowName);
                }
            }
        }

        // Write NameMap txt (only for DTs that need it)
        if (needsNameMap.Contains(name))
            WriteNameMap(jsonObj, Path.Combine(outputDir, $"{name}_Default_NameMap.txt"), utf8NoBom);

        // Inject placeholders into JSON text and write
        var jsonText = InjectPlaceholders(jsonRaw, name, characterCleanNames, customizerCharacters);
        File.WriteAllText(Path.Combine(outputDir, $"{name}_Default.json"), jsonText, utf8NoBom);

        // Write Debug.json (full UAssetAPI serialization with $type) and
        // Generated.json (same but without root $type) — these are used by
        // DataTableGenerator as templates for UAsset reconstruction.
        File.WriteAllText(Path.Combine(outputDir, $"{name}_Debug.json"), jsonRaw, utf8NoBom);
        var generatedJson = Regex.Replace(jsonRaw, @"^\s*""\$type"":\s*""[^""]*"",?\s*\r?\n", "", RegexOptions.Multiline);
        File.WriteAllText(Path.Combine(outputDir, $"{name}_Generated.json"), generatedJson, utf8NoBom);

        Console.WriteLine(" OK");
    }

    // ── Step 4: Generate auxiliary files ──────────────────────────────────────
    Console.WriteLine("[4/4] Generating auxiliary files...");

    // Characters.txt (no trailing newline to match reference)
    File.WriteAllText(
        Path.Combine(outputDir, "Characters.txt"),
        string.Join("\n", characterCleanNames) + "\n",
        utf8NoBom);
    Console.WriteLine($"  Characters.txt ({characterCleanNames.Count} entries)");

    GenerateEntryTemplates(parsedJsons, outputDir, utf8NoBom);
    GenerateAuxFromParsed(parsedJsons, outputDir, utf8NoBom);

    // Version.dat
    File.WriteAllText(Path.Combine(outputDir, "Version.dat"), "v3", utf8NoBom);
    Console.WriteLine("  Version.dat");

    Console.WriteLine("Done! Output: " + outputDir);
    return 0;
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}


// ═══════════════════════════════════════════════════════════════════════════════
// Placeholder injection
// ═══════════════════════════════════════════════════════════════════════════════

static string InjectPlaceholders(string json, string dtName, List<string> characters, List<string> customizerCharacters)
{
    json = InjectNameMapPlaceholder(json);

    if (dtName == "DT_GameCharacterOutfits")
        json = InjectOutfitAddPlaceholders(json, characters);

    if (dtName == "DT_ClothesOutfit" || dtName == "DT_SandboxProps")
        json = InjectEntryStartPlaceholder(json);

    if (dtName == "DT_GameCharacterCustomization")
        json = InjectCustomizationPlaceholders(json, customizerCharacters);

    return json;
}

static string InjectNameMapPlaceholder(string json)
{
    var nameMapEndPattern = new Regex(@"(""[^""]*""\s*)\n(\s*\],\s*\n\s*""CustomSerializationFlags"")", RegexOptions.Singleline);
    var match = nameMapEndPattern.Match(json);
    if (match.Success)
    {
        json = json[..match.Index]
            + match.Groups[1].Value + ",\n    [NAMEMAPSTART]\n"
            + match.Groups[2].Value.TrimStart('\n', '\r')
            + json[(match.Index + match.Length)..];
    }
    return json;
}

static string InjectOutfitAddPlaceholders(string json, List<string> characters)
{
    var insertions = new List<(int position, string placeholder)>();

    foreach (var charName in characters)
    {
        var upperName = charName.ToUpperInvariant();
        var placeholder = $",[{upperName}_OUTFITADD]";

        var rowPattern = new Regex(
            @"""Name"":\s*""Outfits_" + Regex.Escape(charName) + @"[^""]*""",
            RegexOptions.None);
        var rowMatch = rowPattern.Match(json);
        if (!rowMatch.Success) continue;

        var outfitsPropIdx = json.IndexOf("\"Name\": \"Outfits\"", rowMatch.Index, StringComparison.Ordinal);
        if (outfitsPropIdx < 0) continue;

        var nextCharIdx = json.IndexOf("\"GameCharacterOutfitsData\"", outfitsPropIdx + 50, StringComparison.Ordinal);
        var searchEnd = nextCharIdx > 0 ? nextCharIdx : json.Length;

        var section = json[outfitsPropIdx..searchEnd];

        var closePattern = new Regex(@"\}\s*\r?\n\s{16}\]", RegexOptions.None);
        var matches = closePattern.Matches(section);
        if (matches.Count == 0) continue;

        var lastMatch = matches[^1];
        var absPos = outfitsPropIdx + lastMatch.Index + 1;
        insertions.Add((absPos, placeholder));
    }

    insertions.Sort((a, b) => b.position.CompareTo(a.position));

    var sb = new StringBuilder(json);
    foreach (var (pos, ph) in insertions)
        sb.Insert(pos, ph);

    return sb.ToString();
}


static string InjectEntryStartPlaceholder(string json)
{
    // Find "Data": [ in the Exports section — [ENTRYSTART] goes right before
    // the ] that closes the Table struct's last array, just before "Data":
    var pattern = new Regex(
        @"(\})\s*\r?\n(\s+)\]\s*\r?\n(\s+)\},\s*\r?\n\s+""Data"": \[",
        RegexOptions.None);

    var matches = pattern.Matches(json);
    if (matches.Count == 0) return json;

    var match = matches[^1]; // last match (closest to the actual Data section)
    var closeBraceEnd = match.Groups[1].Index + match.Groups[1].Length;
    var bracketIndent = match.Groups[2].Value; // indent of the ]
    var entryStartLine = ",\n" + bracketIndent + "  [ENTRYSTART]";

    json = json[..closeBraceEnd] + entryStartLine + json[closeBraceEnd..];
    return json;
}

static string InjectCustomizationPlaceholders(string json, List<string> customizerCharacters)
{
    // Map property names to placeholder suffixes
    var propMap = new (string propName, string suffix)[]
    {
        ("HairMeshes", "HAIR"),
        ("SkinTextures", "SKIN"),
        ("PubicHairMasks", "PUBICHAIR"),
        ("BodyHairMasks", "PUBICHAIR"),
        ("IrisTextures", "EYES"),
        ("EyeLinerTextures", "EYELINER"),
        ("EyeShadowTextures", "EYESHADOW"),
        ("LipstickTextures", "LIPSTICK"),
        ("TanlineTextures", "TANLINES"),
        ("BeardMeshes", "BEARD"),
    };

    // Build character positions for section bounding
    var charPositions = new List<(string name, int pos)>();
    foreach (var ch in customizerCharacters)
    {
        var idx = json.IndexOf($"\"Name\": \"{ch}\"", StringComparison.Ordinal);
        if (idx >= 0) charPositions.Add((ch, idx));
    }

    // Collect all insertions: (lineStart, braceEnd, placeholder)
    var insertions = new List<(int lineStart, int braceEnd, string placeholder)>();

    for (int ci = 0; ci < charPositions.Count; ci++)
    {
        var (charName, charPos) = charPositions[ci];
        var upperName = charName.ToUpperInvariant();
        var sectionEnd = ci + 1 < charPositions.Count ? charPositions[ci + 1].pos : json.Length;

        foreach (var (propName, suffix) in propMap)
        {
            var propStr = $"\"Name\": \"{propName}\"";
            var propIdx = json.IndexOf(propStr, charPos, StringComparison.Ordinal);
            if (propIdx < 0 || propIdx >= sectionEnd) continue;

            // Find "Value": [ for this property
            var valueStr = "\"Value\": [";
            var valueIdx = json.IndexOf(valueStr, propIdx, StringComparison.Ordinal);
            if (valueIdx < 0 || valueIdx >= sectionEnd) continue;

            var arrStart = valueIdx + valueStr.Length - 1; // position of [

            // Count brackets to find matching ]
            int depth = 1;
            int pos = arrStart + 1;
            while (pos < json.Length && depth > 0)
            {
                if (json[pos] == '[') depth++;
                else if (json[pos] == ']') depth--;
                pos++;
            }
            if (depth != 0) continue;

            var closeBracketIdx = pos - 1; // position of ]

            // Check if array is empty
            var arrayContent = json[(arrStart + 1)..closeBracketIdx].Trim();
            if (string.IsNullOrEmpty(arrayContent)) continue;

            // Find the last top-level } in the array (last entry's closing brace)
            int depth2 = 0;
            int lastTopLevelBrace = -1;
            for (int i = arrStart + 1; i < closeBracketIdx; i++)
            {
                if (json[i] == '{') depth2++;
                else if (json[i] == '}')
                {
                    depth2--;
                    if (depth2 == 0) lastTopLevelBrace = i;
                }
            }
            if (lastTopLevelBrace < 0) continue;

            // Find the start of that } line (to replace the indentation)
            var lineStart = json.LastIndexOf('\n', lastTopLevelBrace) + 1;
            var placeholder = $",[{upperName}_{suffix}]";
            insertions.Add((lineStart, lastTopLevelBrace + 1, placeholder));
        }
    }

    // Sort by position descending to avoid offset drift
    insertions.Sort((a, b) => b.lineStart.CompareTo(a.lineStart));

    var sb = new StringBuilder(json);
    foreach (var (lineStart, braceEnd, ph) in insertions)
    {
        // Replace "          }" with "},[PLACEHOLDER]"
        sb.Remove(lineStart, braceEnd - lineStart);
        sb.Insert(lineStart, "}" + ph);
    }

    return sb.ToString();
}


// ═══════════════════════════════════════════════════════════════════════════════
// Character name cleaning
// ═══════════════════════════════════════════════════════════════════════════════

static string CleanCharacterName(string rowName)
{
    var name = rowName;
    if (name.StartsWith("Outfits_"))
        name = name["Outfits_".Length..];

    string[] suffixPatterns = [
        "_AB_NSFW", "_NewModel", "_AB2", "_AB3", "_AB", "_A2", "_A", "_B", "_C", "_2", "_3"
    ];

    foreach (var suffix in suffixPatterns)
    {
        if (name.EndsWith(suffix, StringComparison.Ordinal))
        {
            name = name[..^suffix.Length];
            break;
        }
    }

    if (name == "RyanYoung") name = "Ryan";
    if (name == "Max" && rowName.Contains("NewModel")) name = "MaxB";

    if (name.Length > 2 && char.IsUpper(name[^1]) && char.IsLower(name[^2])
        && name != "SexBot" && name != "MaxB")
    {
        var candidate = name[..^1];
        if (rowName.StartsWith("Outfits_") && candidate.Length > 2)
            name = candidate;
    }

    return name;
}


// ═══════════════════════════════════════════════════════════════════════════════
// Entry template generation
// ═══════════════════════════════════════════════════════════════════════════════

static string[] GetBodySlots() => ["Head", "Chest", "Hands", "Legs", "Feet"];

static void GenerateEntryTemplates(Dictionary<string, JToken> parsedJsons, string outputDir, Encoding enc)
{
    // ── DT_ClothesOutfit_Entry_Default.json ──
    if (parsedJsons.TryGetValue("DT_ClothesOutfit", out var clothesJson))
    {
        var data = clothesJson["Exports"]?[0]?["Table"]?["Data"] as JArray;
        if (data != null && data.Count > 0)
        {
            // Default entry (first row, no FurMask content)
            var defaultEntry = data[0].DeepClone();
            InjectClothesPlaceholders(defaultEntry, false);
            File.WriteAllText(
                Path.Combine(outputDir, "DT_ClothesOutfit_Entry_Default.json"),
                SerializeWithPlaceholders(defaultEntry), enc);
            Console.WriteLine("  DT_ClothesOutfit_Entry_Default.json");

            // FurMask entry: find a row that has FurMasks with content
            JToken? furMaskRow = null;
            foreach (var row in data)
            {
                foreach (var slot in GetBodySlots())
                {
                    var slotProp = FindProperty(row["Value"], slot);
                    if (slotProp == null) continue;
                    var furMasks = FindProperty(slotProp["Value"], "FurMasks");
                    if (furMasks?["Value"] is JArray arr && arr.Count > 0)
                    {
                        furMaskRow = row;
                        goto foundFurMask;
                    }
                }
            }
            foundFurMask:
            if (furMaskRow != null)
            {
                var furEntry = furMaskRow.DeepClone();
                InjectClothesPlaceholders(furEntry, true);
                File.WriteAllText(
                    Path.Combine(outputDir, "DT_ClothesOutfit_Entry_FurMask.json"),
                    SerializeWithPlaceholders(furEntry), enc);
                Console.WriteLine("  DT_ClothesOutfit_Entry_FurMask.json");
            }
        }
    }

    // ── DT_GameCharacterOutfits_Entry_Default.json (DataTableRowHandle) ──
    if (parsedJsons.TryGetValue("DT_GameCharacterOutfits", out var outfitsJson))
    {
        var data = outfitsJson["Exports"]?[0]?["Table"]?["Data"] as JArray;
        if (data != null && data.Count > 0)
        {
            // Find first DataTableRowHandle inside a character's Outfits array
            foreach (var row in data)
            {
                var outfitsProp = FindProperty(row["Value"], "Outfits");
                if (outfitsProp?["Value"] is JArray outfitsArr && outfitsArr.Count > 0)
                {
                    var handle = outfitsArr[0].DeepClone();
                    handle["Name"] = "[OUTFIT_NR]";
                    // Set RowName value to [CLOTHING_ID]
                    var rowNameProp = FindProperty(handle["Value"], "RowName");
                    if (rowNameProp != null)
                        rowNameProp["Value"] = "[CLOTHING_ID]";

                    File.WriteAllText(
                        Path.Combine(outputDir, "DT_GameCharacterOutfits_Entry_Default.json"),
                        SerializeWithPlaceholders(handle), enc);
                    Console.WriteLine("  DT_GameCharacterOutfits_Entry_Default.json");
                    break;
                }
            }
        }
    }

    // ── DT_GameCharacterCustomization_Entry_Default.json ──
    if (parsedJsons.TryGetValue("DT_GameCharacterCustomization", out var customJson))
    {
        var data = customJson["Exports"]?[0]?["Table"]?["Data"] as JArray;
        if (data != null && data.Count > 0)
        {
            // Find a SoftObjectProperty array entry (SkinTextures, EyeShadowTextures, etc.)
            string[] softObjArrays = ["SkinTextures", "EyeShadowTextures", "EyeLinerTextures",
                "LipstickTextures", "IrisTextures", "TanlineTextures", "PubicHairMasks", "WingMaterials"];
            bool foundCustomEntry = false;
            foreach (var row in data)
            {
                if (foundCustomEntry) break;
                foreach (var arrName in softObjArrays)
                {
                    var arrProp = FindProperty(row["Value"], arrName);
                    if (arrProp?["Value"] is JArray custArr && custArr.Count > 0)
                    {
                        var entry = custArr[0].DeepClone();
                        entry["Name"] = "[CUSTOM_NR]";
                        var assetPath = entry["Value"]?["AssetPath"];
                        if (assetPath != null)
                        {
                            assetPath["PackageName"] = "[PACKAGE_PATH]";
                            assetPath["AssetName"] = "[PACKAGE_NAME]";
                        }
                        File.WriteAllText(
                            Path.Combine(outputDir, "DT_GameCharacterCustomization_Entry_Default.json"),
                            SerializeWithPlaceholders(entry), enc);
                        Console.WriteLine("  DT_GameCharacterCustomization_Entry_Default.json");
                        foundCustomEntry = true;
                        break;
                    }
                }
            }

            // ── HairEntry template from HairMeshes ──
            foreach (var row in data)
            {
                var hairProp = FindProperty(row["Value"], "HairMeshes");
                if (hairProp?["Value"] is JArray hairArr && hairArr.Count > 0)
                {
                    var entry = hairArr[0].DeepClone();
                    entry["Name"] = "[CUSTOM_NR]";
                    // Replace mesh paths
                    var values = entry["Value"] as JArray;
                    if (values != null)
                    {
                        foreach (var prop in values)
                        {
                            var propName = prop["Name"]?.ToString();
                            if (propName == "skinnedMesh" || propName == "physicsMesh")
                            {
                                var ap = prop["Value"]?["AssetPath"];
                                if (ap != null)
                                {
                                    if (propName == "skinnedMesh")
                                    {
                                        ap["PackageName"] = "[PACKAGE_PATH]";
                                        ap["AssetName"] = "[PACKAGE_NAME]";
                                    }
                                    else
                                    {
                                        ap["PackageName"] = "[PHYSICS_PACKAGE_PATH]";
                                        ap["AssetName"] = "[PHYSICS_PACKAGE_NAME]";
                                        prop["IsZero"] = "__PLACEHOLDER__[PHYSICS_ZERO]__";
                                    }
                                }
                            }
                        }
                    }
                    File.WriteAllText(
                        Path.Combine(outputDir, "DT_GameCharacterCustomization_HairEntry_Default.json"),
                        SerializeWithPlaceholders(entry), enc);
                    Console.WriteLine("  DT_GameCharacterCustomization_HairEntry_Default.json");
                    break;
                }
            }
        }
    }

    // ── DT_SandboxProps_Entry_Default.json ──
    if (parsedJsons.TryGetValue("DT_SandboxProps", out var propsJson))
    {
        var data = propsJson["Exports"]?[0]?["Table"]?["Data"] as JArray;
        if (data != null && data.Count > 0)
        {
            var entry = data[0].DeepClone();
            InjectSandboxPlaceholders(entry);
            File.WriteAllText(
                Path.Combine(outputDir, "DT_SandboxProps_Entry_Default.json"),
                SerializeWithPlaceholders(entry), enc);
            Console.WriteLine("  DT_SandboxProps_Entry_Default.json");
        }
    }
}

static void InjectClothesPlaceholders(JToken entry, bool includeFurMask)
{
    entry["Name"] = "[CLOTHING_ID]";

    var values = entry["Value"] as JArray;
    if (values == null) return;

    // OutfitName
    var outfitName = FindProperty(values, "OutfitName");
    if (outfitName != null)
        outfitName["CultureInvariantString"] = "[CLOTHING_NAME]";

    // Body slots
    foreach (var slotName in GetBodySlots())
    {
        var slot = FindProperty(values, slotName);
        if (slot == null) continue;

        var slotValues = slot["Value"] as JArray;
        if (slotValues == null) continue;

        var prefix = slotName.ToUpperInvariant();

        // Meshes array: [0]=mesh, [1]=sex_mesh
        var meshes = FindProperty(slotValues, "Meshes");
        if (meshes?["Value"] is JArray meshArr)
        {
            if (meshArr.Count > 0)
                SetAssetPath(meshArr[0], $"[{prefix}_MESH_PATH]", $"[{prefix}_MESH_NAME]");
            if (meshArr.Count > 1)
                SetAssetPath(meshArr[1], $"[{prefix}_SEX_MESH_PATH]", $"[{prefix}_SEX_MESH_NAME]");
        }

        // PreviewIcon
        var icon = FindProperty(slotValues, "PreviewIcon");
        if (icon != null)
            SetAssetPath(icon, $"[{prefix}_ICON_PATH]", $"[{prefix}_ICON_NAME]");

        // physicsAreas
        SetValueAndForceNotZero(slotValues, "physicsAreas", $"[{prefix}_PHYSICSAREAS]");

        // MorphTarget
        SetValueAndForceNotZero(slotValues, "MorphTarget", $"[{prefix}_MORPHTARGET]");

        // MorphTargetValue
        SetValueAndForceNotZero(slotValues, "MorphTargetValue", $"[{prefix}_MORPHTARGETVALUE]");

        // ConstraintProfile
        SetValueAndForceNotZero(slotValues, "ConstraintProfile", $"[{prefix}_CONSTRAINTPROFILE]");

        // ArousalBlend (stays quoted in the template — it's a StrProperty)
        var arousal = FindProperty(slotValues, "ArousalBlend");
        if (arousal != null)
        {
            arousal["Value"] = $"[{prefix}_AROUSALBLEND]";
            arousal["IsZero"] = false;
        }

        // MuscleFlexRegions
        SetValueAndForceNotZero(slotValues, "MuscleFlexRegions", $"[{prefix}_FLEXREGIONS]");

        // FurMasks
        if (includeFurMask)
        {
            var furMasks = FindProperty(slotValues, "FurMasks");
            if (furMasks != null)
            {
                var arr = new JArray();
                var fm0 = CreateSoftObjectRef($"[{prefix}_FURMASK_PATH]", $"[{prefix}_FURMASK_NAME]", "0");
                var fm1 = CreateSoftObjectRef($"[{prefix}_SEX_FURMASK_PATH]", $"[{prefix}_SEX_FURMASK_NAME]", "1");
                arr.Add(fm0);
                arr.Add(fm1);
                furMasks["Value"] = arr;
            }
        }
    }
}

static void InjectSandboxPlaceholders(JToken entry)
{
    entry["Name"] = "[PROP_ID]";

    var values = entry["Value"] as JArray;
    if (values == null) return;

    // DisplayName
    var displayName = FindProperty(values, "DisplayName");
    if (displayName != null)
        displayName["CultureInvariantString"] = "[PROP_NAME]";

    // Category
    var category = FindProperty(values, "Category");
    if (category != null)
        category["Value"] = "[PROP_CATEGORY]";

    // PreviewIcon
    var icon = FindProperty(values, "PreviewIcon");
    if (icon != null)
        SetAssetPath(icon, "[PROP_ICON_PATH]", "[PROP_ICON_NAME]");

    // Mesh
    var mesh = FindProperty(values, "Mesh");
    if (mesh != null)
        SetAssetPath(mesh, "[PROP_MESH_PATH]", "[PROP_MESH_NAME]");

    // Skeleton
    var skeleton = FindProperty(values, "Skeleton");
    if (skeleton != null)
        SetAssetPath(skeleton, "[PROP_SKELETON_PATH]", "[PROP_SKELETON_NAME]");

    // ActorBP
    var actor = FindProperty(values, "ActorBP");
    if (actor != null)
        SetAssetPath(actor, "[PROP_ACTOR_PATH]", "[PROP_ACTOR_NAME]");

    // BluePrint
    var blueprint = FindProperty(values, "BluePrint");
    if (blueprint != null)
        SetAssetPath(blueprint, "[BLUEPRINT_PATH]", "[BLUEPRINT_NAME]");

    // Vector placeholders for pivot/placement/collision
    SetVectorPlaceholders(values, "PivotOffset", "PIVOTOFFSET");
    SetVectorPlaceholders(values, "PlacementOffset", "PLACEMENTOFFSET");
    SetVectorPlaceholders(values, "CollisionOffset", "COLLISIONOFFSET");
    SetVectorPlaceholders(values, "CollisionExtents", "COLLISIONEXTENTS");

    // Rotator: CollisionRotation
    var collRot = FindProperty(values, "CollisionRotation");
    if (collRot?["Value"] is JObject rotObj)
    {
        rotObj["Pitch"] = $"__PLACEHOLDER__[COLLISIONROTATION_PITCH]__";
        rotObj["Yaw"] = $"__PLACEHOLDER__[COLLISIONROTATION_YAW]__";
        rotObj["Roll"] = $"__PLACEHOLDER__[COLLISIONROTATION_ROLL]__";
    }

    // PlacementCollision
    var placementColl = FindProperty(values, "PlacementCollision");
    if (placementColl != null)
        placementColl["Value"] = "__PLACEHOLDER__[PLACEMENTCOLLISION]__";

    // ADFL
    var adfl = FindProperty(values, "ADFL");
    if (adfl != null)
        adfl["Value"] = "__PLACEHOLDER__[ADFL]__";
}

static void SetVectorPlaceholders(JArray values, string propName, string prefix)
{
    var prop = FindProperty(values, propName);
    if (prop?["Value"] is JObject vec)
    {
        vec["X"] = $"__PLACEHOLDER__[{prefix}_X]__";
        vec["Y"] = $"__PLACEHOLDER__[{prefix}_Y]__";
        vec["Z"] = $"__PLACEHOLDER__[{prefix}_Z]__";
    }
}


// ═══════════════════════════════════════════════════════════════════════════════
// Auxiliary file generation
// ═══════════════════════════════════════════════════════════════════════════════

static void GenerateAuxFromParsed(Dictionary<string, JToken> parsedJsons, string outputDir, Encoding enc)
{
    // CustomizerCharacters.txt from DT_GameCharacterCustomization
    if (parsedJsons.TryGetValue("DT_GameCharacterCustomization", out var customJson))
    {
        var data = customJson["Exports"]?[0]?["Table"]?["Data"] as JArray;
        if (data != null)
        {
            var chars = data.Select(r => r["Name"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            File.WriteAllText(Path.Combine(outputDir, "CustomizerCharacters.txt"),
                string.Join("\n", chars!) + "\n", enc);
            Console.WriteLine($"  CustomizerCharacters.txt ({chars.Count} entries)");
        }
    }

    // FurCharacters.txt from DT_GFur
    if (parsedJsons.TryGetValue("DT_GFur", out var furJson))
    {
        var data = furJson["Exports"]?[0]?["Table"]?["Data"] as JArray;
        if (data != null)
        {
            var chars = data.Select(r => r["Name"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            File.WriteAllText(Path.Combine(outputDir, "FurCharacters.txt"),
                string.Join("\n", chars!) + "\n", enc);
            Console.WriteLine($"  FurCharacters.txt ({chars.Count} entries)");
        }
    }

    // ConstraintProfiles.txt + MorphTargets.txt from DT_ClothesOutfit
    if (parsedJsons.TryGetValue("DT_ClothesOutfit", out var clothesJson))
    {
        // ConstraintProfiles: values are quoted, null is unquoted
        var profiles = new List<string> { "null" };
        var profileSet = new HashSet<string> { "null" };
        FindQuotedPropertyValues(clothesJson, "ConstraintProfile", profiles, profileSet);
        File.WriteAllText(Path.Combine(outputDir, "ConstraintProfiles.txt"),
            string.Join("\n", profiles), enc);
        Console.WriteLine($"  ConstraintProfiles.txt ({profiles.Count} entries)");

        // MorphTargets: same format
        var targets = new List<string> { "null" };
        var targetSet = new HashSet<string> { "null" };
        FindQuotedPropertyValues(clothesJson, "MorphTarget", targets, targetSet);
        File.WriteAllText(Path.Combine(outputDir, "MorphTargets.txt"),
            string.Join("\n", targets), enc);
        Console.WriteLine($"  MorphTargets.txt ({targets.Count} entries)");
    }
}


// ═══════════════════════════════════════════════════════════════════════════════
// Helper methods
// ═══════════════════════════════════════════════════════════════════════════════

static void WriteNameMap(JToken json, string path, Encoding enc)
{
    var nameMap = json["NameMap"];
    if (nameMap == null) return;
    var sb = new StringBuilder();
    foreach (var entry in nameMap)
        sb.AppendLine(entry.ToString());
    File.WriteAllText(path, sb.ToString(), enc);
}

static JToken? FindProperty(JToken? container, string name)
{
    if (container is JArray arr)
    {
        foreach (var item in arr)
        {
            if (item["Name"]?.ToString() == name)
                return item;
        }
    }
    return null;
}

/// Serialize a JToken to indented JSON, then unwrap __PLACEHOLDER__ markers
/// so that values like "[HEAD_PHYSICSAREAS]" appear as raw tokens in the output.
static string SerializeWithPlaceholders(JToken token)
{
    var json = token.ToString(Formatting.Indented);
    // Replace "\"__PLACEHOLDER__[FOO]__\"" with [FOO]  (string values → raw)
    json = Regex.Replace(json, @"""__PLACEHOLDER__(\[[^\]]+\])__""", "$1");
    return json;
}

static void SetAssetPath(JToken softObj, string packageName, string assetName)
{
    var assetPath = softObj["Value"]?["AssetPath"];
    if (assetPath != null)
    {
        assetPath["PackageName"] = packageName;
        assetPath["AssetName"] = assetName;
    }
}

static void SetValueAndForceNotZero(JArray slotValues, string propName, string placeholder)
{
    var prop = FindProperty(slotValues, propName);
    if (prop == null) return;

    // Use string marker; we'll do text replacement after serialization
    prop["Value"] = $"__PLACEHOLDER__{placeholder}__";
    prop["IsZero"] = false;
}

static JToken CreateSoftObjectRef(string packageName, string assetName, string indexName)
{
    return JToken.Parse($@"{{
        ""$type"": ""UAssetAPI.PropertyTypes.Objects.SoftObjectPropertyData, UAssetAPI"",
        ""Name"": ""{indexName}"",
        ""ArrayIndex"": 0,
        ""IsZero"": false,
        ""PropertyTagFlags"": ""None"",
        ""PropertyTagExtensions"": ""NoExtension"",
        ""Value"": {{
            ""$type"": ""UAssetAPI.PropertyTypes.Objects.FSoftObjectPath, UAssetAPI"",
            ""AssetPath"": {{
                ""$type"": ""UAssetAPI.PropertyTypes.Objects.FTopLevelAssetPath, UAssetAPI"",
                ""PackageName"": ""{packageName}"",
                ""AssetName"": ""{assetName}""
            }},
            ""SubPathString"": null
        }}
    }}");
}

static void FindQuotedPropertyValues(JToken token, string propertyName, List<string> values, HashSet<string> seen)
{
    if (token is JObject obj)
    {
        if (obj["Name"]?.ToString() == propertyName)
        {
            var val = obj["Value"];
            if (val != null)
            {
                var str = val.ToString();
                if (!string.IsNullOrWhiteSpace(str) && str != "null")
                {
                    var quoted = $"\"{str}\"";
                    if (seen.Add(quoted))
                        values.Add(quoted);
                }
            }
        }
        foreach (var prop in obj.Properties())
            FindQuotedPropertyValues(prop.Value, propertyName, values, seen);
    }
    else if (token is JArray arr)
    {
        foreach (var item in arr)
            FindQuotedPropertyValues(item, propertyName, values, seen);
    }
}
