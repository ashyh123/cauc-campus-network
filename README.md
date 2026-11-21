# CAUC 校园网 · macOS

一个用于中国民航大学校园网登录和 IPv6 检测的桌面应用。主界面使用 AppKit 系统控件和半透明材质，认证网页使用 WebKit。适用于 macOS 13 及以上的 Apple 芯片和 Intel Mac。

## 使用

1. 解压 `CAUC-Campus-Network-1.1.0-macOS.zip`，打开其中的 `CAUC Campus Network.app`。这个版本未做 Apple Developer ID 签名或公证。如果系统拦截直接运行，可以在 Mac 上按下方步骤从源码构建并做本机签名。
2. 先通过 macOS 连上 `CAUC-WIFI`、`CAUC-DORM` 或有线校园网。在应用中选择对应的入口，点击「开始认证」。已有双栈公网连通时，应用会直接显示结果。
3. 在独立的校园网认证窗口输入账号密码并提交。macOS 如果询问是否允许访问本地网络，需要允许才能访问校内认证页。窗口标题显示当前页面地址，应用使用临时 WebKit 会话。
4. 应用会在登录期间持续检查 IPv4 和 IPv6。若 IPv4 已通而 IPv6 仍待确认，会尝试访问中科大的 IPv6 测试页并复检。若复检仍有问题，可以查看主窗口里的地址和默认路由，以及从应用打开 Wi-Fi 设置。

## 实现

- IPv4 和 IPv6 公网探测分别使用 `tcp4` 和 `tcp6`，直接连接 HTTPS 站点。只有 HTTP 2xx 记为成功，重定向计为探测未通过。中科大 HTTP 页面用于尝试触发，复检仍要求 HTTPS 探测通过。
- 系统诊断枚举活动接口上的全局及链路本地 IPv6 地址，并用 `/sbin/route` 读取 IPv6 默认路由。如果默认路由落在 `utun`，可在诊断里检查是否经过 VPN 或网络扩展。
- 此版在学校认证网页手动输入账号密码，凭据由该网页处理。应用读取系统状态，网卡设置通过 macOS 系统界面调整。

## 在 Mac 上构建

需要 Go 1.23 或更新版本。解压源码后，在项目目录中执行以下命令。

```sh
CGO_ENABLED=0 go test ./...
sh ./build-macos.sh
open 'dist/CAUC Campus Network.app'
```

脚本为当前 Mac 架构构建应用，并执行本机临时签名。也可直接输出探测结果，用于排查网络问题。

```sh
'dist/CAUC Campus Network.app/Contents/MacOS/CAUCCampusNetwork' --diagnose
```

学校的认证入口来自[中国民航大学信息网络服务指南](https://www.cauc.edu.cn/xxzx/info/1013/1358.htm)。Objective-C 桥接使用 [PureGo](https://github.com/ebitengine/purego)。
