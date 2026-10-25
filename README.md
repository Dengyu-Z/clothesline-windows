# Clothesline for Windows

将截图和录屏挂在屏幕顶部的晾衣绳上。面向 Windows 11，使用 .NET 8 / WPF 实现。

这是基于 [Alejandro Buján 的 Tendedero](https://github.com/alejandrobujan/tendedero) 的独立 Windows 移植项目，**不是原作者发布的 Windows 版本**。采用独立名称和图标；上游 macOS 源码保留在仓库中，Windows 实现在 [windows](windows/README.md) 目录。

## 安装与运行

需要 Windows 11 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。当前提供源码构建，尚未提供安装包。

在 PowerShell 中运行：

```powershell
git clone https://github.com/Dengyu-Z/clothesline-windows.git
cd clothesline-windows
dotnet run --project .\windows\SnapshotRail\SnapshotRail.csproj
```

安装 SDK 后若提示找不到 dotnet，请重启终端；也可运行：

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" run --project .\windows\SnapshotRail\SnapshotRail.csproj
```

## 已实现

- 透明晾衣绳、照片夹、卡片摆动和新图片飞入动画。
- 监听截图、录屏目录及用户添加的文件夹；自动挂载复制的图片（默认开启，可记住选择）。
- 图片和视频卡片；视频缩略图取决于 Windows 的格式与解码支持。
- 单击复制、双击打开、长按图片打开画图、拖出分享、右键管理卡片。
- 鼠标移到屏幕顶部显示，离开后自动隐藏；Ctrl+Alt+T 切换显示，托盘菜单提供更多选项。
- 挂载状态恢复、取下记录持久化，以及剪贴板和文件目录之间的图片去重。

## 与原版的差异

不包含 macOS Markup、Quick Look、Force Click、视频时间线剪辑、全屏自动隐藏或自定义快捷键。长按视频仅使用默认程序打开。不会接管或关闭 Windows 截图工具，因此系统截图提示仍可能出现。飞入动画从屏幕中央开始，不是从实际框选区域开始。

详细操作、文件行为、隐私与本地构建说明见 [Windows 使用说明](windows/README.md)。

## 开发与测试

```powershell
dotnet build .\windows\SnapshotRail\SnapshotRail.csproj
dotnet run --project .\windows\SnapshotRail.Tests\SnapshotRail.Tests.csproj
```

测试覆盖状态保存、取下记录和图片指纹；不代替动画、多显示器和系统交互的人工验证。

## 许可证与致谢

源代码使用 [MIT 许可证](LICENSE)，保留原作者 Alejandro Buján 的版权声明。上游的 Tendedero 名称、图标与文档图片不在 MIT 授权范围内；它们不作为本 Windows 版的品牌素材。Clothesline 使用不同的名称与图标，不代表原作者认可或背书。原版介绍和 macOS 安装方式请查看[上游仓库](https://github.com/alejandrobujan/tendedero)。
