package main

// Minimal WireGuard .conf parser for the in-process WireGuard mode (-wg-config).
//
// Only the keys the core needs are read: [Interface] PrivateKey, Address, DNS, MTU and
// [Peer] PublicKey, PresharedKey, AllowedIPs, PersistentKeepalive. Endpoint is ignored:
// the only "endpoint" is the set of TURN streams. Comment lines (#, ;) including WireSock
// (#@ws:) and VK TURN (#@vk:) directives are skipped.

import (
	"bufio"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net/netip"
	"os"
	"strconv"
	"strings"
)

type wgConfig struct {
	PrivateKey   []byte
	Addresses    []netip.Prefix
	DNS          []netip.Addr
	MTU          int
	PeerKey      []byte
	PresharedKey []byte
	AllowedIPs   []netip.Prefix
	Keepalive    int
}

func loadWGConfig(path string) (*wgConfig, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	return parseWGConfig(bufio.NewScanner(f))
}

func parseWGConfig(sc *bufio.Scanner) (*wgConfig, error) {
	cfg := &wgConfig{MTU: 1280}
	section := ""
	line := 0
	for sc.Scan() {
		line++
		t := strings.TrimSpace(strings.TrimPrefix(sc.Text(), utf8BOM))
		if t == "" || strings.HasPrefix(t, "#") || strings.HasPrefix(t, ";") {
			continue
		}
		if strings.HasPrefix(t, "[") && strings.HasSuffix(t, "]") {
			section = strings.ToLower(strings.TrimSpace(t[1 : len(t)-1]))
			continue
		}
		key, value, ok := strings.Cut(t, "=")
		if !ok {
			return nil, fmt.Errorf("line %d: expected key = value", line)
		}
		key, value = strings.ToLower(strings.TrimSpace(key)), strings.TrimSpace(value)
		var err error
		switch section + "." + key {
		case "interface.privatekey":
			cfg.PrivateKey, err = decodeWGKey(value)
		case "interface.address":
			cfg.Addresses, err = appendPrefixes(cfg.Addresses, value)
		case "interface.dns":
			for _, s := range splitList(value) {
				a, perr := netip.ParseAddr(s)
				if perr != nil {
					continue // search domains are not used
				}
				cfg.DNS = append(cfg.DNS, a)
			}
		case "interface.mtu":
			cfg.MTU, err = strconv.Atoi(value)
			if err == nil && (cfg.MTU < 576 || cfg.MTU > 1500) {
				err = fmt.Errorf("MTU %d out of range", cfg.MTU)
			}
		case "peer.publickey":
			cfg.PeerKey, err = decodeWGKey(value)
		case "peer.presharedkey":
			if value != "" {
				cfg.PresharedKey, err = decodeWGKey(value)
			}
		case "peer.allowedips":
			cfg.AllowedIPs, err = appendPrefixes(cfg.AllowedIPs, value)
		case "peer.persistentkeepalive":
			cfg.Keepalive, err = strconv.Atoi(value)
		}
		if err != nil {
			return nil, fmt.Errorf("line %d (%s): %w", line, key, err)
		}
	}
	if err := sc.Err(); err != nil {
		return nil, err
	}
	switch {
	case cfg.PrivateKey == nil:
		return nil, fmt.Errorf("[Interface] PrivateKey is missing")
	case len(cfg.Addresses) == 0:
		return nil, fmt.Errorf("[Interface] Address is missing")
	case cfg.PeerKey == nil:
		return nil, fmt.Errorf("[Peer] PublicKey is missing")
	case len(cfg.AllowedIPs) == 0:
		return nil, fmt.Errorf("[Peer] AllowedIPs is missing")
	}
	return cfg, nil
}

// uapi renders the configuration for device.IpcSet. The endpoint is a placeholder: the
// TURN bridge ignores it and delivers every packet to the server.
func (c *wgConfig) uapi() string {
	var b strings.Builder
	fmt.Fprintf(&b, "private_key=%s\n", hex.EncodeToString(c.PrivateKey))
	fmt.Fprintf(&b, "replace_peers=true\npublic_key=%s\n", hex.EncodeToString(c.PeerKey))
	if c.PresharedKey != nil {
		fmt.Fprintf(&b, "preshared_key=%s\n", hex.EncodeToString(c.PresharedKey))
	}
	fmt.Fprintf(&b, "endpoint=%s\n", turnBridgeEndpointString)
	if c.Keepalive > 0 {
		fmt.Fprintf(&b, "persistent_keepalive_interval=%d\n", c.Keepalive)
	}
	b.WriteString("replace_allowed_ips=true\n")
	for _, p := range c.AllowedIPs {
		fmt.Fprintf(&b, "allowed_ip=%s\n", p)
	}
	return b.String()
}

func decodeWGKey(s string) ([]byte, error) {
	k, err := base64.StdEncoding.DecodeString(s)
	if err != nil || len(k) != 32 {
		return nil, fmt.Errorf("not a WireGuard key")
	}
	return k, nil
}

func appendPrefixes(dst []netip.Prefix, value string) ([]netip.Prefix, error) {
	for _, s := range splitList(value) {
		p, err := netip.ParsePrefix(s)
		if err != nil {
			a, aerr := netip.ParseAddr(s)
			if aerr != nil {
				return nil, fmt.Errorf("bad address %q", s)
			}
			p = netip.PrefixFrom(a, a.BitLen())
		}
		dst = append(dst, p)
	}
	return dst, nil
}

func splitList(v string) []string {
	var out []string
	for _, s := range strings.Split(v, ",") {
		if s = strings.TrimSpace(s); s != "" {
			out = append(out, s)
		}
	}
	return out
}

var utf8BOM = string([]byte{0xef, 0xbb, 0xbf})
