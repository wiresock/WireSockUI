//go:build windows

package main

import (
	"unsafe"

	"golang.org/x/sys/windows"
)

var credsEntropy = []byte("cronixWG/vk-turn-creds/v1")

func dataBlob(b []byte) *windows.DataBlob {
	if len(b) == 0 {
		return &windows.DataBlob{}
	}
	return &windows.DataBlob{Size: uint32(len(b)), Data: &b[0]}
}

func blobBytes(b *windows.DataBlob) []byte {
	if b.Size == 0 || b.Data == nil {
		return nil
	}
	out := make([]byte, b.Size)
	copy(out, unsafe.Slice(b.Data, b.Size))
	return out
}

func sealCreds(plain []byte) ([]byte, error) {
	var out windows.DataBlob
	err := windows.CryptProtectData(dataBlob(plain), nil, dataBlob(credsEntropy), 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out)
	if err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return blobBytes(&out), nil
}

func unsealCreds(sealed []byte) ([]byte, error) {
	var out windows.DataBlob
	err := windows.CryptUnprotectData(dataBlob(sealed), nil, dataBlob(credsEntropy), 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out)
	if err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return blobBytes(&out), nil
}
