package main

import (
	"context"
	"fmt"
	"os/exec"
	"runtime"
	"time"

	"github.com/ebitengine/purego"
	"github.com/ebitengine/purego/objc"
)

type point struct{ X, Y float64 }
type size struct{ Width, Height float64 }
type rect struct {
	Origin point
	Size   size
}

type viewState struct {
	Status   *connectivity
	Report   *diagnostics
	Message  string
	Busy     bool
	Login    bool
	LastTest string
}

type pendingUpdate struct {
	id       uint64
	endpoint string
	update   flowUpdate
}

// Desktop objects and view state are owned by the AppKit thread.
type desktop struct {
	app, window              objc.ID
	portalWindow, portal     objc.ID
	trigger, portalDataStore objc.ID
	handler                  objc.ID
	ui                       dashboardUI
	state                    viewState
	cancel                   context.CancelFunc
	id                       uint64
	updates                  chan pendingUpdate
}

func init() {
	runtime.LockOSThread()
}

func runDesktop() error {
	for _, framework := range []string{"Cocoa", "WebKit"} {
		path := "/System/Library/Frameworks/" + framework + ".framework/" + framework
		if _, err := purego.Dlopen(path, purego.RTLD_GLOBAL|purego.RTLD_LAZY); err != nil {
			return fmt.Errorf("load %s: %w", framework, err)
		}
	}
	pool := send(class("NSAutoreleasePool"), "new")
	defer send(pool, "drain")
	d := &desktop{updates: make(chan pendingUpdate, 16)}
	d.state.Message = "正在读取网络状态…"
	d.app = send(class("NSApplication"), "sharedApplication")
	send(d.app, "setActivationPolicy:", int(0))
	handlerClass, err := objc.RegisterClass("CAUCMacDesktopHandler", objc.GetClass("NSObject"), nil, nil, []objc.MethodDef{
		{Cmd: sel("startLogin:"), Fn: d.startLogin},
		{Cmd: sel("refreshStatus:"), Fn: d.refreshStatus},
		{Cmd: sel("showPortal:"), Fn: d.showPortal},
		{Cmd: sel("stopChecking:"), Fn: d.stopChecking},
		{Cmd: sel("openWiFiSettings:"), Fn: d.openWiFiSettings},
		{Cmd: sel("numberOfRowsInTableView:"), Fn: d.interfaceCount},
		{Cmd: sel("tableView:objectValueForTableColumn:row:"), Fn: d.interfaceCell},
		{Cmd: sel("takeNetworkUpdates:"), Fn: d.takeUpdates},
		{Cmd: sel("windowWillClose:"), Fn: d.windowWillClose},
		{Cmd: sel("webView:didCommitNavigation:"), Fn: d.portalNavigated},
		{Cmd: sel("webView:didFailProvisionalNavigation:withError:"), Fn: d.portalFailed},
		{Cmd: sel("webView:didFailNavigation:withError:"), Fn: d.portalFailed},
	})
	if err != nil {
		return err
	}
	d.handler = send(objc.ID(handlerClass), "new")
	d.installMenu()
	d.window = makeWindow("CAUC 校园网", 740, 650, true)
	send(d.window, "setContentMinSize:", size{650, 610})
	send(d.window, "setDelegate:", d.handler)
	send(d.window, "setTitleVisibility:", int(1))
	send(d.window, "setTitlebarAppearsTransparent:", true)
	d.buildDashboard()
	send(class("NSTimer"), "scheduledTimerWithTimeInterval:target:selector:userInfo:repeats:", 0.15, d.handler, sel("takeNetworkUpdates:"), objc.ID(0), true)
	send(d.window, "makeKeyAndOrderFront:", objc.ID(0))
	send(d.app, "activateIgnoringOtherApps:", true)
	d.start(false, "")
	send(d.app, "run")
	return nil
}

func sel(name string) objc.SEL  { return objc.RegisterName(name) }
func class(name string) objc.ID { return objc.ID(objc.GetClass(name)) }

func send(receiver objc.ID, name string, args ...any) objc.ID {
	return receiver.Send(sel(name), args...)
}

func nsString(value string) objc.ID {
	return send(class("NSString"), "stringWithUTF8String:", value)
}

func goString(value objc.ID) string {
	if value == 0 {
		return ""
	}
	return objc.Send[string](value, sel("UTF8String"))
}

func makeWindow(title string, width, height float64, fullSizeContent bool) objc.ID {
	style := uint(15) // Titled, closable, miniaturizable and resizable.
	if fullSizeContent {
		style |= 1 << 15 // NSWindowStyleMaskFullSizeContentView
	}
	const backingBuffered = 2
	window := send(send(class("NSWindow"), "alloc"), "initWithContentRect:styleMask:backing:defer:", rect{Size: size{width, height}}, style, uint(backingBuffered), false)
	send(window, "setTitle:", nsString(title))
	send(window, "setReleasedWhenClosed:", false)
	send(window, "center")
	return window
}

func makeWebView(config objc.ID, width, height float64) objc.ID {
	return send(send(class("WKWebView"), "alloc"), "initWithFrame:configuration:", rect{Size: size{width, height}}, config)
}

func loadURL(view objc.ID, address string) {
	url := send(class("NSURL"), "URLWithString:", nsString(address))
	request := send(class("NSURLRequest"), "requestWithURL:", url)
	send(view, "loadRequest:", request)
}

func (d *desktop) installMenu() {
	bar := send(class("NSMenu"), "new")
	root := send(class("NSMenuItem"), "new")
	appMenu := send(class("NSMenu"), "new")
	quit := send(send(class("NSMenuItem"), "alloc"), "initWithTitle:action:keyEquivalent:", nsString("退出 CAUC 校园网"), sel("terminate:"), nsString("q"))
	send(appMenu, "addItem:", quit)
	send(root, "setSubmenu:", appMenu)
	send(bar, "addItem:", root)
	send(d.app, "setMainMenu:", bar)
}

