using DataTrace.Plc.Addresses;

namespace DataTrace.Plc.Planning;

public sealed class AddressReadRequest
{
    public required string Key { get; init; }
    public required PlcAddress Address { get; init; }
    public required int WordCount { get; init; }
}

public sealed class ReadBlock
{
    public required string Area { get; init; }
    public required int StartOffset { get; init; }
    public required int WordCount { get; init; }
}

public sealed class ReadSlice
{
    public required int BlockIndex { get; init; }
    public required int OffsetInBlock { get; init; }
    public required int WordCount { get; init; }
}

public sealed class ReadPlan
{
    public IReadOnlyList<ReadBlock> Blocks { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ReadSlice>> Items { get; }

    public ReadPlan(IReadOnlyList<ReadBlock> blocks, IReadOnlyDictionary<string, IReadOnlyList<ReadSlice>> items)
    {
        Blocks = blocks;
        Items = items;
    }

    public ushort[] GetWords(string key, IReadOnlyList<ushort[]> blockData)
    {
        if (!Items.TryGetValue(key, out var slices))
        {
            return [];
        }

        var total = slices.Sum(s => s.WordCount);
        var result = new ushort[total];
        var cursor = 0;
        foreach (var slice in slices)
        {
            var src = blockData[slice.BlockIndex];
            Array.Copy(src, slice.OffsetInBlock, result, cursor, slice.WordCount);
            cursor += slice.WordCount;
        }

        return result;
    }
}

public static class ReadPlanBuilder
{
    public static ReadPlan Build(
        IEnumerable<AddressReadRequest> requests,
        int maxWordsPerRead,
        int mergeGapWords)
    {
        if (maxWordsPerRead <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWordsPerRead));
        }

        var wordRequests = requests
            .Where(r => r.Address.Kind == AddressKind.Word && r.WordCount > 0)
            .Select(r => new Item(r.Key, r.Address.Area, r.Address.Offset, r.WordCount))
            .ToList();

        var blocks = new List<ReadBlock>();
        var itemSlices = new Dictionary<string, List<ReadSlice>>(StringComparer.OrdinalIgnoreCase);

        foreach (var areaGroup in wordRequests.GroupBy(x => x.Area, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = areaGroup.OrderBy(x => x.Start).ToList();
            var clusters = BuildClusters(ordered, mergeGapWords);
            foreach (var cluster in clusters)
            {
                var chunks = Split(cluster.Start, cluster.End, maxWordsPerRead);
                foreach (var chunk in chunks)
                {
                    var blockIndex = blocks.Count;
                    blocks.Add(new ReadBlock
                    {
                        Area = areaGroup.Key,
                        StartOffset = chunk.Start,
                        WordCount = chunk.End - chunk.Start
                    });

                    foreach (var item in cluster.Items)
                    {
                        var overlapStart = Math.Max(item.Start, chunk.Start);
                        var overlapEnd = Math.Min(item.End, chunk.End);
                        if (overlapStart >= overlapEnd)
                        {
                            continue;
                        }

                        if (!itemSlices.TryGetValue(item.Key, out var list))
                        {
                            list = [];
                            itemSlices[item.Key] = list;
                        }

                        list.Add(new ReadSlice
                        {
                            BlockIndex = blockIndex,
                            OffsetInBlock = overlapStart - chunk.Start,
                            WordCount = overlapEnd - overlapStart
                        });
                    }
                }
            }
        }

        return new ReadPlan(
            blocks,
            itemSlices.ToDictionary(k => k.Key, v => (IReadOnlyList<ReadSlice>)v.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static List<AddressCluster> BuildClusters(List<Item> ordered, int mergeGapWords)
    {
        var result = new List<AddressCluster>();
        foreach (var item in ordered)
        {
            if (result.Count == 0 || item.Start > result[^1].End + mergeGapWords)
            {
                result.Add(new AddressCluster(item.Start, item.End, [item]));
            }
            else
            {
                var last = result[^1];
                last.End = Math.Max(last.End, item.End);
                last.Items.Add(item);
            }
        }

        return result;
    }

    private static List<(int Start, int End)> Split(int start, int end, int maxWords)
    {
        var list = new List<(int, int)>();
        var cursor = start;
        while (cursor < end)
        {
            var next = Math.Min(end, cursor + maxWords);
            list.Add((cursor, next));
            cursor = next;
        }

        return list;
    }

    private sealed record Item(string Key, string Area, int Start, int WordCount)
    {
        public int End => Start + WordCount;
    }

    private sealed class AddressCluster(int start, int end, List<Item> items)
    {
        public int Start { get; } = start;
        public int End { get; set; } = end;
        public List<Item> Items { get; } = items;
    }
}
