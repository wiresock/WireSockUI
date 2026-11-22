package main

import (
	"bytes"
	"errors"
	"io"
	"os"
	"testing"
)

type failedDriverRead struct{}

func (failedDriverRead) Read([]byte) (int, error) { return 0, io.ErrUnexpectedEOF }

func TestSplitBinaryRejectsUnknownOrUnreadable(t *testing.T) {
	for _, data := range [][]byte{nil, []byte("Mullvad 1.3.0"), make([]byte, 4096)} {
		if verifySplitBinary(bytes.NewReader(data)) == nil {
			t.Fatal("unknown driver accepted for mutating IOCTLs")
		}
	}
	if err := verifySplitBinary(failedDriverRead{}); !errors.Is(err, io.ErrUnexpectedEOF) {
		t.Fatalf("read error lost: %v", err)
	}
}

func TestSplitBinaryMatchesPackagedDriver(t *testing.T) {
	data, err := os.ReadFile("../../third_party/mullvad/mullvad-split-tunnel.sys")
	if os.IsNotExist(err) {
		t.Skip("run scripts/fetch-windows-deps.py to validate the packaged driver ABI")
	}
	if err != nil {
		t.Fatal(err)
	}
	if err := verifySplitBinary(bytes.NewReader(data)); err != nil {
		t.Fatalf("packaged driver no longer matches the supported ABI: %v", err)
	}
	data[len(data)/2] ^= 1
	if verifySplitBinary(bytes.NewReader(data)) == nil {
		t.Fatal("modified driver accepted")
	}
}
