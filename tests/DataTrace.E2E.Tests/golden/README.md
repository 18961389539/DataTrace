# 视觉基线审核与重录

常规 CI 只运行 compare：必须存在每张 PNG 和 environment.json，并且环境指纹必须与运行时完全一致，之后才执行像素比对。缺文件或浏览器/操作系统不同都会失败。不得在同一次 CI 工作流先运行 update 再运行 compare，这种流程只证明截图能和自己相等。

Playwright NuGet 版本在 Directory.Packages.props 固定为 1.60.0；CI 通过该版本生成的 playwright.ps1 install chromium 安装对应 Chromium。指纹包含 Windows 平台、进程架构和 Chromium 精确版本，不包含 Windows 补丁/build 字符串。截图视口固定为 1280×800，Playwright 默认设备缩放为 1；不要改字体、浏览器缩放或系统显示缩放。Windows 字体渲染差异可能影响像素，因此基线仍依赖兼容的 Windows runner 渲染环境。

## 审核重录

1. 用 pwsh tests/DataTrace.E2E.Tests/record-golden.ps1 -Mode update 在固定版本环境录制。此脚本每次只运行一种模式，不要在 PR 回归工作流设置 update。
2. 检查 golden/*.png 每张新图，并确认 environment.json 的 OS 与 Chromium 指纹符合固定环境。
3. 将 PNG 和 environment.json 一起提交。随后由普通 CI 的 compare 工作流验证。

更新前保留旧基线 diff 以供审阅。环境指纹变化时，不要跳过 compare；先重新录制并审核整套基线。
