using System.Text.Json;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>一次文件读取的产物：归档引用 + 各文件源点位取到的值（取不到的点位不在字典里）。</summary>
public sealed record FileSourceRead(
    IReadOnlyDictionary<int, SourceTagValue> Values,
    string ArchivePath,
    long ArchiveSize,
    uint ArchiveCrc,
    short ErrorCode = 0,
    string? Error = null)
{
    public static FileSourceRead Failure(short errorCode, string error)
        => new(new Dictionary<int, SourceTagValue>(), "", 0, 0, errorCode, error);
}

public sealed record SourceTagValue(double? Numeric, string? Text);

public sealed class FileSourceReader
{
    private readonly ICollectArchiveStore _archives;
    private readonly ILogger _logger;

    public FileSourceReader(ICollectArchiveStore archives, ILogger logger)
    {
        _archives = archives;
        _logger = logger;
    }

    /// <summary>
    /// 文件源点位的那一次读取：读文件 → 归档 → 按字段名解析出这些点位的值。
    /// </summary>
    public async Task<FileSourceRead> ReadAsync(
        Station station,
        DateTime triggerTime,
        string palletCode,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(station.DataFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工站 {Station} 读取数据文件失败：{Path}", station.Code, station.DataFilePath);
            return FileSourceRead.Failure(Domain.Constants.ResultCodes.FileSourceFailed, $"数据文件读取失败：{ex.Message}");
        }

        string archivePath;
        long archiveSize;
        uint archiveCrc;
        try
        {
            var written = await _archives
                .WriteAsync(triggerTime, palletCode, station.Id, bytes, cancellationToken)
                .ConfigureAwait(false);
            archivePath = written.RelativePath;
            archiveSize = written.FileSize;
            archiveCrc = written.Crc32;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工站 {Station} 原始数据归档失败", station.Code);
            return FileSourceRead.Failure(Domain.Constants.ResultCodes.ArchiveFailed, $"原始数据归档失败：{ex.Message}");
        }

        try
        {
            if (station.DataFileFormat == DataFileFormat.Csv)
            {
                return ReadCsv(station, bytes, archivePath, archiveSize, archiveCrc);
            }

            using var document = JsonDocument.Parse(bytes);
            var values = new Dictionary<int, SourceTagValue>();
            foreach (var tag in station.Tags.Where(t => t.Enabled && t.Source == TagDataSource.JsonFile))
            {
                if (tag.DataType == PlcDataType.String)
                {
                    if (JsonFieldReader.TryReadText(document.RootElement, tag.Address, out var text))
                    {
                        values[tag.Id] = new SourceTagValue(Numeric: null, Text: text);
                    }
                }
                else if (JsonFieldReader.TryReadNumeric(document.RootElement, tag.Address, out var numeric))
                {
                    values[tag.Id] = new SourceTagValue(Numeric: numeric, Text: null);
                }

                if (!values.ContainsKey(tag.Id))
                {
                    _logger.LogWarning(
                        "工站 {Station} 的点位 {Tag} 在数据文件里取不到字段 {Field}",
                        station.Code, tag.Name, tag.Address);
                }
            }

            return new FileSourceRead(values, archivePath, archiveSize, archiveCrc);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "工站 {Station} 的数据文件不是合法 JSON：{Path}", station.Code, station.DataFilePath);
            return FileSourceRead.Failure(
                Domain.Constants.ResultCodes.FileSourceFailed, $"数据文件不是合法 JSON：{ex.Message}（已归档：{archivePath}）");
        }
    }

    private FileSourceRead ReadCsv(
        Station station,
        byte[] bytes,
        string archivePath,
        long archiveSize,
        uint archiveCrc)
    {
        if (!CsvFieldReader.TryParse(bytes, out var row, out var error))
        {
            _logger.LogError("工站 {Station} 的数据文件不是合法 CSV：{Path} {Error}", station.Code, station.DataFilePath, error);
            return FileSourceRead.Failure(
                Domain.Constants.ResultCodes.FileSourceFailed, $"数据文件不是合法 CSV：{error}（已归档：{archivePath}）");
        }

        var values = new Dictionary<int, SourceTagValue>();
        foreach (var tag in station.Tags.Where(t => t.Enabled && t.Source == TagDataSource.JsonFile))
        {
            if (tag.DataType == PlcDataType.String)
            {
                if (CsvFieldReader.TryReadText(row, tag.Address, out var text))
                {
                    values[tag.Id] = new SourceTagValue(Numeric: null, Text: text);
                }
            }
            else if (CsvFieldReader.TryReadNumeric(row, tag.Address, out var numeric))
            {
                values[tag.Id] = new SourceTagValue(Numeric: numeric, Text: null);
            }

            if (!values.ContainsKey(tag.Id))
            {
                _logger.LogWarning(
                    "工站 {Station} 的点位 {Tag} 在数据文件里取不到列 {Field}",
                    station.Code, tag.Name, tag.Address);
            }
        }

        return new FileSourceRead(values, archivePath, archiveSize, archiveCrc);
    }
}
