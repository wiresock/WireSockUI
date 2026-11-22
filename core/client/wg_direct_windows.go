//go:build windows

package main

import (
	"fmt"
	"golang.zx2c4.com/wireguard/conn"
)

func pinDirectBind(b conn.Bind) error {
	binding, ok := b.(conn.BindSocketToInterface)
	if !ok {
		return fmt.Errorf("WireGuard bind cannot select the internet interface")
	}
	if err := binding.BindSocketToInterface4(bypassIfIndex.Load(), false); err != nil {
		return err
	}
	return binding.BindSocketToInterface6(bypassIfIndex.Load(), false)
}
