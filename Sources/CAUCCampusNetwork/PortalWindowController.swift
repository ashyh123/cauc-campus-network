import AppKit
import CAUCCampusCore
import WebKit

@MainActor
final class PortalWindowController: NSObject, WKNavigationDelegate, NSWindowDelegate {
    var onUserClosedPortal: (() -> Void)?

    private lazy var dataStore: WKWebsiteDataStore = .nonPersistent()
    private var window: NSWindow?
    private var portalWebView: WKWebView?
    private var triggerWebView: WKWebView?
    private var isClosingProgrammatically = false

    func openPortal() {
        let portalWindow = ensurePortalWindow()
        portalWindow.title = "校园网认证 · 192.168.100.200"
        portalWebView?.load(URLRequest(url: CampusEndpoints.portalURL))

        if #available(macOS 14.0, *) {
            NSApp.activate()
        } else {
            NSApp.activate(ignoringOtherApps: true)
        }
        portalWindow.makeKeyAndOrderFront(nil)
    }

    func closePortal() {
        isClosingProgrammatically = true
        defer { isClosingProgrammatically = false }
        portalWebView?.stopLoading()
        window?.orderOut(nil)
    }

    func triggerIPv6() {
        if triggerWebView == nil {
            let config = WKWebViewConfiguration()
            config.websiteDataStore = dataStore
            triggerWebView = WKWebView(frame: NSRect(x: 0, y: 0, width: 1, height: 1), configuration: config)
        }
        triggerWebView?.load(URLRequest(url: CampusEndpoints.ipv6TriggerURL))
    }

    func stopTrigger() {
        triggerWebView?.stopLoading()
    }

    private func ensurePortalWindow() -> NSWindow {
        if let window {
            return window
        }

        let config = WKWebViewConfiguration()
        config.websiteDataStore = dataStore
        let webView = WKWebView(frame: NSRect(x: 0, y: 0, width: 860, height: 640), configuration: config)
        webView.navigationDelegate = self

        let created = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 860, height: 640),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        created.title = "校园网认证 · 192.168.100.200"
        created.isReleasedWhenClosed = false
        created.minSize = NSSize(width: 640, height: 480)
        created.center()
        created.delegate = self
        created.contentView = webView

        self.portalWebView = webView
        self.window = created
        return created
    }

    // MARK: - WKNavigationDelegate

    func webView(_ webView: WKWebView, didCommit navigation: WKNavigation!) {
        guard let url = webView.url,
              let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = url.host,
              !host.isEmpty
        else {
            return
        }
        window?.title = "校园网认证 · \(scheme)://\(host)"
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        handleLoadFailure(webView, error: error)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        handleLoadFailure(webView, error: error)
    }

    private func handleLoadFailure(_ webView: WKWebView, error: Error) {
        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled {
            return
        }

        window?.title = "校园网认证 · 页面加载失败"
        let html = """
        <!doctype html>
        <meta charset="utf-8">
        <meta name="color-scheme" content="light dark">
        <body style="font:15px/1.6 -apple-system,BlinkMacSystemFont,sans-serif;padding:40px;margin:0;color:#222;">
        <style>@media (prefers-color-scheme: dark){body{background:#1e1e1e;color:#e8e8e8;}a{color:#5ac8fa;}}</style>
        <h2 style="margin-top:0;">无法打开校园网认证页</h2>
        <p>请确认 Mac 已连接 CAUC 校园网，并尝试重新访问 <a href="http://192.168.100.200/">http://192.168.100.200/</a>。</p>
        </body>
        """
        webView.loadHTMLString(html, baseURL: nil)
    }

    // MARK: - NSWindowDelegate

    func windowWillClose(_ notification: Notification) {
        portalWebView?.stopLoading()
        if !isClosingProgrammatically {
            onUserClosedPortal?()
        }
    }
}
