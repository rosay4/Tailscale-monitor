# Tailnet 连接监测器

Windows 桌面程序，自动发现当前 Tailscale 账号可见的设备，监测组网连通性和可选的 TCP 服务端口。

## 运行

- Windows 10/11 x64，安装 .NET Framework 4.8。
- 安装并登录 Tailscale，确保本机服务正在运行。
- 启动 `TailnetMonitor.exe`；设备列表自动发现。可为设备选择 SSH、远程桌面、HTTP 或 HTTPS 的 TCP 端口检测。
- 关闭窗口后程序留在托盘。右键托盘图标可重新打开、暂停或退出。

状态和历史保留最近 30 分钟，退出程序后清除；设备的服务选择和暂停状态保存在当前用户的 `%LOCALAPPDATA%\TailnetMonitor\settings.json`。

## 构建

在安装了 .NET Framework 4.8 的 Windows x64 上打开 PowerShell，在项目目录执行：

```powershell
.\build.ps1 -Test
```

生成的程序位于 `dist\TailnetMonitor.exe`。构建使用 Windows 自带的 .NET Framework C# 编译器，不需要 Go 或 Node.js。需要重新生成图标时运行 `.\make-icon.ps1`。

## 上传与发布

仓库只需包含此目录的源码、脚本、图标和说明；`dist/` 为构建产物，默认不纳入版本控制。需要提供可下载程序时，可以在 GitHub Releases 中单独上传构建后的 EXE。请勿上传 Tailscale 日志、安装包或本机设置文件。
