using System.Text;
using System.Text.RegularExpressions;

public static class TemplateInjector
{
    /// <summary>
    /// Injects all mods into the build's template JSONs.
    /// Returns dtName → injected JSON text (with $type stripped, pure data).
    /// </summary>
    public static Dictionary<string, string> InjectAll(
        string buildDir, ModData[] mods, string[] characters)
    {
        var result = new Dictionary<string, string>();

        // ── DT_ClothesOutfit ──
        var clothesTemplate = ReadTemplate(buildDir, "DT_ClothesOutfit_Default.json");
        if (clothesTemplate != null)
        {
            var entryTemplate = ReadTemplate(buildDir, "DT_ClothesOutfit_Entry_Default.json");
            var furEntryTemplate = ReadTemplate(buildDir, "DT_ClothesOutfit_Entry_FurMask.json");
            var addMods = mods.Where(m => m.Variant == ModVariant.Add).ToArray();

            // Determine which mods need FurMask template
            bool NeedsFurMask(ModData m) =>
                m.Slots.Values.Any(s =>
                    s.FurMaskPath != "None" && !string.IsNullOrEmpty(s.FurMaskPath));

            // Generate entries
            var entries = new List<string>();
            var allNameMapEntries = new List<string>();

            foreach (var mod in addMods)
            {
                var template = (NeedsFurMask(mod) && furEntryTemplate != null)
                    ? furEntryTemplate : entryTemplate;
                if (template == null) continue;

                entries.Add(InstantiateClothesEntry(template, mod));
                allNameMapEntries.AddRange(mod.NameMapEntries);
            }

            var json = clothesTemplate;
            json = ReplaceEntryStart(json, entries);
            json = ReplaceNameMapStart(json, allNameMapEntries.Distinct().ToList());
            result["DT_ClothesOutfit"] = json;
        }

        // ── DT_GameCharacterOutfits ──
        var outfitsTemplate = ReadTemplate(buildDir, "DT_GameCharacterOutfits_Default.json");
        if (outfitsTemplate != null)
        {
            var handleTemplate = ReadTemplate(buildDir, "DT_GameCharacterOutfits_Entry_Default.json");
            var addMods = mods.Where(m => m.Variant == ModVariant.Add).ToArray();
            var portMods = mods.Where(m => m.Variant == ModVariant.Port).ToArray();
            var allOutfitMods = addMods.Concat(portMods).ToArray();

            var json = outfitsTemplate;
            foreach (var charName in characters)
            {
                var upper = charName.ToUpperInvariant();
                var placeholder = $"[{upper}_OUTFITADD]";
                if (!json.Contains(placeholder)) continue;

                var charMods = allOutfitMods.Where(m =>
                    m.Character.Equals(charName, StringComparison.OrdinalIgnoreCase)).ToArray();

                if (charMods.Length == 0)
                {
                    // Remove placeholder — use IndexOf+splice to avoid Regex on large string
                    var commaPlaceholder = $",{placeholder}";
                    if (json.Contains(commaPlaceholder))
                        json = SpliceReplace(json, commaPlaceholder, "");
                    else
                        json = SpliceReplace(json, placeholder, "");
                    continue;
                }

                // Determine next sequential index: find the last numeric "Name" before
                // the placeholder to continue the vanilla numbering scheme.
                int lastIndex = 0;
                int placeholderIdx = json.IndexOf(placeholder, StringComparison.Ordinal);
                if (placeholderIdx > 0)
                {
                    var searchStart = Math.Max(0, placeholderIdx - 5000);
                    var searchRegion = json.Substring(searchStart, placeholderIdx - searchStart);
                    var matches = Regex.Matches(searchRegion, @"""Name"":\s*""(\d+)""");
                    if (matches.Count > 0)
                        lastIndex = int.Parse(matches[^1].Groups[1].Value);
                }

                int outfitNr = lastIndex + 1;
                var handles = new List<string>();
                foreach (var mod in charMods)
                {
                    if (handleTemplate == null) continue;
                    var handle = handleTemplate
                        .Replace("[CLOTHING_ID]", mod.ClothingId)
                        .Replace("[OUTFIT_NR]", outfitNr.ToString());
                    handles.Add(handle);
                    outfitNr++;
                }

                json = SpliceReplace(json, $",{placeholder}",
                    ",\n" + string.Join(",\n", handles));
            }

            // Add ClothingId entries to NameMap (FNames referenced in outfit handles)
            var outfitNameMap = allOutfitMods
                .Select(m => m.ClothingId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct().ToList();
            json = ReplaceNameMapStart(json, outfitNameMap);
            result["DT_GameCharacterOutfits"] = json;
        }

        // ── DT_GameCharacterCustomization ──
        var custTemplate = ReadTemplate(buildDir, "DT_GameCharacterCustomization_Default.json");
        if (custTemplate != null)
        {
            var entryTemplate = ReadTemplate(buildDir, "DT_GameCharacterCustomization_Entry_Default.json");
            var hairTemplate = ReadTemplate(buildDir, "DT_GameCharacterCustomization_HairEntry_Default.json");
            var custMods = mods.Where(m => m.Variant == ModVariant.CharacterCustomization).ToArray();

            // Collect NameMap entries from cust mods
            var custNameMap = custMods.SelectMany(m => m.NameMapEntries).Distinct().ToList();

            var json = custTemplate;

            // Read CustomizerCharacters.txt for the placeholder character names
            var custCharFile = Path.Combine(buildDir, "CustomizerCharacters.txt");
            var custChars = File.Exists(custCharFile)
                ? File.ReadAllLines(custCharFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                : [];

            // Map of cust target → placeholder suffix
            var targetToSuffix = new Dictionary<string, string>
            {
                ["Hair"] = "HAIR", ["Beard"] = "BEARD", ["Skin"] = "SKIN",
                ["PubicHair"] = "PUBICHAIR", ["Eyes"] = "EYES",
                ["EyeLiner"] = "EYELINER", ["EyeShadow"] = "EYESHADOW",
                ["Lipstick"] = "LIPSTICK", ["Tanlines"] = "TANLINES"
            };

            foreach (var charName in custChars)
            {
                var upper = charName.ToUpperInvariant();
                foreach (var (target, suffix) in targetToSuffix)
                {
                    var placeholder = $"[{upper}_{suffix}]";
                    if (!json.Contains(placeholder)) continue;

                    var entries = custMods
                        .Where(m => m.Character.Equals(charName, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(m => m.CustEntries.Where(e => e.Target == target))
                        .ToList();

                    if (entries.Count == 0)
                    {
                        var commaPlaceholder = $",{placeholder}";
                        if (json.Contains(commaPlaceholder))
                            json = SpliceReplace(json, commaPlaceholder, "");
                        else
                            json = SpliceReplace(json, placeholder, "");
                        continue;
                    }

                    var instantiated = new List<string>();
                    foreach (var entry in entries)
                    {
                        var tmpl = (target == "Hair" && hairTemplate != null) ? hairTemplate : entryTemplate;
                        if (tmpl == null) continue;

                        var inst = tmpl
                            .Replace("[PACKAGE_PATH]", entry.Path)
                            .Replace("[PACKAGE_NAME]", entry.Name)
                            .Replace("[CUSTOM_NR]", entry.Name);

                        if (target == "Hair")
                        {
                            inst = inst
                                .Replace("[PHYSICS_PACKAGE_PATH]", entry.PhysicsPath)
                                .Replace("[PHYSICS_PACKAGE_NAME]", entry.PhysicsName);
                            // Handle IsZero for physics mesh: true if "None", false otherwise
                            var physZero = (entry.PhysicsPath == "None" || string.IsNullOrEmpty(entry.PhysicsPath));
                            inst = inst.Replace("[PHYSICS_ZERO]", physZero ? "true" : "false");
                        }

                        instantiated.Add(inst);
                    }

                    json = json.Replace($",{placeholder}",
                        ",\n" + string.Join(",\n", instantiated));
                }
            }

            // Add namemap entries for cust mods
            json = ReplaceNameMapStart(json, custNameMap);
            result["DT_GameCharacterCustomization"] = json;
        }

        // ── DT_Tattoo ──
        var tattooTemplate = ReadTemplate(buildDir, "DT_Tattoo_Default.json");
        if (tattooTemplate != null)
        {
            var entryTemplate = ReadTemplate(buildDir, "DT_Tattoo_Entry_Default.json");
            var tattooMods = mods.Where(m => m.Variant == ModVariant.Tattoo).ToArray();

            var entries = new List<string>();
            var tattooNameMap = new List<string>();

            foreach (var mod in tattooMods)
            {
                foreach (var tattoo in mod.TattooEntries)
                {
                    if (entryTemplate == null) continue;
                    entries.Add(InstantiateTattooEntry(entryTemplate, tattoo));
                }
                tattooNameMap.AddRange(mod.NameMapEntries);
            }

            var json = tattooTemplate;
            json = ReplaceEntryStart(json, entries);
            json = ReplaceNameMapStart(json, tattooNameMap.Distinct().ToList());
            result["DT_Tattoo"] = json;
        }

        // ── DT_SandboxProps — no mod generates sandbox props yet.
        // Don't include in result → ForgeGenerator will use fast file-copy path.

        // ── DT_GFur (no placeholders, copy Debug.json as-is for UAsset reconstruction) ──
        // DT_GFur has no placeholders — it's passed through unchanged.

        return result;
    }

    // ── Entry instantiation ──

    static string InstantiateClothesEntry(string template, ModData mod)
    {
        var text = template;
        text = text.Replace("[CLOTHING_ID]", mod.ClothingId);
        text = text.Replace("[CLOTHING_NAME]", mod.ClothingName);

        foreach (var (slotName, slot) in mod.Slots)
        {
            var prefix = slotName; // HEAD, CHEST, etc.

            // String replacements (inside JSON quotes in template)
            text = text.Replace($"[{prefix}_MESH_PATH]", slot.MeshPath == "None" ? "None" : slot.MeshPath);
            text = text.Replace($"[{prefix}_MESH_NAME]", slot.MeshName == "None" ? "None" : slot.MeshName);
            text = text.Replace($"[{prefix}_SEX_MESH_PATH]", slot.SexMeshPath == "None" ? "None" : slot.SexMeshPath);
            text = text.Replace($"[{prefix}_SEX_MESH_NAME]", slot.SexMeshName == "None" ? "None" : slot.SexMeshName);
            text = text.Replace($"[{prefix}_ICON_PATH]", slot.IconPath == "None" ? "None" : slot.IconPath);
            text = text.Replace($"[{prefix}_ICON_NAME]", slot.IconName == "None" ? "None" : slot.IconName);

            // Numeric replacements (raw values, no quotes in template)
            text = text.Replace($"[{prefix}_PHYSICSAREAS]", slot.PhysicsAreas.ToString());
            text = text.Replace($"[{prefix}_FLEXREGIONS]", slot.FlexRegions.ToString());
            text = text.Replace($"[{prefix}_MORPHTARGETVALUE]",
                slot.MorphTargetValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture));

            // NamePropertyData replacements: null means JSON null, non-null means quoted string.
            // UAssetAPI serializes unset FNames as JSON null, NOT as the string "null".
            text = text.Replace($"[{prefix}_MORPHTARGET]",
                slot.MorphTarget == "null" ? "null" : $"\"{slot.MorphTarget}\"");
            text = text.Replace($"[{prefix}_CONSTRAINTPROFILE]",
                slot.ConstraintProfile == "null" ? "null" : $"\"{slot.ConstraintProfile}\"");

            // ArousalBlend: in the template it's a string property value (inside quotes)
            text = text.Replace($"[{prefix}_AROUSALBLEND]",
                slot.ArousalBlend.ToString("G", System.Globalization.CultureInfo.InvariantCulture));

            // FurMask paths
            text = text.Replace($"[{prefix}_FURMASK_PATH]", slot.FurMaskPath);
            text = text.Replace($"[{prefix}_FURMASK_NAME]", slot.FurMaskName);
            text = text.Replace($"[{prefix}_SEX_FURMASK_PATH]", slot.SexFurMaskPath);
            text = text.Replace($"[{prefix}_SEX_FURMASK_NAME]", slot.SexFurMaskName);
        }

        return text;
    }

    static string InstantiateTattooEntry(string template, TattooData tattoo)
    {
        var text = template;
        text = text.Replace("[TATTOO_ID]", tattoo.TattooId);
        text = text.Replace("[TATTOO_DISPLAY_NAME]", tattoo.DisplayName);
        text = text.Replace("[TATTOO_TEXTURE_PATH]", tattoo.TexturePath);
        text = text.Replace("[TATTOO_TEXTURE_NAME]", tattoo.TextureName);
        text = text.Replace("[TATTOO_ICON_PATH]", tattoo.IconPath);
        text = text.Replace("[TATTOO_ICON_NAME]", tattoo.IconName);

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        text = text.Replace("[TATTOO_COLOR_R]", tattoo.ColorR.ToString("G", ic));
        text = text.Replace("[TATTOO_COLOR_G]", tattoo.ColorG.ToString("G", ic));
        text = text.Replace("[TATTOO_COLOR_B]", tattoo.ColorB.ToString("G", ic));
        text = text.Replace("[TATTOO_COLOR_A]", tattoo.ColorA.ToString("G", ic));

        text = text.Replace("[TATTOO_COVERED_SLOTS]", tattoo.CoveredSlots.ToString());
        text = text.Replace("[TATTOO_COST]", tattoo.Cost.ToString());

        text = text.Replace("[TATTOO_UV_SET]", tattoo.UVSet);

        // TraderType: "None" → JSON null, otherwise quoted enum value
        text = text.Replace("[TATTOO_TRADER_TYPE]",
            tattoo.TraderType == "None" ? "null" : $"\"{tattoo.TraderType}\"");

        return text;
    }

    // ── Placeholder replacement (StringBuilder-based to avoid 60MB string copies) ──

    static readonly Regex RxEntryStartEmpty = new(@",?\s*\[ENTRYSTART\]", RegexOptions.Compiled);
    static readonly Regex RxNameMapStartEmpty = new(@",?\s*\[NAMEMAPSTART\]\s*\n?", RegexOptions.Compiled);

    static string SpliceReplace(string json, string marker, string insert)
    {
        int idx = json.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return json;
        var sb = new StringBuilder(json.Length + insert.Length);
        sb.Append(json, 0, idx);
        sb.Append(insert);
        sb.Append(json, idx + marker.Length, json.Length - idx - marker.Length);
        return sb.ToString();
    }

    static string ReplaceEntryStart(string json, List<string> entries)
    {
        if (entries.Count == 0)
            return RxEntryStartEmpty.Replace(json, "", 1); // single match, pre-compiled
        return SpliceReplace(json, "[ENTRYSTART]", string.Join(",\n", entries));
    }

    static string ReplaceNameMapStart(string json, List<string> entries)
    {
        if (entries.Count == 0)
            return RxNameMapStartEmpty.Replace(json, "\n", 1);
        var formatted = string.Join(",\n    ", entries.Select(e => $"\"{e}\""));
        return SpliceReplace(json, "[NAMEMAPSTART]", formatted);
    }

    static string? ReadTemplate(string buildDir, string filename)
    {
        var path = Path.Combine(buildDir, filename);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
