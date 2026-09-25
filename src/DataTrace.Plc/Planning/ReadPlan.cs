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

    /// <summary>块起始位置，单位由 <see cref="OffsetUnit"/> 决定。</summary>
    public required int StartOffset { get; init; }

    /// <summary>块长度，恒为字数。</summary>
    public required int WordCount { get; init; }

    public OffsetUnit OffsetUnit { get; init; } = OffsetUnit.Word;

    /// <summary>
    /// 块起始地址：给驱动用于拼请求。
    /// </summary>
    /// <remarks>
    /// 字单位直接拼"区名+偏移"（D100）；字节单位要按 IoTClient 的写法给偏移 ——
    /// DB 区必须是 DB108.4 这种带点的形式，写成 DB1084 会被解析成"DB 块号 1084、偏移 84 字节"，
    /// 读到的是完全不相干的地址。
    /// </remarks>
    public PlcAddress StartAddress()
    {
        var text = OffsetUnit == OffsetUnit.Byte && Area.StartsWith("DB", StringComparison.OrdinalIgnoreCase)
            ? $"{Area}.{StartOffset}"
            : $"{Area}{StartOffset}";
        return new PlcAddress(Area, StartOffset, -1, AddressKind.Word, text, OffsetUnit);
    }
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
            .Select(r => new Item(r.Key, r.Address.Area, r.Address.Offset, r.WordCount, r.Address.OffsetUnit))
            .ToList();

        var blocks = new List<ReadBlock>();
        var itemSlices = new Dictionary<string, List<ReadSlice>>(StringComparer.OrdinalIgnoreCase);

        foreach (var unitGroup in wordRequests.GroupBy(x => x.Unit))
        {
            foreach (var areaGroup in unitGroup.GroupBy(x => x.Area, StringComparer.OrdinalIgnoreCase))
            {
                // 字节单位（西门子）与字单位不能混算：偏移的步长不同，簇和切片的边界都会错位。
                var ordered = areaGroup.OrderBy(x => x.Start).ToList();
                var clusters = unitGroup.Key == OffsetUnit.Byte
                    // 字节区的偏移是字节数，与"字数间隙"没有可比性，因此一个请求一块，不做合簇。
                    ? ordered.Select(item => new AddressCluster(item.Start, item.End, [item])).ToList()
                    : BuildClusters(ordered, mergeGapWords);

                var unitsPerWord = unitGroup.Key == OffsetUnit.Byte ? 2 : 1;
                var maxUnits = maxWordsPerRead * unitsPerWord;

                foreach (var cluster in clusters)
                {
                    var chunks = Split(cluster.Start, cluster.End, maxUnits);
                    foreach (var chunk in chunks)
                    {
                        var blockIndex = blocks.Count;
                        blocks.Add(new ReadBlock
                        {
                            Area = areaGroup.Key,
                            StartOffset = chunk.Start,
                            WordCount = (chunk.End - chunk.Start) / unitsPerWord,
                            OffsetUnit = unitGroup.Key
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
                                OffsetInBlock = (overlapStart - chunk.Start) / unitsPerWord,
                                WordCount = (overlapEnd - overlapStart) / unitsPerWord
                            });
                        }
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

    private static List<(int Start, int End)> Split(int start, int end, int maxUnits)
    {
        var list = new List<(int, int)>();
        var cursor = start;
        while (cursor < end)
        {
            var next = Math.Min(end, cursor + maxUnits);
            list.Add((cursor, next));
            cursor = next;
        }

        return list;
    }

    private sealed record Item(string Key, string Area, int Start, int WordCount, OffsetUnit Unit)
    {
        /// <summary>结束位置，单位与 <see cref="Start"/> 相同（字或字节）。</summary>
        public int End => Start + WordCount * (Unit == OffsetUnit.Byte ? 2 : 1);
    }

    private sealed class AddressCluster(int start, int end, List<Item> items)
    {
        public int Start { get; } = start;
        public int End { get; set; } = end;
        public List<Item> Items { get; } = items;
    }
}