package main

import (
	"strconv"

	"github.com/ebitengine/purego/objc"
)

const (
	verticalLayout            = 1
	leadingAlignment          = 5
	centerYAlignment          = 10
	fillDistribution          = 0
	fillEquallyDistribution   = 1
	sidebarMaterial           = 7
	contentBackgroundMaterial = 18
	behindWindowBlending      = 0
	withinWindowBlending      = 1
)

type statusTile struct {
	icon, status objc.ID
}

type dashboardUI struct {
	mode, connect, refresh, stop  objc.ID
	message, summary, time, route objc.ID
	interfaces                    objc.ID
	ipv4, ipv6                    statusTile
}

func (d *desktop) buildDashboard() {
	root := effectView(sidebarMaterial, behindWindowBlending, 0)
	send(d.window, "setContentView:", root)

	headIcon := symbol("network", "校园网", "systemTealColor", 42)
	name := label("CAUC 校园网", 31, 0.4, "labelColor")
	d.ui.time = label("正在检测网络…", 12, 0, "secondaryLabelColor")
	head := horizontal(14, headIcon, vertical(2, name, d.ui.time))

	left, ipv4 := makeStatusTile("IPv4")
	right, ipv6 := makeStatusTile("IPv6")
	d.ui.ipv4, d.ui.ipv6 = ipv4, ipv6
	families := horizontal(12, left, right)
	send(families, "setDistribution:", int(fillEquallyDistribution))

	d.ui.mode = send(send(class("NSPopUpButton"), "alloc"), "initWithFrame:pullsDown:", rect{}, false)
	send(d.ui.mode, "addItemWithTitle:", nsString("校园 Wi-Fi"))
	send(d.ui.mode, "addItemWithTitle:", nsString("有线校园网"))
	send(d.ui.mode, "selectItemAtIndex:", int(0))
	send(d.ui.mode, "setAccessibilityLabel:", nsString("校园网认证入口"))
	d.ui.connect = button("开始认证", d.handler, "startLogin:")
	send(d.ui.connect, "setKeyEquivalent:", nsString("\r"))
	d.ui.refresh = button("重新检测", d.handler, "refreshStatus:")
	d.ui.stop = button("停止检测", d.handler, "stopChecking:")
	openPortal := button("打开认证页", d.handler, "showPortal:")
	d.ui.message = label("", 13, 0, "secondaryLabelColor")
	controls := vertical(9, horizontal(8, d.ui.mode, d.ui.connect, openPortal, d.ui.refresh, d.ui.stop), d.ui.message)
	actions := panel(controls)
	equalWidth(d.ui.message, controls)

	networkSettings := button("Wi-Fi 设置…", d.handler, "openWiFiSettings:")
	spacer := send(class("NSView"), "new")
	diagnosticTitle := horizontal(8, label("IPv6 诊断", 17, 0.3, "labelColor"), spacer, networkSettings)
	d.ui.summary = label("", 13, 0, "secondaryLabelColor")
	d.ui.route = label("", 13, 0.23, "labelColor")
	interfaceList := d.buildInterfaceList()
	diagnosticItems := vertical(10, diagnosticTitle, d.ui.summary, d.ui.route, interfaceList)
	for _, child := range []objc.ID{diagnosticTitle, d.ui.summary, d.ui.route, interfaceList} {
		equalWidth(child, diagnosticItems)
	}
	details := panel(diagnosticItems)

	main := vertical(14, head, families, actions, details)
	inset(root, main, 42, 25)
	for _, wide := range []objc.ID{families, actions, details} {
		equalWidth(wide, main)
	}
}

func (d *desktop) buildInterfaceList() objc.ID {
	table := send(class("NSTableView"), "new")
	send(table, "setColumnAutoresizingStyle:", int(1)) // Uniform column sizing.
	send(table, "setUsesAlternatingRowBackgroundColors:", true)
	send(table, "setRowHeight:", float64(24))
	for _, item := range []struct{ id, title string }{
		{"name", "网络接口"}, {"global", "全局 IPv6"}, {"local", "链路本地 IPv6"},
	} {
		column := send(send(class("NSTableColumn"), "alloc"), "initWithIdentifier:", nsString(item.id))
		send(column, "setTitle:", nsString(item.title))
		send(column, "setWidth:", float64(176))
		send(table, "addTableColumn:", column)
		send(column, "release")
	}
	send(table, "setDataSource:", d.handler)
	d.ui.interfaces = table
	container := send(class("NSScrollView"), "new")
	send(container, "setHasVerticalScroller:", true)
	send(container, "setAutohidesScrollers:", true)
	send(container, "setDrawsBackground:", false)
	send(container, "setDocumentView:", table)
	anchor := send(container, "heightAnchor")
	activate(send(anchor, "constraintGreaterThanOrEqualToConstant:", float64(105)))
	return container
}

func (d *desktop) interfaceCount(_ objc.ID, _ objc.SEL, _ objc.ID) int {
	if d.state.Report == nil {
		return 0
	}
	return len(d.state.Report.Interfaces)
}

func (d *desktop) interfaceCell(_ objc.ID, _ objc.SEL, _ objc.ID, column objc.ID, row int) objc.ID {
	if d.state.Report == nil || row < 0 || row >= len(d.state.Report.Interfaces) {
		return 0
	}
	item := d.state.Report.Interfaces[row]
	switch goString(send(column, "identifier")) {
	case "name":
		return nsString(item.Name)
	case "global":
		return nsString(strconv.Itoa(item.Global))
	case "local":
		return nsString(strconv.Itoa(item.Local))
	}
	return 0
}

