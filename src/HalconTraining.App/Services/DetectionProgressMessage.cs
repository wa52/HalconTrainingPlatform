using System.Text.Json;

namespace HalconTraining.App.Services;

public sealed record DetectionProgressMessage(string Stage, int? Current, int? Total, double? Loss)
{
    public static DetectionProgressMessage? Parse(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("stage", out var stage) || stage.ValueKind != JsonValueKind.String) return null;
            var detail = root.TryGetProperty("detail", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            int? ReadInt(string name) => detail.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
            double? loss = null;
            foreach (var name in new[] { "loss", "meanLoss", "trainingLoss" })
                if (detail.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) { loss = number; break; }
            return new(stage.GetString() ?? "", ReadInt("epoch") ?? ReadInt("processed"), ReadInt("epochs") ?? ReadInt("total"), loss);
        }
        catch (JsonException) { return null; }
    }
}
