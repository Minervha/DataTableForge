using System.Diagnostics;
using Newtonsoft.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

/// <summary>
/// Converts a PNG image into a cooked UE5 Texture2D asset (.uasset/.uexp/.ubulk).
///
/// Hybrid approach:
/// - UAssetAPI handles .uasset/.uexp (NameMap patching, export rename)
/// - .ubulk is handled as raw binary (direct mip data replacement)
///
/// UAssetAPI does NOT load or write .ubulk for Texture2D exports — it stores
/// only the 768-byte FTexturePlatformData metadata in the export's Extras field.
/// The actual mip pixel data lives entirely in .ubulk and must be handled manually.
/// </summary>
public static class TextureConverter
{
    public static int Run(string configPath)
    {
        try
        {
            // ── Step 1: Parse config ────────────────────────────────────────
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"Config not found: {configPath}");
                return 1;
            }

            var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            var config = JsonConvert.DeserializeObject<TextureConfig>(File.ReadAllText(configPath))
                ?? throw new Exception("Failed to parse texture config");

            // Resolve relative paths against config directory
            string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(configDir, p));
            config.Input        = Resolve(config.Input);
            config.Output       = Resolve(config.Output);
            config.TemplatePath = Resolve(config.TemplatePath);
            config.TexconvPath  = Resolve(config.TexconvPath);
            config.UsmapPath    = Resolve(config.UsmapPath);

            Console.Error.WriteLine($"[convert-texture] Input: {config.Input}");
            Console.Error.WriteLine($"[convert-texture] Template: {config.TemplatePath}");
            Console.Error.WriteLine($"[convert-texture] Output: {config.Output}/{config.AssetName}");
            Console.Error.WriteLine($"[convert-texture] Format: {config.TexconvFormat} -> {config.TargetPixelFormat}");

            // ── Step 2: Validate inputs ─────────────────────────────────────
            var templateUasset = config.TemplatePath + ".uasset";
            var templateUexp   = config.TemplatePath + ".uexp";
            var templateUbulk  = config.TemplatePath + ".ubulk";

            if (!File.Exists(config.Input))
            { Console.Error.WriteLine($"PNG not found: {config.Input}"); return 1; }
            if (!File.Exists(templateUasset))
            { Console.Error.WriteLine($"Template .uasset not found: {templateUasset}"); return 1; }
            if (!File.Exists(templateUexp))
            { Console.Error.WriteLine($"Template .uexp not found: {templateUexp}"); return 1; }
            if (!File.Exists(templateUbulk))
            { Console.Error.WriteLine($"Template .ubulk not found: {templateUbulk}"); return 1; }
            if (!File.Exists(config.TexconvPath))
            { Console.Error.WriteLine($"texconv not found: {config.TexconvPath}"); return 1; }
            if (!File.Exists(config.UsmapPath))
            { Console.Error.WriteLine($"usmap not found: {config.UsmapPath}"); return 1; }

            var templateBulkSize = new FileInfo(templateUbulk).Length;
            Console.Error.WriteLine($"[convert-texture] Template .ubulk: {templateBulkSize} bytes");

            // ── Step 3: Run texconv PNG -> DDS ──────────────────────────────
            var tempDir = Path.Combine(Path.GetTempPath(), $"forge_tex_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var texconvArgs = $"-f {config.TexconvFormat} -m 0 -w {config.Width} -h {config.Height} -y -o \"{tempDir}\" \"{config.Input}\"";
                Console.Error.WriteLine($"[convert-texture] texconv {texconvArgs}");

                var psi = new ProcessStartInfo
                {
                    FileName = config.TexconvPath,
                    Arguments = texconvArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var proc = Process.Start(psi)!;
                var texconvStdout = proc.StandardOutput.ReadToEnd();
                var texconvStderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(60_000);

                if (proc.ExitCode != 0)
                {
                    Console.Error.WriteLine($"[convert-texture] texconv failed (code {proc.ExitCode}):");
                    Console.Error.WriteLine(texconvStdout);
                    Console.Error.WriteLine(texconvStderr);
                    return 1;
                }

                var ddsFiles = Directory.GetFiles(tempDir, "*.dds");
                if (ddsFiles.Length == 0)
                {
                    Console.Error.WriteLine("[convert-texture] texconv produced no .dds output");
                    return 1;
                }

                var ddsBytes = File.ReadAllBytes(ddsFiles[0]);
                Console.Error.WriteLine($"[convert-texture] DDS file: {ddsBytes.Length} bytes");

                // ── Step 4: Strip DDS header -> raw mip data ────────────────
                int headerSize = 128;
                if (ddsBytes.Length > 148)
                {
                    // DX10 extended header: fourCC at offset 84 = "DX10" (0x30315844)
                    uint fourCC = BitConverter.ToUInt32(ddsBytes, 84);
                    if (fourCC == 0x30315844)
                    {
                        headerSize = 148;
                        Console.Error.WriteLine("[convert-texture] DDS has DX10 extended header (148 bytes)");
                    }
                }

                var ddsRawData = new byte[ddsBytes.Length - headerSize];
                Array.Copy(ddsBytes, headerSize, ddsRawData, 0, ddsRawData.Length);
                Console.Error.WriteLine($"[convert-texture] Raw mip data: {ddsRawData.Length} bytes, template .ubulk: {templateBulkSize} bytes");

                // ── Step 5: UAssetAPI — patch .uasset/.uexp ─────────────────
                // UAssetAPI handles NameMap (pixel format + asset name) and export
                // table (ObjectName rename). It does NOT touch .ubulk.
                var mappings = new Usmap(config.UsmapPath);
                var asset = new UAsset(templateUasset, EngineVersion.VER_UE5_4, mappings);

                Console.Error.WriteLine($"[convert-texture] Template loaded: {asset.Exports.Count} exports, NameMap: {asset.GetNameMapIndexList().Count} entries");

                // Patch NameMap: pixel format (e.g. PF_BC5 -> PF_DXT5)
                var nameMap = asset.GetNameMapIndexList();
                for (int i = 0; i < nameMap.Count; i++)
                {
                    var entry = nameMap[i].Value;
                    if (entry.StartsWith("PF_") && entry != config.TargetPixelFormat)
                    {
                        Console.Error.WriteLine($"[convert-texture] NameMap[{i}]: '{entry}' -> '{config.TargetPixelFormat}'");
                        asset.SetNameReference(i, new FString(config.TargetPixelFormat));
                    }
                }

                // Patch NameMap: asset name (template name -> new asset name)
                var templateName = Path.GetFileNameWithoutExtension(templateUasset);
                nameMap = asset.GetNameMapIndexList(); // re-read after PF patch
                for (int i = 0; i < nameMap.Count; i++)
                {
                    if (nameMap[i].Value == templateName)
                    {
                        Console.Error.WriteLine($"[convert-texture] NameMap[{i}]: '{templateName}' -> '{config.AssetName}'");
                        asset.SetNameReference(i, new FString(config.AssetName));
                    }
                }

                // Rename export ObjectName
                foreach (var exp in asset.Exports)
                {
                    if (exp.ObjectName.Value.Value == templateName)
                    {
                        exp.ObjectName = FName.FromString(asset, config.AssetName);
                        Console.Error.WriteLine($"[convert-texture] Export ObjectName -> '{config.AssetName}'");
                    }
                }

                // Write .uasset + .uexp (UAssetAPI handles these two)
                Directory.CreateDirectory(config.Output);
                var outputUasset = Path.Combine(config.Output, config.AssetName + ".uasset");
                asset.Write(outputUasset);
                Console.Error.WriteLine($"[convert-texture] Wrote .uasset: {new FileInfo(outputUasset).Length} bytes");

                var outputUexp = Path.Combine(config.Output, config.AssetName + ".uexp");
                if (File.Exists(outputUexp))
                    Console.Error.WriteLine($"[convert-texture] Wrote .uexp: {new FileInfo(outputUexp).Length} bytes");

                // ── Step 6: Handle .ubulk — raw binary ──────────────────────
                // The .ubulk contains raw mip pixel data. The .uexp metadata
                // (FTexturePlatformData in Extras) references it by offset/size.
                // Since we use the same format + dimensions as the template,
                // the mip structure is identical — we just replace the pixel bytes.
                var outputUbulk = Path.Combine(config.Output, config.AssetName + ".ubulk");

                if (ddsRawData.Length == templateBulkSize)
                {
                    // Perfect match — write DDS raw data directly as .ubulk
                    Console.Error.WriteLine($"[convert-texture] .ubulk: exact size match, writing DDS raw data directly");
                    File.WriteAllBytes(outputUbulk, ddsRawData);
                }
                else if (ddsRawData.Length < templateBulkSize)
                {
                    // DDS raw data is smaller than template .ubulk.
                    // This happens because UE5 may pad/align mip data in .ubulk.
                    // Strategy: copy template .ubulk structure, overwrite mip data
                    // starting from offset 0 (mips are stored largest-first).
                    Console.Error.WriteLine($"[convert-texture] .ubulk: DDS ({ddsRawData.Length}) < template ({templateBulkSize}), padding with template structure");
                    var bulkBytes = File.ReadAllBytes(templateUbulk);
                    Array.Copy(ddsRawData, 0, bulkBytes, 0, ddsRawData.Length);
                    File.WriteAllBytes(outputUbulk, bulkBytes);
                }
                else
                {
                    // DDS raw data is larger — shouldn't happen with same dimensions.
                    // Truncate to template size as safety measure.
                    Console.Error.WriteLine($"[convert-texture] WARNING: .ubulk: DDS ({ddsRawData.Length}) > template ({templateBulkSize}), truncating");
                    var bulkBytes = new byte[templateBulkSize];
                    Array.Copy(ddsRawData, 0, bulkBytes, 0, (int)templateBulkSize);
                    File.WriteAllBytes(outputUbulk, bulkBytes);
                }

                Console.Error.WriteLine($"[convert-texture] Wrote .ubulk: {new FileInfo(outputUbulk).Length} bytes");

                // ── Step 7: Verify and output result ────────────────────────
                var outputFiles = new List<string>();
                foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
                {
                    var p = Path.Combine(config.Output, config.AssetName + ext);
                    if (File.Exists(p))
                    {
                        outputFiles.Add(p);
                        Console.Error.WriteLine($"[convert-texture] Final: {Path.GetFileName(p)} ({new FileInfo(p).Length:N0} bytes)");
                    }
                }

                var result = new
                {
                    success = true,
                    outputFiles = outputFiles.ToArray(),
                    assetName = config.AssetName,
                };
                Console.WriteLine(JsonConvert.SerializeObject(result));
                return 0;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[convert-texture] Fatal error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
