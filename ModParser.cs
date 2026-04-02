using System.Text.RegularExpressions;

public enum ModVariant { Add, Port, CharacterCustomization, Tattoo }

public class SlotData
{
    public string MeshPath = "None", MeshName = "None";
    public string SexMeshPath = "None", SexMeshName = "None";
    public string IconPath = "None", IconName = "None";
    public int PhysicsAreas, FlexRegions;
    public string MorphTarget = "null", ConstraintProfile = "null";
    public float MorphTargetValue = 1.0f, ArousalBlend;
    public string FurMaskPath = "None", FurMaskName = "None";
    public string SexFurMaskPath = "None", SexFurMaskName = "None";
}

public class CustEntry
{
    public string Target = "";       // Hair, Skin, Eyes, etc.
    public string Path = "None", Name = "None";
    public string PhysicsPath = "None", PhysicsName = "None"; // Hair only
}

public class TattooData
{
    public string TattooId = "", DisplayName = "";
    public string TexturePath = "None", TextureName = "None";
    public string IconPath = "None", IconName = "None";
    public float ColorR = 0f, ColorG = 0f, ColorB = 0f, ColorA = 1.0f;
    public int CoveredSlots;
    public int Cost = 100;
    public string UVSet = "Set1";
    public string TraderType = "None";
}

public class ModData
{
    public string ModName = "", Author = "", Character = "";
    public ModVariant Variant;
    public string[] NameMapEntries = [];
    public string ClothingId = "", ClothingName = "";
    public Dictionary<string, SlotData> Slots = new(); // HEAD, CHEST, HANDS, LEGS, FEET
    public List<CustEntry> CustEntries = new();
    public List<TattooData> TattooEntries = new();
}

public static class ModParser
{
    static readonly string[] SlotNames = ["HEAD", "CHEST", "HANDS", "LEGS", "FEET"];
    static readonly string[] SlotFields = [
        "MESH_PATH", "MESH_NAME", "SEX_MESH_PATH", "SEX_MESH_NAME",
        "ICON_PATH", "ICON_NAME", "PHYSICSAREAS", "MORPHTARGET",
        "MORPHTARGETVALUE", "CONSTRAINTPROFILE", "AROUSALBLEND", "FLEXREGIONS",
        "FURMASK_PATH", "FURMASK_NAME", "SEX_FURMASK_PATH", "SEX_FURMASK_NAME"
    ];

    static readonly string[] CustTargets = [
        "Hair", "Beard", "Skin", "PubicHair", "Eyes",
        "EyeLiner", "EyeShadow", "Lipstick", "Tanlines"
    ];

    public static ModData Parse(string txtPath)
    {
        var content = File.ReadAllText(txtPath);
        var variant = DetectVariant(content);
        return variant switch
        {
            ModVariant.Add => ParseAdd(content),
            ModVariant.Port => ParsePort(content),
            ModVariant.CharacterCustomization => ParseCharCust(content),
            ModVariant.Tattoo => ParseTattoo(content),
            _ => throw new Exception($"Unknown variant in {txtPath}")
        };
    }

    static ModVariant DetectVariant(string content)
    {
        var m = Regex.Match(content, @"^Variant:\s*(.*)$", RegexOptions.Multiline);
        if (!m.Success) return ModVariant.Add; // default
        var v = m.Groups[1].Value.Trim();
        return v switch
        {
            "Character Customization" => ModVariant.CharacterCustomization,
            "Port" => ModVariant.Port,
            "Tattoo" => ModVariant.Tattoo,
            _ => ModVariant.Add
        };
    }

