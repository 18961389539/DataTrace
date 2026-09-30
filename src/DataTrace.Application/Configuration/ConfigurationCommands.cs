// 配置写命令。这里曾经是一个近 600 行的单文件，已按领域拆到同目录下：
//
//   ConfigurationCommands.Plc.cs         PLC 连接与心跳
//   ConfigurationCommands.Quality.cs     点位、曲线、型号（含 SavedRecipe）
//   ConfigurationCommands.Settings.cs    系统设置
//   ConfigurationCommands.Extensions.cs  仓储上的便捷写入扩展与工站规范化
//
// 命名空间未变（DataTrace.Application.Configuration），调用方不需要改 using。
