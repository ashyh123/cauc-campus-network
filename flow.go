package main

import (
	"context"
	"time"
)

type flowUpdate struct {
	status     *connectivity
	report     *diagnostics
	message    string
	openPortal bool
	trigger    bool
	done       bool
}

type flowServices struct {
	check    func(context.Context, bool) connectivity
	diagnose func(context.Context, connectivity) diagnostics
	interval time.Duration
	limit    time.Duration
}

func runFlow(ctx context.Context, login bool, service flowServices, publish func(flowUpdate)) {
	status := service.check(ctx, false)
	if ctx.Err() != nil {
		return
	}
	if !login || status.IPv4 && status.IPv6 {
		report := service.diagnose(ctx, status)
		message := "检测完成。"
		if status.IPv4 && status.IPv6 {
			message = "IPv4 和 IPv6 均可访问公网。"
		}
		if ctx.Err() == nil {
			publish(flowUpdate{status: &status, report: &report, message: message, done: true})
		}
		return
	}

	copyStatus := status
	publish(flowUpdate{status: &copyStatus, openPortal: true, message: "请在校园网认证窗口登录。"})
	triggered := false
	wait, cancel := context.WithTimeout(ctx, service.limit)
	defer cancel()
	ticker := time.NewTicker(service.interval)
	defer ticker.Stop()
	for {
		if status.IPv4 && !status.IPv6 && !triggered {
			triggered = true
			publish(flowUpdate{trigger: true, message: "IPv4 已连通，正在访问中科大测试页并复检 IPv6。"})
		}

		select {
		case <-wait.Done():
		case <-ticker.C:
			next := service.check(wait, triggered)
			if wait.Err() == nil {
				status = next
				if status.IPv4 && status.IPv6 {
					report := service.diagnose(ctx, status)
					if ctx.Err() == nil {
						publish(flowUpdate{status: &status, report: &report, message: "认证后复检完成，IPv4 和 IPv6 均可访问公网。", done: true})
					}
					return
				}
				copyStatus := status
				publish(flowUpdate{status: &copyStatus})
				continue
			}
		}

		if ctx.Err() == nil {
			report := service.diagnose(ctx, status)
			message := "等待认证超时。请确认网页登录结果，并重新检测。"
			if status.IPv4 && !status.IPv6 {
				message = "IPv4 已连通，IPv6 仍未通过公网探测。请查看下方诊断。"
			}
			publish(flowUpdate{status: &status, report: &report, message: message, done: true})
		}
		return
	}
}
