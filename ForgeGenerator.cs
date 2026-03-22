using System.Diagnostics;
using System.Runtime;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

public static class ForgeGenerator
{
    static readonly UTF8Encoding Utf8NoBom = new(false);

    // DataTable name → subfolder inside the pak
    static readonly Dictionary<string, string> DtPaths = new()
    {
        ["DT_ClothesOutfit"] = "WildLifeC/Content/DataTables",
        ["DT_GFur"] = "WildLifeC/Content/DataTables",
        ["DT_GameCharacterOutfits"] = "WildLifeC/Content/DataTables/NPC",
        ["DT_GameCharacterCustomization"] = "WildLifeC/Content/DataTables/NPC",
        ["DT_SandboxProps"] = "WildLifeC/Content/DataTables/Sandbox",
    };

    /// <summary>Emit a structured progress line for the Electron UI to parse.</summary>
    static void EmitProgress(int percent, string message)
    {
        Console.WriteLine($"PROGRESS:{percent}:{message}");
        Console.Out.Flush();
    }

    public static int Generate(ForgeConfig config)
    {
        // Batch GC mode: reduces stop-the-world pauses on large heap (60MB+ JSON strings)
        var prevLatency = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.Batch;

        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var stepSw = new System.Diagnostics.Stopwatch();

        void StartStep(string name) { stepSw.Restart(); Console.Write($"  [{name}]"); }
        void EndStep() { Console.WriteLine($" {stepSw.ElapsedMilliseconds}ms"); }

        EmitProgress(0, "Parsing mods...");
        Console.WriteLine("[1/5] Parsing mods...");

        // 1. Parse all mod .txt files
        var mods = new List<ModData>();
        foreach (var modId in config.Mods)
        {
            var modDir = Path.Combine(config.ModsDir, modId);
            if (!Directory.Exists(modDir))
            {
                Console.WriteLine($"  WARN: mod dir not found: {modDir}");
                continue;
            }
            foreach (var txt in Directory.GetFiles(modDir, "*.txt"))
            {
                try
                {
                    var mod = ModParser.Parse(txt);
                    mods.Add(mod);
                    Console.WriteLine($"  {Path.GetFileName(txt)} -> {mod.Variant} ({mod.ModName})");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ERROR parsing {txt}: {ex.Message}");
                }
            }
        }
        Console.WriteLine($"  {mods.Count} mod entries parsed.");
        EmitProgress(15, $"{mods.Count} mods parsed");

        // 2. Read character lists
        var characters = ReadLines(Path.Combine(config.BuildDir, "Characters.txt"));

        // 3. Inject — try v2 (direct UAsset manipulation) first, v1 (JSON) as fallback
        EmitProgress(20, "Injecting mods...");
        Console.WriteLine("[2/5] Injecting mods...");

        Dictionary<string, UAsset>? directAssets = null;
        Dictionary<string, string>? injected = null;

        StartStep("v2-inject");
        directAssets = AssetInjector.InjectAll(
            config.BuildDir, config.UsmapPath, mods.ToArray(), characters);
        EndStep();

        if (directAssets != null)
        {
            Console.WriteLine($"  v2: {directAssets.Count} DataTables injected (direct).");
        }
        else
        {
            // v1 fallback: JSON template injection
            Console.Write("  v1 fallback (no AutoMod_P)...");
            StartStep("v1-inject");
            injected = TemplateInjector.InjectAll(
                config.BuildDir, mods.ToArray(), characters);
            EndStep();
            Console.WriteLine($" {injected.Count} DataTables injected.");
        }

        EmitProgress(35, "DataTables injected");

        // 4. Convert to .uasset/.uexp
        EmitProgress(40, "Converting DataTables...");
        Console.WriteLine("[3/5] Writing .uasset/.uexp...");

        Usmap? mappings = null;
        if (File.Exists(config.UsmapPath))
        {
            mappings = new Usmap(config.UsmapPath);
            Console.WriteLine($"  Loaded mappings: {config.UsmapPath}");
        }
        else
        {
            Console.Error.WriteLine($"  WARNING: usmap not found: {config.UsmapPath}");
        }

        var stagingDir = Path.Combine(config.OutputDir, config.UserId);
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, true);

        var autoModBase = Path.Combine(config.BuildDir, "AutoMod_P");

        // ── Write all DTs in parallel (v2 assets are independent) ──
        var writeTasks = new List<Task>();
        var writeErrors = new System.Collections.Concurrent.ConcurrentBag<string>();

