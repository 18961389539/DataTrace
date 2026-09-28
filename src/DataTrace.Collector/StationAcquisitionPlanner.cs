using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Planning;

namespace DataTrace.Collector;

/// <summary>
/// 一次采集要读的字块。配置版本不变时可以复用，不必每次触发都重新解析地址。
/// </summary>
public sealed class StationAcquisitionPlan
{
    public required ReadPlan Plan { get; init; }
}

public static class StationAcquisitionPlanner
{
    public static StationAcquisitionPlan Compile(Station station, PlcConnection connection, IPlcDriver driver)
    {
        if (!driver.TryParseAddress(station.PalletCodeAddress, out var palletAddress))
        {
            throw new PlcDriverException($"托盘码地址非法: {station.PalletCodeAddress}");
        }

        RejectBitAddress(palletAddress, $"托盘码地址 {station.PalletCodeAddress}");

        var requests = new List<AddressReadRequest>
        {
            new()
            {
                Key = "pallet",
                Address = palletAddress,
                WordCount = ValueCodec.WordCountOf(station.PalletCodeDataType, station.PalletCodeLength)
            }
        };

        foreach (var pos in station.Positions.Where(p => !string.IsNullOrWhiteSpace(p.OccupiedAddress)))
        {
            if (!driver.TryParseAddress(pos.OccupiedAddress!, out var occ))
            {
                throw new PlcDriverException($"有料地址非法: {pos.OccupiedAddress}");
            }

            RejectBitAddress(occ, $"有料地址 {pos.OccupiedAddress}");
            requests.Add(new AddressReadRequest { Key = $"occ_{pos.Index}", Address = occ, WordCount = 1 });
        }

        foreach (var tag in station.Tags.Where(t => t.Enabled && t.Source == TagDataSource.Plc))
        {
            if (!driver.TryParseAddress(tag.Address, out var addr))
            {
                throw new PlcDriverException($"点位地址非法: {tag.Address}");
            }

            RejectBitAddress(addr, $"点位 {tag.Name} 的地址 {tag.Address}");
            requests.Add(new AddressReadRequest
            {
                Key = $"tag_{tag.Id}",
                Address = addr,
                WordCount = ValueCodec.WordCountOf(tag.DataType, tag.Length)
            });
        }

        foreach (var curve in station.Curves.Where(c => c.Enabled && c.PointCount > 0))
        {
            foreach (var series in curve.Series)
            {
                if (!driver.TryParseAddress(series.StartAddress, out var addr))
                {
                    throw new PlcDriverException($"曲线地址非法: {series.StartAddress}");
                }

                RejectBitAddress(addr, $"曲线 {curve.Code} 的 {series.Role} 起始地址 {series.StartAddress}");
                var typeWords = ValueCodec.WordCountOf(series.DataType);
                var stride = Math.Max(typeWords, series.StrideWords);
                var wordCount = (curve.PointCount - 1) * stride + typeWords;
                requests.Add(new AddressReadRequest
                {
                    Key = $"curve_{curve.Id}_{series.Id}",
                    Address = addr,
                    WordCount = wordCount
                });
            }
        }

        return new StationAcquisitionPlan
        {
            Plan = ReadPlanBuilder.Build(requests, driver.Capabilities.MaxWordsPerRead, connection.MergeGapWords)
        };
    }

    private static void RejectBitAddress(PlcAddress address, string what)
    {
        if (address.IsBit)
        {
            throw new PlcDriverException($"{what} 是位地址，采集不支持位地址（请改用字地址，非 0 即 true）");
        }
    }
}

public static class StationBlockReader
{
    public static async Task<ushort[][]> ReadAsync(
        StationAcquisitionPlan plan,
        Plc.Queue.PlcRequestQueue queue,
        CancellationToken cancellationToken)
    {
        var buffers = new ushort[plan.Plan.Blocks.Count][];
        for (var i = 0; i < plan.Plan.Blocks.Count; i++)
        {
            var block = plan.Plan.Blocks[i];
            buffers[i] = await queue.ReadWordsAsync(block.StartAddress(), block.WordCount, cancellationToken)
                .ConfigureAwait(false);
        }

        return buffers;
    }
}

public static class StationSampleDecoder
{
    public static string PalletCode(Station station, PlcConnection connection, ReadPlan plan, ushort[][] buffers)
    {
        var palletWords = plan.GetWords("pallet", buffers);
        return station.PalletCodeDataType == PlcDataType.String
            ? ValueCodec.DecodeAscii(palletWords, station.PalletCodeLength, connection.StringHighByteFirst)
            : ((int)ValueCodec.DecodeNumeric(palletWords, station.PalletCodeDataType, connection.FloatWordOrder, 1, 0)).ToString();
    }
}
