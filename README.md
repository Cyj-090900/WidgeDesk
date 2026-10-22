# WidgeDesk

Windows 桌面美化工具：玻璃 Dock、启动台和统一设置中心，x64。

支持 Windows 10 2004（Build 19041）及以上。Windows 11 会启用系统圆角与背景材质，
Windows 10 上这些特性按能力探测并自动回退到自绘，功能保持一致。

## 许可证

原创代码来自 GlassDesk，采用 Apache License 2.0，允许修改、商用及闭源衍生产品，须遵守版权、许可、修改声明等要求。完整条款见 LICENSE；第三方代码与资源保留自身许可，详见 NOTICE 和 docs/third-party。

GlassDesk 不打包用户导入应用的图标，项目许可不授予第三方商标权利。

## 环境

- Windows 10 2004（Build 19041）及以上，x64。
  源项目目标 SDK 为 Windows 10.0.26100.0（这只影响编译时能用哪些 API 投影，不构成最低系统要求）；
  运行兼容下限由 `Directory.Build.targets` 的 `SupportedOSPlatformVersion` 决定。
  选 19041 是因为自排除用的 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 从 Windows 10 2004 起才有。
- Windows 10 上请安装全部系统更新，并且**构建时必须保持 `CETCompat=false`**（见下文「Windows 10 说明」）。
- .NET 10 SDK，global.json 固定工具链版本 10.0.401 并允许 latestFeature 滚动策略。
- 高级实时玻璃使用部分非公开 DWM 接口，并按能力提供兼容回退。各版本的现场验证不代表所有 Windows Build、显卡、HDR、多屏环境均已验证。

