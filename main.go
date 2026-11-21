package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
)

func main() {
	if len(os.Args) == 2 && os.Args[1] == "--diagnose" {
		ctx := context.Background()
		status := checkConnectivity(ctx, false)
		result := struct {
			Connectivity connectivity `json:"connectivity"`
			Diagnostics  diagnostics  `json:"diagnostics"`
		}{status, readDiagnostics(ctx, status)}
		encoder := json.NewEncoder(os.Stdout)
		encoder.SetIndent("", "  ")
		if err := encoder.Encode(result); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	if err := runDesktop(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
