# CAUC 校园网助手 (v2.0.0)

一个轻量、原生的中国民航大学（CAUC）校园网状态与一键认证工具，同时提供 **macOS**（Swift 6 / AppKit / SwiftUI / WebKit）与 **Windows**（C# / .NET 8 / WPF / WebView2）两个原生版本。

## 功能特性（macOS & Windows）

- **原生托盘 / 菜单栏浮窗**：
  - **macOS**：常驻屏幕右上角菜单栏（`LSUIElement` 无 Dock 图标），点击展开 `312pt` 紧凑毛玻璃浮窗（macOS 26+ 原生 `NSGlassEffectView`，macOS 13–25 `NSVisualEffectView`）。
  - **Windows**：常驻系统托盘，实时同步任务栏图标状态与深浅色主题，点击弹出 `312pt` 紧凑浮窗（Windows 11 22H2+ 原生 DWM Acrylic 亚克力毛玻璃，Windows 10 自动回退 Fluent 实色卡片），右键菜单支持快捷操作。
- **IPv4 / IPv6 公网可达严格检测**：分别基于 IPv4 与 IPv6 协议栈发起原生 TLS/HTTP 探测，仅 2xx 响应判定可达，拒绝校园网认证重定向（302）误判。
- **校园网额度实时展示与 VPN/TUN 穿透优化**：直连 Dr.COM 网关 `http://192.168.100.200/drcom/chkstatus`（自动绕过系统代理），展示学号、剩余流量、账户余额及累计 / IPv6 免计流量，并在网关暂不可达时回退展示 7 天内本地缓存。
  - **物理网卡并发竞速与增量解析**：开启 VPN/TUN 时直接枚举物理无线/有线网卡并发直连网关（macOS 通过 `NWParameters.requiredInterface`，Windows 通过 `IP_UNICAST_IF` + 源 IP 绑定），并记忆上次成功网卡；收到完整响应头与 JSON 体即刻解析返回，无需等待 TCP 连接关闭。
  - **同路径智能复用与冷却退避**：同一网络路径下对成功额度（60 秒 TTL）与不可达状态（60 秒冷却退避）进行复用，未认证状态（`.offline`）不缓存；支持点击刷新、认证流程及网络切换时强制重检。
- **网络切换与休眠唤醒自动监听**：监听 Wi-Fi / 有线 / VPN（TUN）网络路径变化（1.5 秒防抖后自动重检），并在系统休眠唤醒后自动刷新状态。
- **一键无痕网页认证与 IPv6 自动激活**：使用独立无痕浏览器会话（macOS 非持久化 `WKWebsiteDataStore` / Windows WebView2 InPrivate 模式）打开 `http://192.168.100.200/`；若 IPv4 已通而 IPv6 待确认，自动后台访问 `http://test6.ustc.edu.cn/` 并启用中科大 IPv6 探测复检，双栈连通后自动关闭认证窗口；手动关闭窗口后立即停止后台认证轮询。
- **开机自启**：macOS 通过 `SMAppService.mainApp`，Windows 通过 `HKCU\...\Run` 注册表项一键管理。

---

## Windows 版下载与使用（Windows 10 / 11 x64）

1. 前往本仓库的 [Releases](https://github.com/ashyh123/cauc-campus-network/releases) 下载 `CAUC-Campus-Network-2.0.0-Windows-x64.zip` 并解压。
2. 直接双击运行 `CAUCCampusNetwork.exe`（自包含单文件便携版，无需单独安装 .NET 运行时；内置认证页依赖 Windows 10/11 系统自带的 Microsoft Edge WebView2）。
3. 连上 CAUC 校园网后，点击「开始认证」完成登录即可自动检测并激活双栈公网连接。

### Windows 从源码测试与构建

需要 Windows 10/11 与 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。在仓库根目录执行：

```powershell
dotnet test windows/CAUCCampusNetwork.sln -c Release
.\windows\build-windows.ps1 -Version 2.0.0
```

构建产物将输出至 `windows/dist/CAUC-Campus-Network-2.0.0-Windows-x64.zip`（含 `SHA256SUMS.txt`）。仓库同时配置了 GitHub Actions（`.github/workflows/windows-release.yml`），在每次推送与发布 Tag 时会自动完成测试与 Windows x64 构建。

---

## macOS 版从源码测试与构建

需要 macOS 13.0+ 及 Swift 6 工具链（Xcode 或 Command Line Tools）。在仓库根目录执行：

```sh
swift test --disable-xctest --enable-swift-testing \
  -Xswiftc -load-plugin-library \
  -Xswiftc /Library/Developer/CommandLineTools/usr/lib/swift/host/plugins/testing/libTestingMacros.dylib
sh ./build-macos.sh
open 'dist/CAUC Campus Network.app'
```

构建脚本会生成 `dist/CAUC Campus Network.app` 并执行本机临时签名（`codesign --force --sign -`）。如果已安装完整 Xcode，直接运行 `swift test --disable-xctest --enable-swift-testing` 即可（Command Line Tools 环境下需显式传入上方的 `libTestingMacros.dylib` 插件参数）。如需使用 `SMAppService` 开机自启功能，请将 `CAUC Campus Network.app` 安装到 `/Applications` 目录后运行：

```sh
rm -rf '/Applications/CAUC Campus Network.app'
ditto 'dist/CAUC Campus Network.app' '/Applications/CAUC Campus Network.app'
open '/Applications/CAUC Campus Network.app'
```

---

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。

