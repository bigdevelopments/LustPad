using System.Text.Json;
using System.Text.Json.Serialization;

namespace LustPad.Core.Presets;

public static class PresetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Save(string path, PadParameters parameters)
    {
        var json = JsonSerializer.Serialize(parameters, Options);
        File.WriteAllText(path, json);
    }

    public static PadParameters Load(string path)
    {
        var json = File.ReadAllText(path);
        var p = JsonSerializer.Deserialize<PadParameters>(json, Options)
                ?? throw new InvalidOperationException("Preset file was empty or invalid.");
        return p;
    }

    public static IReadOnlyList<(string Name, Func<PadParameters> Factory)> BuiltInPresets { get; } =
    [
        ("Lush Pad", PadParameters.CreateDefaultLush),
        ("Warm Drone", PadParameters.CreateWarmDrone),
        ("Shimmer Pad", PadParameters.CreateShimmer),
        ("Dark Pad", PadParameters.CreateDarkPad),
        ("Ooh Choir", PadParameters.CreateOohChoir),
        ("Ahhh Pad", PadParameters.CreateAhhhPad),
    ];
}
