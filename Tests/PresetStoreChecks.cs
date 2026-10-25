using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class PresetStoreChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static MiragePreset Example(string name = "Daytime outlines")
    {
        return new MiragePreset
        {
            schemaVersion = 1, name = name,
            classes = new List<MiragePresetClass> { new MiragePresetClass
            {
                classIds = new[] { 2, 7 }, effects = new List<MiragePresetEffect>
                { new MiragePresetEffect { effect = "Outline", minimum = 0, maximum = 100, rgba = new[] { 1f, 0.5f, 0f, 1f } } }
            } },
            layers = new List<MiragePresetLayer> { new MiragePresetLayer { name = "Outline", enabled = true } },
            runners = new List<MiragePresetRunner> { new MiragePresetRunner { role = "inpainting", updateRate = 1 } },
            depthEnabled = false, inpaintingEnabled = false,
            depthUnits = "relative", depthModel = "DA3-SMALL"
        };
    }
    public static string Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "mirage-preset-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string name in new[] { "../escape", "C:\\escape", ".hidden", "CON", "aux", "LPT1", "COM¹", "bad/name", "", new string('x', 65) })
                Check(!MiragePresetStore.TryNormalizeName(name, out _, out _), "Unsafe name accepted: " + name);
            Check(MiragePresetStore.TryNormalizeName("  Nächtliche Straße  ", out string normalized, out _) && normalized == "Nächtliche Straße", "Unicode name/trim failed");
            var store = new MiragePresetStore(root);
            var example = Example();
            Check(store.TrySave(example.name, example, out string error), error);
            Check(store.TryLoad(example.name, out MiragePreset loaded, out error), error);
            Check(loaded.classes[0].classIds.SequenceEqual(new[] { 2, 7 }) && loaded.classes[0].effects[0].rgba[1] == 0.5f,
                "Class group or packed color did not round-trip");
            Check(loaded.runners[0].role == "inpainting" && loaded.layers[0].enabled && !loaded.depthEnabled, "Layer/model flags changed");
            example.classes[0].effects[0].rgba[1] = 0.75f;
            Check(store.TrySave(example.name, example, out error), error);
            Check(store.TryLoad(example.name, out loaded, out error) && loaded.classes[0].effects[0].rgba[1] == 0.75f, "Atomic overwrite failed");
            string original = File.ReadAllText(Path.Combine(root, example.name + ".json"));
            var invalid = Example(); invalid.classes[0].effects[0].minimum = float.NaN;
            Check(!store.TrySave(invalid.name, invalid, out error), "NaN range accepted");
            Check(File.ReadAllText(Path.Combine(root, example.name + ".json")) == original, "Invalid save replaced the good preset");
            invalid = Example(); invalid.classes[0].effects[0].effect = "FutureEffect";
            Check(!MiragePresetStore.Validate(invalid, out error), "Unknown effect accepted");
            invalid = Example(); invalid.classes[0].effects[0].rgba[2] = float.PositiveInfinity;
            Check(!MiragePresetStore.Validate(invalid, out error), "Infinite color accepted");
            invalid = Example(); invalid.classes[0].classIds = new[] { 2, 2 };
            Check(!MiragePresetStore.Validate(invalid, out error), "Duplicate class IDs accepted");
            invalid = Example(); invalid.classes.Add(invalid.classes[0]);
            Check(!MiragePresetStore.Validate(invalid, out error), "Duplicate class group accepted");
            invalid = Example(); invalid.layers.Add(invalid.layers[0]);
            Check(!MiragePresetStore.Validate(invalid, out error), "Duplicate display layer accepted");
            invalid = Example(); invalid.runners[0].updateRate = -0.1f;
            Check(!MiragePresetStore.Validate(invalid, out error), "Invalid scheduling rate accepted");
            invalid = Example(); invalid.classes[0].effects[0].effect = "GaussianBlur";
            Check(!MiragePresetStore.Validate(invalid, out error), "Out-of-range Gaussian parameters accepted");
            invalid = Example(); invalid.schemaVersion = 2;
            Check(!MiragePresetStore.Validate(invalid, out error), "Unknown schema version accepted");
            File.WriteAllText(Path.Combine(root, "Malformed.json"), "{}");
            Check(!store.TryLoad("Malformed", out loaded, out error) && loaded == null, "Missing version accepted");
            File.WriteAllText(Path.Combine(root, "Malformed.json"), original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"unsupported\": true"));
            Check(!store.TryLoad("Malformed", out loaded, out error), "Unknown JSON property accepted");
            File.WriteAllText(Path.Combine(root, "Malformed.json"), new string(' ', MiragePresetStore.MaximumFileBytes + 1));
            Check(!store.TryLoad("Malformed", out loaded, out error), "Oversized preset accepted");
            Check(store.TrySaveLast(example, out error) && store.HasLastSession, error);
            Check(store.TryLoadLast(out loaded, out error) && loaded.classes[0].effects[0].rgba[1] == 0.75f, "Last-session round-trip failed");
            Check(!store.GetNames().Any(name => name.StartsWith(".", StringComparison.Ordinal)), "Internal last-session file appears as a named preset");
            Check(!Directory.GetFiles(root).Any(path => path.Contains(".tmp-")), "Temporary files were left after writes");
            string blocker = Path.Combine(root, "not-a-folder"); File.WriteAllText(blocker, "occupied");
            var unavailable = new MiragePresetStore(blocker);
            Check(!unavailable.TrySave(example.name, example, out error) && !string.IsNullOrEmpty(error), "Unavailable folder did not return a useful error");
            return "PASS: actual preset store round-trips, atomic replacement, unsafe names, schema/parameter validation, size bounds, last session, hidden-file filtering and storage errors.";
        }
        finally { Directory.Delete(root, true); }
    }
}
