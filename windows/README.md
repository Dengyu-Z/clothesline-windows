# Clothesline：Windows 11 使用说明

这是 Tendedero 工作流的独立 Windows 实现，使用不同的名称和图标，不是官方 Windows 版本。原版 macOS 源码未改动。

## 启动

安装 Windows 11 和 .NET 8 SDK，无需单独安装 Visual Studio 工作负载。从仓库根目录运行：

```powershell
dotnet run --project .\windows\SnapshotRail\SnapshotRail.csproj
```

从本目录运行时，项目路径为 .\SnapshotRail\SnapshotRail.csproj。SDK 安装后若找不到 dotnet，请重启终端或使用 C:\Program Files\dotnet\dotnet.exe 的完整路径。

## 使用

- 鼠标移到屏幕顶部可显示晾衣绳，离开后自动隐藏。Ctrl+Alt+T 或双击托盘图标也可切换显示。
- 托盘菜单提供剪贴板监听、自动隐藏、卡片大小、保留数量、添加/停止监视文件夹和退出。
- 自动挂载复制的图片默认开启，关闭或开启后的选择会保存。Win+Shift+S 截图进入剪贴板后即可挂载；也可使用横条的“保存剪贴板”按钮。
- 默认按当前用户的标准目录寻找图片/Screenshots、视频（含 Captures）和桌面，不写死用户名。新文件事件包含子目录；启动时的初始扫描主要针对目录顶层，可为特殊保存位置添加监视目录。
- 支持图片及 .mov/.mp4/.m4v/.avi/.wmv 视频。视频使用 Windows Shell 缩略图，不支持的格式可能显示占位卡片。
- 单击图片复制图像，视频复制文件；双击用默认程序打开。长按图片打开画图，长按视频打开默认视频程序，并不执行视频剪辑。
- 可将卡片拖入其他应用或文件夹。目标接受移动操作时，卡片会从绳上取下。
- 右键可复制、打开、在资源管理器中定位、从绳子取下、移入回收站，以及批量取下。

单击图片目前复制卡片使用的缩略图，并非保证原始分辨率；需要原文件时请拖出文件或从资源管理器中打开。

## 删除与重启

外部目录卡片的角标 × 仅从绳上取下，保留原文件；Clothesline 自己保存在本地收件目录的文件，点击角标 × 会移入回收站。右键“移入回收站”会操作源文件，请留意两者区别。窗口右上角的 × 用于退出。

取下、批量取下、超过数量上限时会立即记录文件版本。重启恢复上次挂着的卡片，包括空绳状态，不会不断用更早的旧文件补位；程序关闭期间新创建的文件可以加入。同一路径被替换或修改后可作为新版本挂载。旧版本未保存过的取下操作无法追溯。

Windows 可能同时将截图写入剪贴板和截图文件夹，两条入口按解码后的像素去重，不依赖文件名、PNG 编码或 DPI。重复文件不会被删除。取下当前卡片后，再次主动复制同一图片仍可挂载。

## 本地数据与隐私

程序不上传截图或视频。开启自动挂载复制图片后，剪贴板图像会作为 PNG 保存到 %LOCALAPPDATA%\Clothesline\Screenshots，请在处理敏感图片时留意这个选项。

状态文件位于 %LOCALAPPDATA%\Clothesline：dismissed-captures.json 保存取下版本记录，rail-session.json 保存挂载路径与扫描时间；偏好位于当前用户注册表 HKCU\Software\Clothesline。状态包含本地路径，不应随源代码提交。原始图片和视频不嵌入状态文件。

## 当前限制

不包含 macOS Markup、Quick Look、Force Click、视频剪辑、全屏自动隐藏和快捷键自定义。不会接管 Win+Shift+S 或系统录屏快捷键，不会关闭系统截图提示。动画从屏幕中央飞入；多显示器与混合 DPI 环境仍需要更多人工测试。部分选项（如自动隐藏）不保证跨重启保存。

## 构建与测试

从仓库根目录运行：

```powershell
dotnet build .\windows\SnapshotRail\SnapshotRail.csproj
dotnet run --project .\windows\SnapshotRail.Tests\SnapshotRail.Tests.csproj
```

测试无需第三方测试包，覆盖状态持久化、取下行为及图片指纹，不是完整的 UI 自动化测试。

源代码许可和名称、图标限制见仓库 LICENSE。上游项目：[alejandrobujan/tendedero](https://github.com/alejandrobujan/tendedero)。