## 编译和运行

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet run --project src/GlassDesk.App/GlassDesk.App.csproj -c Release
```

程序数据位于实际运行目录旁的 Data。源码构建版也使用独立的输出目录数据。正式版按用户会话维持单实例，启动开发版前应先从托盘退出已安装版。

```powershell
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release
```

部分 Windows/WPF 验收测试会创建窗口并检查真实桌面服务，应在可交互的 Windows 会话中运行。

## 从源码生成便携版

```powershell
$version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
$version = @($version | Where-Object { $_ })[0]
dotnet publish src/GlassDesk.App/GlassDesk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true "-p:InformationalVersion=$version" -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/publish
Copy-Item LICENSE artifacts/publish/LICENSE.txt
Copy-Item NOTICE artifacts/publish/NOTICE.txt
Copy-Item docs/third-party artifacts/publish/third-party -Recurse
```

artifacts/publish 中的 GlassDesk.exe 可直接运行。不要把自己的 Data 目录放入分发包。框架与第三方资源的许可文件应随分发包保留。

## 从源码生成安装包

另安装 **Inno Setup 7**（脚本用了 `SetupArchitecture`，这是 IS7 才有的指令；IS6 会报
「Unrecognized [Setup] section directive」而中止），并将 ISCC.exe 所在目录加入当前终端的
PATH。先执行上面的便携版发布命令，再执行：

```powershell
$env:GLASSDESK_PACKAGE_STAGE = (Resolve-Path artifacts/publish).Path
ISCC /DAppIdValue=GlassDesk.Independent "/DProductVersion=$version" "/DFileVersion=$version.0" "/DPackageBaseName=GlassDesk-$version-win-x64" /Oartifacts packaging/GlassDesk-Capture.iss
```

安装包支持选择目录。安装更新保留设置、应用库、布局和图标缓存，卸载也保留 Data。此源码打包流程不要求私有仓库的旧安装载荷。

产出的安装包名为 `GlassDesk-<版本>-win-x64-Setup.exe`。**这个文件名不能改**：设置中心的
「检查更新」按这个规则在本仓库的 Releases 中查找资产（见 `UpdateClient`），改名会导致
更新器报告「更新信息无效」。Release 标签也必须是纯版本号（如 `v1.3.1`）。

## 修改着色器

TransitionGlass.hlsl 和编译后的 TransitionGlass.ps 均在 src/GlassDesk.UI/Effects 中。修改 HLSL 后执行 build/compile-transition-shader.ps1，再重新编译。

历史 GlassDesk.UiAcceptance 工程的目标框架低于正式项目，直接构建整个旧解决方案会报 NU1201。请按上述 App 项目构建；此问题不影响正式程序的源码构建。本次保留历史测试源码，未改写历史产品代码。

## Windows 10 说明

### 必须关闭 CET 标记（`CETCompat=false`）

.NET 8 起，生成的 apphost（也就是发布出来的那个 `.exe`）默认带 `CET_COMPAT` 标记。
如果 Windows 10 的 `ntdll.dll` 早于 2023 年的 CET 内核修复，coreclr 会在启动阶段
（`Thread::StaticInitialize`）直接 fail-fast：

```
Fatal error.
Your Windows doesn't fully support CET. Please install all available Windows updates.
```

这**不是代码问题，也没法在程序里捕获**：检查发生在任何托管代码运行之前，而且 CET 状态
在 `.exe` 启动瞬间就已确定，运行时无法更改。Windows 10 22H2 上该修复最先随
KB5044273 发布（`ntdll` 10.0.19041.5007）；早于它的机器只要镜像带 CET 标记就会崩。

所以本项目在 `Directory.Build.props` 里设了 `<CETCompat>false</CETCompat>`。

代价说明白：在支持 CET 的机器上，本进程会放弃这项加固。对一个桌面美化工具来说，
「在 Windows 10 上能启动」比这项硬化更重要，但这是一个明确的取舍，不是 SDK 默认值。

`dotnet.exe` 宿主不受影响——它本身不带 CET 标记，这也正是同样的代码 `dotnet run` 能跑、
而发布的 `.exe` 会崩的原因。

### 编译时需要打的补丁（仅限未打 CET 修复的机器）

如果开发机本身就是这些老构建，`dotnet build` 会失败，因为**编译器进程** `csc.exe`
同样带 CET 标记。报错出现在 `Microsoft.CSharp.Core.targets`，内容与上面一致。

两条可行路径：

1. **安装全部 Windows 更新**（推荐）：`ntdll` 到 10.0.19041.5007 及以上后即可直接构建。
2. **让 MSBuild 用不带标记的托管编译器**：`dotnet exec csc.dll` 走的是 `dotnet.exe`
   宿主，不经过 apphost 的 CET 检查，所以能正常编译。做法是复制 SDK 的
   `Roslyn/bincore`，把副本里 `csc.exe` / `VBCSCompiler.exe` 的 PE 调试目录项
   （类型 20，`IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS`）第 0 位清掉，再让构建指向副本：

   ```powershell
   dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release `
     -m:1 -nodeReuse:false -p:UseSharedCompilation=false `
     -p:CscToolPath=<补丁后的 Roslyn 副本路径> -p:CscToolExe=csc.exe
   ```

   不要改动 `Program Files` 里的 SDK 原文件；补丁只作用在副本上。

### Windows 10 上的玻璃效果

实时玻璃靠「排除自身 + 采样桌面」，Windows 10 上走的是**有文档记载**的 DWM
缩略图路径（`DocumentedThumbnails`），非公开的共享视觉接口只在 Windows 11 上启用。
这只是内部取样方式不同，外观与交互一致；取样比例（每 4 像素取 1）等实现细节相同。

Windows 10 没有 `DWMWA_WINDOW_CORNER_PREFERENCE`（系统圆角）和
`DWMWA_SYSTEMBACKDROP_TYPE`（Mica/Acrylic 材质），这两项会自动回退到自绘圆角与
自绘半透明面板。`WindowAcrylic` 的未公开亚克力接口是**按构建号白名单**开放的，
Windows 10 不在名单内，因此走自绘面板——这是设计内的降级，不影响功能。
