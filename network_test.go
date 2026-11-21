package main

import (
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestProbeUsesRequestedFamilyAndRejectsRedirect(t *testing.T) {
	for _, test := range []struct{ family, other, address string }{
		{"tcp4", "tcp6", "127.0.0.1:0"},
		{"tcp6", "tcp4", "[::1]:0"},
	} {
		t.Run(test.family, func(t *testing.T) {
			listener, err := net.Listen(test.family, test.address)
			if err != nil {
				t.Skipf("loopback unavailable: %v", err)
			}
			server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Path == "/portal" {
					http.Redirect(w, r, "/ok", http.StatusFound)
					return
				}
				w.WriteHeader(http.StatusNoContent)
			}))
			server.Listener.Close()
			server.Listener = listener
			server.Start()
			defer server.Close()
			ctx := context.Background()
			if !probeFamily(ctx, test.family, []string{server.URL + "/ok"}) {
				t.Fatal("matching family should connect")
			}
			if probeFamily(ctx, test.other, []string{server.URL + "/ok"}) {
				t.Fatal("wrong family should not connect")
			}
			if probeFamily(ctx, test.family, []string{server.URL + "/portal"}) {
				t.Fatal("Portal redirect should not count as success")
			}
		})
	}
}

func TestParseIPv6Route(t *testing.T) {
	output := "   route to: default\n    gateway: fe80::1%en0\n  interface: en0\n      flags: <UP,GATEWAY,DONE>\n"
	device, gateway := parseIPv6Route(output)
	if device != "en0" || gateway != "fe80::1%en0" {
		t.Fatalf("got device %q and gateway %q", device, gateway)
	}
}
