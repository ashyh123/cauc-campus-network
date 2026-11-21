package main

import (
	"context"
	"testing"
	"time"
)

func TestLoginSkipsPortalWhenAlreadyOnline(t *testing.T) {
	runFlow(context.Background(), true, flowServices{
		check:    func(context.Context, bool) connectivity { return connectivity{IPv4: true, IPv6: true} },
		diagnose: func(context.Context, connectivity) diagnostics { return diagnostics{} },
	}, func(update flowUpdate) {
		if !update.done || update.openPortal || update.trigger || update.status == nil || !update.status.IPv6 {
			t.Fatalf("unexpected update: %+v", update)
		}
	})
}

func TestLoginTriggersIPv6OnlyOnce(t *testing.T) {
	sequence := []connectivity{{}, {IPv4: true}, {IPv4: true}, {IPv4: true, IPv6: true}}
	checks, opens, triggers := 0, 0, 0
	done := false
	runFlow(context.Background(), true, flowServices{
		check: func(_ context.Context, fallback bool) connectivity {
			if fallback != (checks >= 2) {
				t.Fatalf("check %d selected fallback %v", checks, fallback)
			}
			result := sequence[checks]
			checks++
			return result
		},
		diagnose: func(context.Context, connectivity) diagnostics { return diagnostics{} },
		interval: time.Millisecond,
		limit:    time.Second,
	}, func(update flowUpdate) {
		if update.openPortal {
			opens++
		}
		if update.trigger {
			triggers++
		}
		if update.done {
			done = update.status != nil && update.status.IPv4 && update.status.IPv6
		}
	})
	if !done || opens != 1 || triggers != 1 || checks != len(sequence) {
		t.Fatalf("done=%v opens=%d triggers=%d checks=%d", done, opens, triggers, checks)
	}
}
