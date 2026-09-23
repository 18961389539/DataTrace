# DataTrace 产线数据采集系统

.NET 8 Blazor Server 应用：多品牌 PLC 采集、托盘会话、曲线外置、按月 SQLite 分库、Web 查询与报表。

## 运行

```bat
cd src\DataTrace.Web
dotnet run
```

浏览器打开 http://localhost:5080

默认账号：

| 用户 | 密码 | 角色 |
|---|---|---|
| admin | Admin@123 | 管理员 |
| engineer | Engineer@123 | 工程师 |
| operator | Operator@123 | 操作员 |
| viewer | Viewer@123 | 访客 |

首次启动会写入演示产线（3 工站、每托盘一件产品、位移压力曲线）并连接**模拟 PLC**。工程师在「PLC仿真」页即可走通采集-存储-查询。

## Windows 服务

先发布，再把整个目录拷到工控机（例如 `C:\DataTrace`）：

```bat
dotnet publish src\DataTrace.Web -c Release -o publish
sc create DataTrace binPath= "C:\DataTrace\DataTrace.Web.exe" start= auto
sc start DataTrace
```

监听 `http://0.0.0.0:5080`，工控机与平板可通过局域网 IP 访问。

服务的工作目录是 `C:\Windows\System32`，不是安装目录；应用的**内容根固定取 exe 所在目录**，
所以 `appsettings.json`、`wwwroot` 都能正常读到（若按工作目录找，端口会悄悄退回 5000、
日志级别与 `DataRoot` 一起失效）。因此 `sc create` 不需要额外指定工作目录。

换端口时别只改 `ASPNETCORE_URLS`：`appsettings.json` 里的 `Kestrel:Endpoints:Http:Url`
优先级更高，会**静默**把它覆盖掉（现象是服务照样起在 5080，日志里没有任何提示）。
改监听地址要改配置文件，或用同名配置项覆盖：

```bat
set Kestrel__Endpoints__Http__Url=http://0.0.0.0:5100
```

## 数据目录

`data/config.db` 配置与用户  
`data/runtime/data_yyyyMM.db` 月库  
`data/curves/` 曲线文件  
`data/spool/` 写库失败补传  
`data/logs/` 日志  

`DataRoot` 可以指到别的盘（相对路径按 exe 目录解析），**日志也一起跟着走**——
装在 `C:\Program Files` 下时服务对安装目录通常没有写权限。

保留策略默认 3 年，按整月删除。

## PLC 品牌

模拟器、三菱 MC 3E、西门子 S7、Modbus TCP、欧姆龙 FINS（后四者经 IoTClient）。Float 字序可配，字符串 ASCII。
