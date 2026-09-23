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

```bat
sc create DataTrace binPath= "C:\DataTrace\DataTrace.Web.exe" start= auto
sc start DataTrace
```

监听 `http://0.0.0.0:5080`，工控机与平板可通过局域网 IP 访问。

## 数据目录

`data/config.db` 配置与用户  
`data/runtime/data_yyyyMM.db` 月库  
`data/curves/` 曲线文件  
`data/spool/` 写库失败补传  
`data/logs/` 日志  

保留策略默认 3 年，按整月删除。

## PLC 品牌

模拟器、三菱 MC 3E、西门子 S7、Modbus TCP、欧姆龙 FINS（后四者经 IoTClient）。Float 字序可配，字符串 ASCII。
