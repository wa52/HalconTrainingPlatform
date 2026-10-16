namespace HalconTraining.Domain.Training;

public sealed record DatasetSplit(int Seed, int[] Train, int[] Validation, int[] Test)
{
    public static DatasetSplit Create(IReadOnlyList<string?> groups, int seed = 42, double trainPercent = 70, double validationPercent = 15)
    {
        if (!double.IsFinite(trainPercent) || !double.IsFinite(validationPercent) || trainPercent <= 0 || validationPercent <= 0 || trainPercent + validationPercent >= 100)
            throw new ArgumentException("训练、验证、测试比例必须均大于0，合计100%。");
        var random = new Random(seed);
        var order = Enumerable.Range(0, groups.Count).OrderBy(_ => random.Next()).ToArray();
        var train = (int)Math.Round(groups.Count * trainPercent / 100);
        var validation = (int)Math.Round(groups.Count * validationPercent / 100);
        if (groups.All(string.IsNullOrWhiteSpace))
            return new(seed, order.Take(train).ToArray(), order.Skip(train).Take(validation).ToArray(), order.Skip(train + validation).ToArray());
        var buckets = new[] { new List<int>(), new List<int>(), new List<int>() };
        var targets = new[] { train, validation, groups.Count - train - validation };
        foreach (var group in order.GroupBy(i => string.IsNullOrWhiteSpace(groups[i]) ? $"image:{i}" : "group:" + groups[i]))
        {
            var destination = Enumerable.Range(0, 3).OrderByDescending(i => targets[i] - buckets[i].Count).First();
            buckets[destination].AddRange(group);
        }
        return new(seed, buckets[0].ToArray(), buckets[1].ToArray(), buckets[2].ToArray());
    }
}
