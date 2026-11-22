package main

import (
	"bufio"
	"encoding/binary"
	"strings"
	"testing"
	"unicode/utf16"
)

func TestSplitApplicationABI(t *testing.T) {
	paths := []string{`\Device\HarddiskVolume3\Приложения\a.exe`, `\Device\HarddiskVolume3\b.exe`}
	b, err := splitBlob(nil, paths, false)
	if err != nil {
		t.Fatal(err)
	}
	if binary.LittleEndian.Uint64(b) != 2 || binary.LittleEndian.Uint64(b[8:]) != uint64(len(b)) {
		t.Fatal("bad header")
	}
	for i, p := range paths {
		e := b[16+i*16:]
		offset := int(binary.LittleEndian.Uint64(e))
		length := int(binary.LittleEndian.Uint16(e[8:]))
		if length != len(utf16.Encode([]rune(p)))*2 {
			t.Fatal("length must be UTF-16 bytes")
		}
		wide := []uint16{}
		for j := 0; j < length; j += 2 {
			wide = append(wide, binary.LittleEndian.Uint16(b[48+offset+j:]))
		}
		if string(utf16.Decode(wide)) != p {
			t.Fatal("offset must be relative to string region")
		}
	}
}

func TestSplitProcessABI(t *testing.T) {
	b, err := splitBlob([]splitProcess{{pid: 42, parent: 7, path: `\Device\a.exe`}, {pid: 99, parent: 42}}, nil, true)
	if err != nil {
		t.Fatal(err)
	}
	if binary.LittleEndian.Uint64(b[16:]) != 42 || binary.LittleEndian.Uint64(b[24:]) != 7 || binary.LittleEndian.Uint64(b[48:]) != 99 {
		t.Fatal("process stride or handle size mismatch")
	}
	if binary.LittleEndian.Uint16(b[72:]) != 0 {
		t.Fatal("inaccessible process should retain parent, with empty path")
	}
}

func TestWGExclusionConfiguration(t *testing.T) {
	cfg, err := parseWGConfig(bufio.NewScanner(strings.NewReader(testWGConf + "\n#@ws:DisallowedApps = C:\\Program Files\\Browser\\browser.exe, D:\\app.exe\n")))
	if err != nil {
		t.Fatal(err)
	}
	if len(cfg.ExcludedApps) != 2 || cfg.ExcludedApps[0] != `C:\Program Files\Browser\browser.exe` {
		t.Fatal(cfg.ExcludedApps)
	}
	for _, directive := range []string{"AllowedApps = browser.exe", "DisallowedApps = browser.exe", "DisallowedApps = C:\\folder", "DisallowedIPs = 1.2.3.4", "PostUp = command"} {
		if _, err := parseWGConfig(bufio.NewScanner(strings.NewReader(testWGConf + "\n#@ws:" + directive))); err == nil {
			t.Fatalf("silently accepted %s", directive)
		}
	}
	if _, err := parseWGConfig(bufio.NewScanner(strings.NewReader(testWGConf + "\n[Peer]\nPublicKey = v7TPJ2oxxZQqBXiaoeKVDMW/pA2ioW/VN36QIjInxls="))); err == nil {
		t.Fatal("multiple peers silently merged")
	}
}

func TestDirectWGEndpoint(t *testing.T) {
	cfg, err := parseWGConfig(bufio.NewScanner(strings.NewReader(testWGConf)))
	if err != nil {
		t.Fatal(err)
	}
	cfg.Direct = true
	if !strings.Contains(cfg.uapi(), "endpoint=127.0.0.1:9000\n") {
		t.Fatal("direct mode lost endpoint")
	}
	cfg.Direct = false
	if strings.Contains(cfg.uapi(), "127.0.0.1") {
		t.Fatal("TURN mode leaked real endpoint")
	}
}