    static string Get(string content, string key)
    {
        var escaped = Regex.Escape(key);
        var m = Regex.Match(content, $@"^{escaped}:\s*(.*)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    static ModData ParseAdd(string content)
    {
        var mod = new ModData
        {
            Variant = ModVariant.Add,
            ModName = Get(content, "ModName"),
            Author = Get(content, "Author"),
            Character = Get(content, "Character"),
            ClothingId = Get(content, "[CLOTHING_ID]"),
            ClothingName = Get(content, "[CLOTHING_NAME]"),
        };

        var nameMapRaw = Get(content, "NameMap");
        mod.NameMapEntries = string.IsNullOrEmpty(nameMapRaw)
            ? []
            : nameMapRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim()).ToArray();

        foreach (var slot in SlotNames)
        {
            var sd = new SlotData
            {
                MeshPath = Get(content, $"[{slot}_MESH_PATH]"),
                MeshName = Get(content, $"[{slot}_MESH_NAME]"),
                SexMeshPath = Get(content, $"[{slot}_SEX_MESH_PATH]"),
                SexMeshName = Get(content, $"[{slot}_SEX_MESH_NAME]"),
                IconPath = Get(content, $"[{slot}_ICON_PATH]"),
                IconName = Get(content, $"[{slot}_ICON_NAME]"),
                FurMaskPath = Get(content, $"[{slot}_FURMASK_PATH]"),
                FurMaskName = Get(content, $"[{slot}_FURMASK_NAME]"),
                SexFurMaskPath = Get(content, $"[{slot}_SEX_FURMASK_PATH]"),
                SexFurMaskName = Get(content, $"[{slot}_SEX_FURMASK_NAME]"),
            };

            if (int.TryParse(Get(content, $"[{slot}_PHYSICSAREAS]"), out var pa))
                sd.PhysicsAreas = pa;
            if (int.TryParse(Get(content, $"[{slot}_FLEXREGIONS]"), out var fr))
                sd.FlexRegions = fr;
            if (float.TryParse(Get(content, $"[{slot}_MORPHTARGETVALUE]"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mtv))
                sd.MorphTargetValue = mtv;
            if (float.TryParse(Get(content, $"[{slot}_AROUSALBLEND]"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ab))
                sd.ArousalBlend = ab;

            var morphRaw = Get(content, $"[{slot}_MORPHTARGET]");
            sd.MorphTarget = morphRaw.StartsWith('"') && morphRaw.EndsWith('"')
                ? morphRaw[1..^1] : (string.IsNullOrEmpty(morphRaw) ? "null" : morphRaw);

            var constraintRaw = Get(content, $"[{slot}_CONSTRAINTPROFILE]");
            sd.ConstraintProfile = constraintRaw.StartsWith('"') && constraintRaw.EndsWith('"')
                ? constraintRaw[1..^1] : (string.IsNullOrEmpty(constraintRaw) ? "null" : constraintRaw);

            if (string.IsNullOrEmpty(sd.MeshPath)) sd.MeshPath = "None";
            if (string.IsNullOrEmpty(sd.MeshName)) sd.MeshName = "None";
            if (string.IsNullOrEmpty(sd.SexMeshPath)) sd.SexMeshPath = "None";
            if (string.IsNullOrEmpty(sd.SexMeshName)) sd.SexMeshName = "None";
            if (string.IsNullOrEmpty(sd.IconPath)) sd.IconPath = "None";
            if (string.IsNullOrEmpty(sd.IconName)) sd.IconName = "None";
            if (string.IsNullOrEmpty(sd.FurMaskPath)) sd.FurMaskPath = "None";
            if (string.IsNullOrEmpty(sd.FurMaskName)) sd.FurMaskName = "None";
            if (string.IsNullOrEmpty(sd.SexFurMaskPath)) sd.SexFurMaskPath = "None";
            if (string.IsNullOrEmpty(sd.SexFurMaskName)) sd.SexFurMaskName = "None";

            mod.Slots[slot] = sd;
        }

        return mod;
    }

    static ModData ParsePort(string content)
    {
        return new ModData
        {
            Variant = ModVariant.Port,
            ModName = Get(content, "ModName"),
            Author = Get(content, "Author"),
            Character = Get(content, "Character"),
            ClothingId = Get(content, "[CLOTHING_ID]"),
        };
    }

    static ModData ParseCharCust(string content)
    {
        var mod = new ModData
        {
            Variant = ModVariant.CharacterCustomization,
            ModName = Get(content, "ModName"),
            Author = Get(content, "Author"),
            Character = Get(content, "Character"),
        };

        var nameMapRaw = Get(content, "NameMap");
        mod.NameMapEntries = string.IsNullOrEmpty(nameMapRaw)
            ? []
            : nameMapRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim()).ToArray();

        foreach (var line in content.Split('\n'))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;
            var key = line[..colonIdx].Trim();
            var val = line[(colonIdx + 1)..].Trim();

            if (!CustTargets.Contains(key)) continue;

            var parts = val.Split(',').Select(p => p.Trim()).ToArray();
            if (key == "Hair" && parts.Length >= 4)
            {
                mod.CustEntries.Add(new CustEntry
                {
                    Target = "Hair",
                    Path = parts[0], Name = parts[1],
                    PhysicsPath = parts[2], PhysicsName = parts[3]
                });
            }
            else if (parts.Length >= 2)
            {
                mod.CustEntries.Add(new CustEntry
                {
                    Target = key,
                    Path = parts[0], Name = parts[1]
                });
            }
        }

        return mod;
    }

    // Tattoos are character-agnostic (global DT_Tattoo, not per-character).
    // mod.Character is intentionally left empty.
    static ModData ParseTattoo(string content)
    {
        var mod = new ModData
        {
            Variant = ModVariant.Tattoo,
            ModName = Get(content, "ModName"),
            Author = Get(content, "Author"),
        };

        var nameMapRaw = Get(content, "NameMap");
        mod.NameMapEntries = string.IsNullOrEmpty(nameMapRaw)
            ? []
            : nameMapRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim()).ToArray();

        var tattoo = new TattooData
        {
            TattooId = Get(content, "[TATTOO_ID]"),
            DisplayName = Get(content, "[TATTOO_DISPLAY_NAME]"),
            TexturePath = Get(content, "[TATTOO_TEXTURE_PATH]"),
            TextureName = Get(content, "[TATTOO_TEXTURE_NAME]"),
            IconPath = Get(content, "[TATTOO_ICON_PATH]"),
            IconName = Get(content, "[TATTOO_ICON_NAME]"),
            UVSet = Get(content, "[TATTOO_UV_SET]"),
            TraderType = Get(content, "[TATTOO_TRADER_TYPE]"),
        };

        if (string.IsNullOrEmpty(tattoo.TexturePath)) tattoo.TexturePath = "None";
        if (string.IsNullOrEmpty(tattoo.TextureName)) tattoo.TextureName = "None";
        if (string.IsNullOrEmpty(tattoo.IconPath)) tattoo.IconPath = tattoo.TexturePath;
        if (string.IsNullOrEmpty(tattoo.IconName)) tattoo.IconName = tattoo.TextureName;
        if (string.IsNullOrEmpty(tattoo.UVSet)) tattoo.UVSet = "Set1";
        if (string.IsNullOrEmpty(tattoo.TraderType)) tattoo.TraderType = "None";

        if (float.TryParse(Get(content, "[TATTOO_COLOR_R]"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var r)) tattoo.ColorR = r;
        if (float.TryParse(Get(content, "[TATTOO_COLOR_G]"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var g)) tattoo.ColorG = g;
        if (float.TryParse(Get(content, "[TATTOO_COLOR_B]"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var b)) tattoo.ColorB = b;
        if (float.TryParse(Get(content, "[TATTOO_COLOR_A]"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var a)) tattoo.ColorA = a;

        if (int.TryParse(Get(content, "[TATTOO_COVERED_SLOTS]"), out var slots))
            tattoo.CoveredSlots = slots;
        if (int.TryParse(Get(content, "[TATTOO_COST]"), out var cost))
            tattoo.Cost = cost;

        if (string.IsNullOrWhiteSpace(tattoo.TattooId))
        {
            Console.Error.WriteLine($"  WARN: skipping tattoo with empty TattooId in {mod.ModName}");
            return mod;
        }

        mod.TattooEntries.Add(tattoo);
        return mod;
    }
}