func (d *desktop) render() {
	if d.state.Status == nil {
		d.ui.ipv4.update(false, false)
		d.ui.ipv6.update(false, false)
	} else {
		d.ui.ipv4.update(true, d.state.Status.IPv4)
		d.ui.ipv6.update(true, d.state.Status.IPv6)
	}
	setText(d.ui.message, d.state.Message)
	if d.state.LastTest != "" {
		setText(d.ui.time, "上次检测  "+d.state.LastTest)
	}
	send(d.ui.mode, "setEnabled:", !d.state.Login)
	send(d.ui.connect, "setEnabled:", !d.state.Busy)
	send(d.ui.refresh, "setEnabled:", !d.state.Busy)
	send(d.ui.stop, "setEnabled:", d.state.Busy)
	if d.state.Report == nil {
		setText(d.ui.summary, "等待首次检测结果")
		setText(d.ui.route, "默认路由  ·  待检测")
		return
	}
	setText(d.ui.summary, d.state.Report.Summary)
	route := "系统未报告 IPv6 默认路由"
	if d.state.Report.RouteDevice != "" {
		route = d.state.Report.RouteDevice
		if d.state.Report.RouteGateway != "" {
			route += " → " + d.state.Report.RouteGateway
		}
	}
	setText(d.ui.route, "默认路由  ·  "+route)
	send(d.ui.interfaces, "reloadData")
}

func makeStatusTile(family string) (objc.ID, statusTile) {
	tile := statusTile{
		icon:   symbol("circle.dotted", "", "secondaryLabelColor", 24),
		status: label("待检测", 15, 0.23, "secondaryLabelColor"),
	}
	content := vertical(5,
		label(family, 25, 0.35, "labelColor"),
		horizontal(7, tile.icon, tile.status),
	)
	return panel(content), tile
}

func (tile statusTile) update(known, online bool) {
	name, text, tint := "circle.dotted", "待检测", "secondaryLabelColor"
	switch {
	case known && online:
		name, text, tint = "checkmark.circle.fill", "公网可达", "systemGreenColor"
	case known:
		name, text, tint = "exclamationmark.circle.fill", "探测未通过", "systemOrangeColor"
	}
	setText(tile.status, text)
	setSymbol(tile.icon, name, "", tint)
}

func effectView(material, blending int, corner float64) objc.ID {
	view := send(class("NSVisualEffectView"), "new")
	send(view, "setMaterial:", material)
	send(view, "setBlendingMode:", blending)
	if corner > 0 {
		send(view, "setWantsLayer:", true)
		layer := send(view, "layer")
		send(layer, "setCornerRadius:", corner)
		send(layer, "setMasksToBounds:", true)
	}
	return view
}

func panel(child objc.ID) objc.ID {
	view := effectView(contentBackgroundMaterial, withinWindowBlending, 17)
	inset(view, child, 15, 16)
	return view
}

func label(text string, fontSize, weight float64, color string) objc.ID {
	field := send(class("NSTextField"), "labelWithString:", nsString(text))
	send(field, "setFont:", send(class("NSFont"), "systemFontOfSize:weight:", fontSize, weight))
	send(field, "setTextColor:", send(class("NSColor"), color))
	return field
}

func setText(field objc.ID, value string) {
	send(field, "setStringValue:", nsString(value))
}

func button(title string, target objc.ID, action string) objc.ID {
	control := send(class("NSButton"), "buttonWithTitle:target:action:", nsString(title), target, sel(action))
	send(control, "setBezelStyle:", int(1))
	return control
}

func symbol(name, accessibilityLabel, tint string, extent float64) objc.ID {
	view := send(class("NSImageView"), "new")
	send(view, "setImageScaling:", int(3)) // NSImageScaleProportionallyUpOrDown
	setSymbol(view, name, accessibilityLabel, tint)
	for _, axis := range []string{"widthAnchor", "heightAnchor"} {
		activate(send(send(view, axis), "constraintEqualToConstant:", extent))
	}
	return view
}

func setSymbol(view objc.ID, name, accessibilityLabel, tint string) {
	image := send(class("NSImage"), "imageWithSystemSymbolName:accessibilityDescription:", nsString(name), nsString(accessibilityLabel))
	send(view, "setImage:", image)
	send(view, "setContentTintColor:", send(class("NSColor"), tint))
}

func vertical(spacing float64, views ...objc.ID) objc.ID {
	return stack(verticalLayout, leadingAlignment, spacing, views)
}

func horizontal(spacing float64, views ...objc.ID) objc.ID {
	return stack(0, centerYAlignment, spacing, views)
}

func stack(orientation, alignment int, spacing float64, views []objc.ID) objc.ID {
	container := send(class("NSStackView"), "new")
	send(container, "setOrientation:", orientation)
	send(container, "setAlignment:", alignment)
	send(container, "setDistribution:", int(fillDistribution))
	send(container, "setSpacing:", spacing)
	for _, view := range views {
		send(view, "setTranslatesAutoresizingMaskIntoConstraints:", false)
		send(container, "addArrangedSubview:", view)
	}
	return container
}

func activate(constraint objc.ID) {
	send(constraint, "setActive:", true)
}

func equalWidth(child, parent objc.ID) {
	activate(send(send(child, "widthAnchor"), "constraintEqualToAnchor:", send(parent, "widthAnchor")))
}

func inset(parent, child objc.ID, top, margin float64) {
	send(child, "setTranslatesAutoresizingMaskIntoConstraints:", false)
	send(parent, "addSubview:", child)
	for _, pair := range []struct {
		edge     string
		constant float64
	}{
		{"topAnchor", top}, {"leadingAnchor", margin},
		{"trailingAnchor", -margin}, {"bottomAnchor", -margin},
	} {
		activate(send(send(child, pair.edge), "constraintEqualToAnchor:constant:", send(parent, pair.edge), pair.constant))
	}
}
