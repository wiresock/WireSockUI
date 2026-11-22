//go:build !windows

package main

import "golang.zx2c4.com/wireguard/conn"

func pinDirectBind(conn.Bind) error { return nil }