func (d *desktop) selectedPortal() string {
	if objc.Send[int](d.ui.mode, sel("indexOfSelectedItem")) == 1 {
		return wiredPortal
	}
	return wifiPortal
}

func (d *desktop) startLogin(_ objc.ID, _ objc.SEL, _ objc.ID) {
	if !d.state.Busy {
		d.start(true, d.selectedPortal())
	}
}

func (d *desktop) refreshStatus(_ objc.ID, _ objc.SEL, _ objc.ID) {
	if !d.state.Busy {
		d.start(false, "")
	}
}

func (d *desktop) showPortal(_ objc.ID, _ objc.SEL, _ objc.ID) {
	d.openPortal(d.selectedPortal())
}

func (d *desktop) openWiFiSettings(_ objc.ID, _ objc.SEL, _ objc.ID) {
	go func() {
		_ = exec.Command("/usr/bin/open", "x-apple.systempreferences:com.apple.wifi-settings-extension").Run()
	}()
}

func (d *desktop) start(login bool, endpoint string) {
	if d.cancel != nil {
		d.cancel()
	}
	if d.trigger != 0 {
		send(d.trigger, "stopLoading")
	}
	ctx, cancel := context.WithCancel(context.Background())
	d.cancel = cancel
	d.id++
	id := d.id
	d.state.Busy = true
	d.state.Login = login
	d.state.Message = "正在分别检测 IPv4 和 IPv6…"
	d.render()
	go runFlow(ctx, login, flowServices{
		check: checkConnectivity, diagnose: readDiagnostics,
		interval: 2 * time.Second, limit: 55 * time.Second,
	}, func(update flowUpdate) {
		select {
		case d.updates <- pendingUpdate{id: id, endpoint: endpoint, update: update}:
		case <-ctx.Done():
		}
	})
}

func (d *desktop) stopChecking(_ objc.ID, _ objc.SEL, _ objc.ID) {
	if d.cancel != nil {
		d.cancel()
		d.cancel = nil
	}
	d.id++
	d.state.Busy = false
	d.state.Login = false
	d.state.Message = "已停止检测。"
	if d.trigger != 0 {
		send(d.trigger, "stopLoading")
	}
	d.render()
}

func (d *desktop) takeUpdates(_ objc.ID, _ objc.SEL, _ objc.ID) {
	changed := false
	for {
		select {
		case item := <-d.updates:
			if item.id != d.id {
				continue
			}
			changed = true
			u := item.update
			if u.status != nil {
				d.state.Status = u.status
				d.state.LastTest = time.Now().Format("15:04:05")
			}
			if u.report != nil {
				d.state.Report = u.report
			}
			if u.message != "" {
				d.state.Message = u.message
			}
			if u.openPortal {
				d.openPortal(item.endpoint)
			}
			if u.trigger {
				d.openTrigger()
			}
			if u.done {
				d.state.Busy = false
				d.state.Login = false
				if d.trigger != 0 {
					send(d.trigger, "stopLoading")
				}
				d.cancel()
				d.cancel = nil
			}
		default:
			if changed {
				d.render()
			}
			return
		}
	}
}

func (d *desktop) newPortalWebView(width, height float64) objc.ID {
	if d.portalDataStore == 0 {
		d.portalDataStore = send(send(class("WKWebsiteDataStore"), "nonPersistentDataStore"), "retain")
	}
	config := send(class("WKWebViewConfiguration"), "new")
	send(config, "setWebsiteDataStore:", d.portalDataStore)
	view := makeWebView(config, width, height)
	send(config, "release")
	return view
}

func (d *desktop) openPortal(endpoint string) {
	if d.portal == 0 {
		d.portalWindow = makeWindow("校园网认证", 840, 630, false)
		d.portal = d.newPortalWebView(840, 630)
		send(d.portal, "setNavigationDelegate:", d.handler)
		send(d.portalWindow, "setContentView:", d.portal)
	}
	send(d.portalWindow, "setTitle:", nsString("校园网认证  ·  "+endpoint))
	loadURL(d.portal, endpoint)
	send(d.portalWindow, "makeKeyAndOrderFront:", objc.ID(0))
}

func (d *desktop) openTrigger() {
	if d.trigger == 0 {
		d.trigger = d.newPortalWebView(1, 1)
	}
	loadURL(d.trigger, triggerURL)
}

func (d *desktop) windowWillClose(_ objc.ID, _ objc.SEL, _ objc.ID) {
	send(d.app, "terminate:", objc.ID(0))
}

func (d *desktop) portalNavigated(_ objc.ID, _ objc.SEL, view objc.ID, _ objc.ID) {
	url := send(view, "URL")
	scheme := goString(send(url, "scheme"))
	host := goString(send(url, "host"))
	if (scheme == "http" || scheme == "https") && host != "" {
		send(d.portalWindow, "setTitle:", nsString("校园网认证  ·  "+scheme+"://"+host))
	}
}

func (d *desktop) portalFailed(_ objc.ID, _ objc.SEL, view objc.ID, _ objc.ID, err objc.ID) {
	if objc.Send[int](err, sel("code")) == -999 { // A new request replaced this one.
		return
	}
	send(d.portalWindow, "setTitle:", nsString("校园网认证  ·  页面加载失败"))
	html := `<meta charset="utf-8"><body style="font:16px -apple-system;padding:36px"><h2>无法打开认证页</h2><p>请确认 Mac 已连上 CAUC 校园网，并在主窗口选择对应的无线或有线入口。</p></body>`
	send(view, "loadHTMLString:baseURL:", nsString(html), objc.ID(0))
}
