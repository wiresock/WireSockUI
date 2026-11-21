package main

import (
	"bytes"
	"encoding/binary"
	"net"
	"testing"
)

func TestIPv4UDPRoundTrip(t *testing.T) {
	fake := &net.UDPAddr{IP: net.IPv4(10, 255, 255, 2), Port: 51820}
	client := &net.UDPAddr{IP: net.IPv4(10, 255, 255, 1), Port: 61234}
	payload := []byte("wireguard handshake initiation")

	pkt, err := buildIPv4UDP(client, fake, payload, 7)
	if err != nil {
		t.Fatal(err)
	}
	if checksum16(pkt[:ipv4HeaderLen], 0) != 0 {
		t.Fatal("IPv4 header checksum does not verify")
	}
	// UDP checksum verifies to zero over pseudo-header + datagram.
	udp := pkt[ipv4HeaderLen:]
	pseudo := uint32(0x0aff) + 0xff01 + 0x0aff + 0xff02 + ipProtoUDP + uint32(len(udp))
	if checksum16(udp, pseudo) != 0 {
		t.Fatal("UDP checksum does not verify")
	}

	got, src, ok := parseIPv4UDP(pkt, fake)
	if !ok || !bytes.Equal(got, payload) || src.String() != client.String() {
		t.Fatalf("parse got %q from %v ok=%v", got, src, ok)
	}
}

func TestParseIPv4UDPIgnoresOtherTraffic(t *testing.T) {
	fake := &net.UDPAddr{IP: net.IPv4(10, 255, 255, 2), Port: 51820}
	client := &net.UDPAddr{IP: net.IPv4(10, 255, 255, 1), Port: 5353}

	wrongPort, _ := buildIPv4UDP(client, &net.UDPAddr{IP: fake.IP, Port: 5353}, []byte("mdns"), 1)
	wrongDst, _ := buildIPv4UDP(client, &net.UDPAddr{IP: net.IPv4(224, 0, 0, 251), Port: 51820}, []byte("x"), 1)
	frag, _ := buildIPv4UDP(client, fake, []byte("frag"), 1)
	binary.BigEndian.PutUint16(frag[6:8], 0x2000) // MF
	tcp, _ := buildIPv4UDP(client, fake, []byte("tcp"), 1)
	tcp[9] = 6
	ipv6 := make([]byte, 60)
	ipv6[0] = 0x60

	for name, p := range map[string][]byte{"port": wrongPort, "dst": wrongDst, "fragment": frag, "tcp": tcp, "ipv6": ipv6, "short": {0x45}} {
		if _, _, ok := parseIPv4UDP(p, fake); ok {
			t.Fatalf("%s packet should be ignored", name)
		}
	}
}

func TestWintunLinkAddresses(t *testing.T) {
	local, fake, err := wintunLinkAddresses("10.255.255.2:51820")
	if err != nil || local.String() != "10.255.255.1" || fake.Port != 51820 {
		t.Fatalf("got %v %v %v", local, fake, err)
	}
	for _, bad := range []string{"8.8.8.2:51820", "10.0.0.1:51820", "10.0.0.3:1", "[fd00::2]:51820"} {
		if _, _, err := wintunLinkAddresses(bad); err == nil {
			t.Fatalf("%s should be rejected", bad)
		}
	}
}

func TestIsUnicastIPv4ToRemote(t *testing.T) {
	fake := net.IPv4(10, 255, 255, 2)
	src := &net.UDPAddr{IP: net.IPv4(10, 255, 255, 1), Port: 1}
	for dst, want := range map[string]bool{"142.250.1.1": true, "10.255.255.3": false, "239.255.255.250": false, "255.255.255.255": false, "169.254.1.1": false} {
		p, _ := buildIPv4UDP(src, &net.UDPAddr{IP: net.ParseIP(dst), Port: 1}, nil, 1)
		if got := isUnicastIPv4ToRemote(p, fake); got != want {
			t.Fatalf("%s: got %v want %v", dst, got, want)
		}
	}
}
