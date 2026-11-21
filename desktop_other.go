//go:build !darwin

package main

import "errors"

func runDesktop() error {
	return errors.New("the desktop interface requires macOS; use --diagnose for the command-line probe")
}
