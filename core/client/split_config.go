package main

// The driver ABI uses 64-bit SIZE_T/HANDLE, including on ARM64. Never marshal
// Go structs directly: padding and UTF-16 byte lengths are part of the ABI.
import (
	"encoding/binary"
	"fmt"
	"unicode/utf16"
)

type splitProcess struct {
	pid, parent uint32
	path        string
}

func splitBlob(processes []splitProcess, apps []string, discovery bool) ([]byte, error) {
	count, stride := len(apps), 16
	if discovery {
		count, stride = len(processes), 32
	}
	if count > 16384 {
		return nil, fmt.Errorf("too many split tunnel entries")
	}
	b := make([]byte, 16+count*stride)
	binary.LittleEndian.PutUint64(b, uint64(count))
	var strings []byte
	for i := 0; i < count; i++ {
		entry := b[16+i*stride:]
		var path string
		if discovery {
			p := processes[i]
			path = p.path
			binary.LittleEndian.PutUint64(entry, uint64(p.pid))
			binary.LittleEndian.PutUint64(entry[8:], uint64(p.parent))
			entry = entry[16:]
		} else {
			path = apps[i]
		}
		wide := utf16.Encode([]rune(path))
		if len(wide)*2 > 65534 {
			return nil, fmt.Errorf("EXE path is too long")
		}
		binary.LittleEndian.PutUint64(entry, uint64(len(strings)))
		binary.LittleEndian.PutUint16(entry[8:], uint16(len(wide)*2))
		for _, w := range wide {
			strings = binary.LittleEndian.AppendUint16(strings, w)
		}
	}
	b = append(b, strings...)
	binary.LittleEndian.PutUint64(b[8:], uint64(len(b)))
	return b, nil
}
