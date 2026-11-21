//go:build !windows

package main

import "syscall"

func initBypassInterface() error { return nil }

func bindToBypassInterface(string, syscall.RawConn) error { return nil }
