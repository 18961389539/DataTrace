namespace DataTrace.Infrastructure.Backup;

/// <summary>安装数据根路径（config.db / runtime / curves / spool）。</summary>
public sealed class DataRootPaths
{
    public DataRootPaths(string root)
    {
        Root = Path.GetFullPath(root);
        ConfigDbPath = Path.Combine(Root, "config.db");
        RuntimeDirectory = Path.Combine(Root, "runtime");
        CurvesDirectory = Path.Combine(Root, "curves");
        SpoolDirectory = Path.Combine(Root, "spool");
    }

    public string Root { get; }
    public string ConfigDbPath { get; }
    public string RuntimeDirectory { get; }
    public string CurvesDirectory { get; }
    public string SpoolDirectory { get; }
}
