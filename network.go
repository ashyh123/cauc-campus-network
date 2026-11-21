package main

import (
	"context"
	"fmt"
	"net"
	"net/http"
	"net/netip"
	"os/exec"
	"runtime"
	"strings"
	"time"
)

const (
	wifiPortal  = "http://192.168.4.252/"
	wiredPortal = "http://192.168.100.200/"
	triggerURL  = "http://test6.ustc.edu.cn/"
	ustcProbe   = "https://test6.ustc.edu.cn/"
)

var publicTargets = []string{
	"https://www.cloudflare.com/cdn-cgi/trace",
	"https://www.apple.com/library/test/success.html",
}

type connectivity struct {
	IPv4 bool `json:"ipv4"`
	IPv6 bool `json:"ipv6"`
}

type interfaceIPv6 struct {
	Name   string `json:"name"`
	Global int    `json:"global"`
	Local  int    `json:"local"`
}

type diagnostics struct {
	Interfaces   []interfaceIPv6 `json:"interfaces"`
	RouteDevice  string          `json:"routeDevice"`
	RouteGateway string          `json:"routeGateway"`
	Summary      string          `json:"summary"`
}

func checkConnectivity(ctx context.Context, ustcFallback bool) connectivity {
	v4, v6 := make(chan bool, 1), make(chan bool, 1)
	go func() { v4 <- probeFamily(ctx, "tcp4", publicTargets) }()
	go func() {
		targets := publicTargets
		if ustcFallback {
			targets = append(append([]string(nil), publicTargets...), ustcProbe)
		}
		v6 <- probeFamily(ctx, "tcp6", targets)
	}()
	return connectivity{IPv4: <-v4, IPv6: <-v6}
}

func probeFamily(ctx context.Context, family string, targets []string) bool {
	ctx, cancel := context.WithTimeout(ctx, 3500*time.Millisecond)
	defer cancel()
	dialer := &net.Dialer{Timeout: 2 * time.Second}
	transport := &http.Transport{
		DialContext: func(ctx context.Context, _ string, address string) (net.Conn, error) {
			return dialer.DialContext(ctx, family, address)
		},
		DisableKeepAlives:   true,
		TLSHandshakeTimeout: 2 * time.Second,
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{
		Transport: transport,
		CheckRedirect: func(_ *http.Request, _ []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	results := make(chan bool, len(targets))
	for _, target := range targets {
		go func(target string) {
			request, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
			if err != nil {
				results <- false
				return
			}
			response, err := client.Do(request)
			if err != nil {
				results <- false
				return
			}
			defer response.Body.Close()
			results <- response.StatusCode >= 200 && response.StatusCode < 300
		}(target)
	}
	for range targets {
		if <-results {
			return true
		}
	}
	return false
}

func readDiagnostics(ctx context.Context, status connectivity) diagnostics {
	report := diagnostics{Interfaces: make([]interfaceIPv6, 0)}
	interfaces, err := net.Interfaces()
	if err == nil {
		for _, link := range interfaces {
			if link.Flags&net.FlagUp == 0 || link.Flags&net.FlagLoopback != 0 {
				continue
			}
			counts := interfaceIPv6{Name: link.Name}
			addresses, _ := link.Addrs()
			for _, item := range addresses {
				address, _, _ := strings.Cut(item.String(), "/")
				ip, err := netip.ParseAddr(address)
				if err != nil || !ip.Is6() || ip.Is4In6() {
					continue
				}
				if ip.IsLinkLocalUnicast() {
					counts.Local++
				}
				if ip.As16()[0]&0xe0 == 0x20 {
					counts.Global++
				}
			}
			if counts.Local != 0 || counts.Global != 0 || strings.HasPrefix(link.Name, "en") {
				report.Interfaces = append(report.Interfaces, counts)
			}
		}
	}
	if runtime.GOOS == "darwin" {
		routeContext, cancel := context.WithTimeout(ctx, 2*time.Second)
		defer cancel()
		output, err := exec.CommandContext(routeContext, "/sbin/route", "-n", "get", "-inet6", "default").Output()
		if err == nil {
			report.RouteDevice, report.RouteGateway = parseIPv6Route(string(output))
		}
	}
	switch {
	case status.IPv6:
		report.Summary = "IPv6 公网探测成功。"
	case err != nil:
		report.Summary = "无法读取网络接口。"
	case report.RouteDevice == "":
		report.Summary = "IPv6 探测未通过，系统未报告 IPv6 默认路由。可在系统设置中重新连接校园网。"
	case strings.HasPrefix(report.RouteDevice, "utun"):
		report.Summary = fmt.Sprintf("IPv6 默认路由使用 %s 隧道接口。可检查当前 VPN 或网络扩展。", report.RouteDevice)
	default:
		report.Summary = fmt.Sprintf("系统已在 %s 上建立 IPv6 默认路由。请检查地址与校园网认证状态。", report.RouteDevice)
	}
	return report
}

func parseIPv6Route(output string) (device string, gateway string) {
	for _, line := range strings.Split(output, "\n") {
		key, value, ok := strings.Cut(strings.TrimSpace(line), ":")
		if !ok {
			continue
		}
		switch key {
		case "interface":
			device = strings.TrimSpace(value)
		case "gateway":
			gateway = strings.TrimSpace(value)
		}
	}
	return device, gateway
}
