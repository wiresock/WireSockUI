package main

import (
	"bufio"
	"bytes"
	"errors"
	"net"
	"net/netip"
	"strings"
	"testing"
	"time"

	"golang.zx2c4.com/wireguard/conn"
)

const testWGConf = `[Interface]
PrivateKey = kA7iF5CIrY97dUfZNHNqtR+GTtHVbW6A91RY+Hw6z2I=
Address = 10.88.0.2/32
DNS = 1.1.1.1, 1.0.0.1, corp.example
MTU = 1280
#@ws:VirtualAdapterMode = true

[Peer]
PublicKey = v7TPJ2oxxZQqBXiaoeKVDMW/pA2ioW/VN36QIjInxls=
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = 127.0.0.1:9000
PersistentKeepalive = 25
#@vk:Server = 45.196.97.204:57001
`

func TestParseWGConfig(t *testing.T) {
	cfg, err := parseWGConfig(bufio.NewScanner(strings.NewReader(testWGConf)))
	if err != nil {
		t.Fatal(err)
	}
	if len(cfg.PrivateKey) != 32 || len(cfg.PeerKey) != 32 || cfg.MTU != 1280 || cfg.Keepalive != 25 {
		t.Fatalf("unexpected config %+v", cfg)
	}
	if joinPrefixesForTest(cfg.Addresses) != "10.88.0.2/32" || joinPrefixesForTest(cfg.AllowedIPs) != "0.0.0.0/0,::/0" {
		t.Fatalf("addresses %v allowed %v", cfg.Addresses, cfg.AllowedIPs)
	}
	if len(cfg.DNS) != 2 {
		t.Fatalf("DNS should keep the two IPs and skip the search domain: %v", cfg.DNS)
	}
	u := cfg.uapi()
	for _, want := range []string{"private_key=", "public_key=", "endpoint=" + turnBridgeEndpointString,
		"persistent_keepalive_interval=25", "allowed_ip=0.0.0.0/0", "allowed_ip=::/0"} {
		if !strings.Contains(u, want) {
			t.Fatalf("uapi lacks %q:\n%s", want, u)
		}
	}
	if strings.Contains(u, "127.0.0.1") {
		t.Fatal("profile Endpoint must be ignored")
	}
}

func TestParseWGConfigErrors(t *testing.T) {
	for name, conf := range map[string]string{
		"no key":      "[Interface]\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = v7TPJ2oxxZQqBXiaoeKVDMW/pA2ioW/VN36QIjInxls=\nAllowedIPs = 0.0.0.0/0\n",
		"bad key":     "[Interface]\nPrivateKey = abc\n",
		"bad address": "[Interface]\nPrivateKey = kA7iF5CIrY97dUfZNHNqtR+GTtHVbW6A91RY+Hw6z2I=\nAddress = nope\n",
		"bad mtu":     strings.Replace(testWGConf, "MTU = 1280", "MTU = 99", 1),
	} {
		if _, err := parseWGConfig(bufio.NewScanner(strings.NewReader(conf))); err == nil {
			t.Fatalf("%s: expected an error", name)
		}
	}
}

func TestTurnBridgeBothDirections(t *testing.T) {
	b := newTurnBridge()
	pc := turnBridgePacketConn{b}
	fns, _, err := b.Open(0)
	if err != nil {
		t.Fatal(err)
	}

	// wireguard-go -> streams
	if err := b.Send([][]byte{[]byte("handshake")}, turnBridgeEndpoint{}); err != nil {
		t.Fatal(err)
	}
	buf := make([]byte, 64)
	n, addr, err := pc.ReadFrom(buf)
	if err != nil || string(buf[:n]) != "handshake" || addr.String() != turnBridgeAddr.String() {
		t.Fatalf("ReadFrom got %q %v %v", buf[:n], addr, err)
	}

	// streams -> wireguard-go
	if _, err := pc.WriteTo([]byte("response"), addr); err != nil {
		t.Fatal(err)
	}
	pkts, sizes, eps := [][]byte{make([]byte, 64)}, make([]int, 1), make([]conn.Endpoint, 1)
	if n, err := fns[0](pkts, sizes, eps); err != nil || n != 1 || !bytes.Equal(pkts[0][:sizes[0]], []byte("response")) {
		t.Fatalf("receive got %d %q %v", n, pkts[0][:sizes[0]], err)
	}

	// Close unblocks receivers; the device may reopen the bind afterwards.
	go func() { time.Sleep(20 * time.Millisecond); _ = b.Close() }()
	if _, err := fns[0](pkts, sizes, eps); !errors.Is(err, net.ErrClosed) {
		t.Fatalf("receive after Close: %v", err)
	}
	if _, _, err := b.Open(0); err != nil {
		t.Fatalf("reopen: %v", err)
	}
	_ = pc.Close()
	if _, _, err := pc.ReadFrom(buf); !errors.Is(err, net.ErrClosed) {
		t.Fatalf("ReadFrom after Close: %v", err)
	}
}

func joinPrefixesForTest(ps []netip.Prefix) string {
	s := make([]string, len(ps))
	for i, p := range ps {
		s[i] = p.String()
	}
	return strings.Join(s, ",")
}
