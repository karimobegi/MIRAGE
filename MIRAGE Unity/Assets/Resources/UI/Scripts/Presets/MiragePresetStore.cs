using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

/// <summary>Portable effect settings only: no scene, model assets, video paths or participant data.</summary>
[Serializable]
public sealed class MiragePreset
{
    public const int CurrentVersion = 1;
    public int schemaVersion;
    public string name;
    public List<MiragePresetClass> classes;
    public List<MiragePresetLayer> layers;
    public List<MiragePresetRunner> runners;
    public bool depthEnabled;
    public bool inpaintingEnabled;
    public string depthUnits;
    public string depthModel;
}

[Serializable]
public sealed class MiragePresetClass
{
    public int[] classIds;
    public List<MiragePresetEffect> effects;
}

[Serializable]
public sealed class MiragePresetEffect
{
    public string effect;
    public float minimum;
    public float maximum;
    public float[] rgba;
}

[Serializable]
public sealed class MiragePresetLayer
{
    public string name;
    public bool enabled;
}

[Serializable]
public sealed class MiragePresetRunner
{
    // A role survives a scene rename and a switch between MI-GAN and Moebius.
    public string role;
    public float updateRate;
}

/// <summary>Versioned, bounded, atomic preset files. Independent of Unity for direct filesystem tests.</summary>
public sealed class MiragePresetStore
{
    public const int MaximumFileBytes = 1024 * 1024;
    private const string LastSessionFile = ".last-session.json";
    private readonly string directory;
    private static readonly string[] Effects = {
        "ColorArea", "Outline", "Opacity", "Inpainting", "Icon", "BBox", "Info",
        "GaussianBlur", "Kuwahara", "Replace", "Transform", "Utility"
    };
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.None,
        MissingMemberHandling = MissingMemberHandling.Error,
        MaxDepth = 16,
        Formatting = Formatting.Indented
    };

    public MiragePresetStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Preset folder is required.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
    }

    public string DirectoryPath => directory;

    public static bool TryNormalizeName(string value, out string name, out string error)
    {
        name = value == null ? "" : value.Trim();
        if (name.Length == 0 || name.Length > 64 || !char.IsLetterOrDigit(name[0]) ||
            name.Any(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_'))
        {
            error = "Use 1–64 letters, numbers, spaces, hyphens or underscores, starting with a letter or number.";
            return false;
        }
        string upper = name.ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(upper) ||
            (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) &&
             ((upper[3] >= '0' && upper[3] <= '9') || upper[3] == '¹' || upper[3] == '²' || upper[3] == '³')))
        {
            error = "That name is reserved by Windows. Choose a different preset name.";
            return false;
        }
        error = null;
        return true;
    }

    public bool Exists(string name)
    {
        return TryNormalizeName(name, out string valid, out _) && File.Exists(PresetPath(valid));
    }

    public string[] GetNames()
    {
        if (!System.IO.Directory.Exists(directory)) return Array.Empty<string>();
        return System.IO.Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => TryNormalizeName(name, out _, out _))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
    }

    public bool TrySave(string name, MiragePreset preset, out string error)
    {
        if (!TryNormalizeName(name, out string valid, out error)) return false;
        if (!Validate(preset, out error)) return false;
        preset.name = valid;
        return TryWrite(PresetPath(valid), preset, out error);
    }

    public bool TryLoad(string name, out MiragePreset preset, out string error)
    {
        preset = null;
        return TryNormalizeName(name, out string valid, out error) && TryRead(PresetPath(valid), out preset, out error);
    }

    public bool HasLastSession => File.Exists(Path.Combine(directory, LastSessionFile));
    public bool TrySaveLast(MiragePreset preset, out string error)
    {
        return Validate(preset, out error) && TryWrite(Path.Combine(directory, LastSessionFile), preset, out error);
    }
    public bool TryLoadLast(out MiragePreset preset, out string error)
    {
        return TryRead(Path.Combine(directory, LastSessionFile), out preset, out error);
    }

    public static string ClassKey(IEnumerable<int> ids) => string.Join(",", ids.OrderBy(id => id));

    public static bool Validate(MiragePreset preset, out string error)
    {
        error = null;
        if (preset == null || preset.schemaVersion != MiragePreset.CurrentVersion)
            return Invalid("This preset format is not supported. Use a version 1 MIRAGE preset.", out error);
        if (!TryNormalizeName(preset.name, out _, out error)) return false;
        if ((preset.depthUnits != "relative" && preset.depthUnits != "metres") ||
            (preset.depthModel != null && (preset.depthModel.Length > 128 || preset.depthModel.Any(char.IsControl))))
            return Invalid("The preset has invalid depth-unit or model metadata.", out error);
        if (preset.classes == null || preset.layers == null || preset.runners == null ||
            preset.classes.Count > 64 || preset.layers.Count > 64 || preset.runners.Count > 3)
            return Invalid("The preset is incomplete or contains too many settings.", out error);
        var classGroups = new HashSet<string>(StringComparer.Ordinal);
        int effectCount = 0;
        foreach (var item in preset.classes)
        {
            if (item == null || item.classIds == null || item.classIds.Length == 0 || item.classIds.Length > 256 ||
                item.classIds.Any(id => id < 0 || id > 10000) || item.classIds.Distinct().Count() != item.classIds.Length ||
                item.effects == null || item.effects.Count > 64 || !classGroups.Add(ClassKey(item.classIds)))
                return Invalid("The preset contains an invalid or duplicate object category.", out error);
            foreach (var effect in item.effects)
            {
                effectCount++;
                if (effect == null || !Effects.Contains(effect.effect) || !Finite(effect.minimum) || !Finite(effect.maximum) ||
                    effect.minimum < 0 || effect.maximum > 100 || effect.minimum > effect.maximum || effect.rgba == null ||
                    effect.rgba.Length != 4 || effect.rgba.Any(v => !Finite(v) || v < 0 || v > 1))
                    return Invalid("The preset contains an unknown effect or an invalid range/color value.", out error);
                if (effect.effect == "GaussianBlur" && (effect.rgba[0] > 30f / 255f + 0.000001f || effect.rgba[1] > 16f / 255f + 0.000001f))
                    return Invalid("Gaussian blur radius or sigma exceeds the supported limits.", out error);
                if (effect.effect == "Kuwahara" && (effect.rgba[0] < 1f / 255f - 0.000001f || effect.rgba[0] > 10f / 255f + 0.000001f ||
                    effect.rgba[1] < 2f / 255f - 0.000001f || effect.rgba[1] > 16f / 255f + 0.000001f || effect.rgba[2] > 4f / 255f + 0.000001f))
                    return Invalid("Painterly filter parameters exceed the supported slider limits.", out error);
            }
        }
        if (effectCount > 512) return Invalid("The preset contains too many effects.", out error);
        var layers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in preset.layers)
            if (layer == null || string.IsNullOrWhiteSpace(layer.name) || layer.name.Length > 128 || !layers.Add(layer.name))
                return Invalid("The preset contains an invalid or duplicate display layer.", out error);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runner in preset.runners)
            if (runner == null || (runner.role != "segmentation" && runner.role != "depth" && runner.role != "inpainting") ||
                !roles.Add(runner.role) || !Finite(runner.updateRate) || runner.updateRate < 0 || runner.updateRate > 1)
                return Invalid("The preset contains an invalid model scheduling setting.", out error);
        return true;
    }

    private static bool Invalid(string message, out string error) { error = message; return false; }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private string PresetPath(string name) => Path.Combine(directory, name + ".json");

    private bool TryRead(string path, out MiragePreset preset, out string error)
    {
        preset = null;
        try
        {
            if (!File.Exists(path)) return Invalid("The preset file was not found. Refresh the list or choose another preset.", out error);
            // Check the opened stream, not a prior file-info snapshot. Sharing
            // only for reads prevents a writer growing the file during a load.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumFileBytes) return Invalid("The preset file is larger than 1 MB.", out error);
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    preset = JsonConvert.DeserializeObject<MiragePreset>(reader.ReadToEnd(), JsonSettings);
            }
            if (!Validate(preset, out error)) { preset = null; return false; }
            return true;
        }
        catch (JsonException) { return Invalid("The preset JSON is invalid or contains unsupported fields. The current settings were kept.", out error); }
        catch (Exception exception) when (IsStorageError(exception))
        { return Invalid("The preset could not be read. Check that your user profile folder is accessible.", out error); }
    }

    private bool TryWrite(string path, MiragePreset preset, out string error)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string json = JsonConvert.SerializeObject(preset, JsonSettings);
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            if (bytes.Length > MaximumFileBytes) return Invalid("The preset is larger than 1 MB.", out error);
            System.IO.Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            error = null;
            return true;
        }
        catch (Exception exception) when (IsStorageError(exception))
        { return Invalid("The preset could not be saved. Check free disk space and permission to write to your user profile.", out error); }
        finally
        {
            // Only our unique temporary file is removed; the prior preset survives a failed replacement.
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception exception) when (IsStorageError(exception)) { }
        }
    }

    private static bool IsStorageError(Exception exception) => exception is IOException || exception is UnauthorizedAccessException ||
        exception is System.Security.SecurityException || exception is ArgumentException || exception is NotSupportedException;
}