        foreach (var (dtName, subPath) in DtPaths)
        {
            var basePath = Path.Combine(autoModBase, subPath, $"{dtName}.uasset");
            var outDir = Path.Combine(stagingDir, subPath);
            Directory.CreateDirectory(outDir);

            // v2 path: direct UAsset write (parallelizable — each asset is independent)
            if (directAssets != null && directAssets.ContainsKey(dtName))
            {
                var asset = directAssets[dtName];
                var dn = dtName; // capture for closure
                writeTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        asset.Write(Path.Combine(outDir, $"{dn}.uasset"));
                    }
                    catch (Exception ex)
                    {
                        writeErrors.Add($"{dn}: {ex.GetType().Name}: {ex.Message}");
                    }
                }));
                continue;
            }

            // File copy path: unchanged DTs (GFur, SandboxProps)
            if (File.Exists(basePath) &&
                (dtName == "DT_GFur" || (injected == null || !injected.ContainsKey(dtName))))
            {
                File.Copy(basePath, Path.Combine(outDir, $"{dtName}.uasset"), true);
                var uexpPath = Path.ChangeExtension(basePath, ".uexp");
                if (File.Exists(uexpPath))
                    File.Copy(uexpPath, Path.Combine(outDir, $"{dtName}.uexp"), true);
                continue;
            }

            // v1 fallback: JSON-based DeserializeJson (sequential — too heavy to parallelize)
            if (injected != null && injected.ContainsKey(dtName))
            {
                var asset = UAsset.DeserializeJson(injected[dtName]);
                if (mappings != null) asset.Mappings = mappings;
                asset.Write(Path.Combine(outDir, $"{dtName}.uasset"));
            }
            else if (File.Exists(basePath))
            {
                string jsonSource;
                if (dtName != "DT_GFur" && injected != null && injected.ContainsKey(dtName))
                    jsonSource = injected[dtName];
                else
                {
                    var debugPath = Path.Combine(config.BuildDir, $"{dtName}_Debug.json");
                    if (!File.Exists(debugPath)) continue;
                    jsonSource = File.ReadAllText(debugPath);
                }
                var asset = UAsset.DeserializeJson(jsonSource);
                if (mappings != null) asset.Mappings = mappings;
                try { Directory.CreateDirectory(Path.GetDirectoryName(basePath)!); asset.Write(basePath); } catch { }
                asset.Write(Path.Combine(outDir, $"{dtName}.uasset"));
            }
        }

        // Wait for all parallel writes
        if (writeTasks.Count > 0)
        {
            StartStep("parallel-write");
            Task.WaitAll(writeTasks.ToArray());
            EndStep();
        }

        if (!writeErrors.IsEmpty)
        {
            foreach (var err in writeErrors)
                Console.Error.WriteLine($"  ERROR: {err}");
            return 1;
        }

        foreach (var (dtName, _) in DtPaths)
            Console.WriteLine($"  {dtName} OK");
        EmitProgress(75, "DataTables written");

        // 5. Copy mod assets (WildLifeC/ trees)
        EmitProgress(80, "Copying mod assets...");
        Console.WriteLine("[4/5] Copying mod assets...");
        foreach (var modId in config.Mods)
        {
            var wlcSrc = Path.Combine(config.ModsDir, modId, "WildLifeC");
            if (Directory.Exists(wlcSrc))
            {
                CopyDirRecursive(wlcSrc, Path.Combine(stagingDir, "WildLifeC"));
                Console.WriteLine($"  {modId}/WildLifeC -> staging");
            }
        }

        // 6. Pack with repak
        EmitProgress(92, "Packing .pak...");
        Console.WriteLine("[5/5] Packing .pak...");
        var pakPath = Path.Combine(config.OutputDir, $"{config.PakName}_P.pak");

        var psi = new ProcessStartInfo(config.RepakExe,
            $"pack \"{stagingDir}\" \"{pakPath}\"")
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

        Console.WriteLine($"  Created: {pakPath}");

        // Cleanup staging
        try { Directory.Delete(stagingDir, true); } catch { }

        GCSettings.LatencyMode = prevLatency;
        totalSw.Stop();
        EmitProgress(100, "Done");
        Console.WriteLine($"Done! Total: {totalSw.ElapsedMilliseconds}ms");
        return 0;
    }

    /// <summary>
    /// Merge injected template data (NameMap + Table.Data) into the Debug.json
    /// which has full $type annotations needed by UAsset.DeserializeJson().
    /// </summary>
    static string MergeInjectedIntoDebug(string debugJson, string injectedJson)
    {
        // Parse both with NaN support
        JToken debugObj;
        using (var r = new JsonTextReader(new StringReader(debugJson))
            { FloatParseHandling = FloatParseHandling.Double })
            debugObj = JToken.Load(r);

        JToken injectedObj;
        using (var r = new JsonTextReader(new StringReader(injectedJson))
            { FloatParseHandling = FloatParseHandling.Double })
            injectedObj = JToken.Load(r);

        // Replace NameMap
        var injectedNameMap = injectedObj["NameMap"];
        if (injectedNameMap != null)
            debugObj["NameMap"] = injectedNameMap.DeepClone();

        // Replace Exports[0].Table.Data
        var injectedData = injectedObj["Exports"]?[0]?["Table"]?["Data"];
        if (injectedData != null)
        {
            var debugData = debugObj["Exports"]?[0]?["Table"]?["Data"];
            if (debugData != null)
                debugData.Replace(injectedData.DeepClone());
        }

        // Serialize back preserving $type as plain JSON properties and NaN as native doubles.
        // Use JToken.WriteTo() instead of JsonSerializer with TypeNameHandling.Auto —
        // the latter can add/modify $type annotations in ways that break UAssetAPI.
        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb))
        using (var writer = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
        {
            debugObj.WriteTo(writer);
        }
        return sb.ToString();
    }

    static string[] ReadLines(string path)
    {
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
    }

    static void CopyDirRecursive(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
        {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            CopyDirRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
