package main

import (
	"crypto/sha256"
	"fmt"
	"io"
)

// This is an ABI allowlist, independent of the foreign service's name/version label.
func verifySplitBinary(r io.Reader) error {
	h := sha256.New()
	if _, err := io.Copy(h, r); err != nil {
		return err
	}
	if fmt.Sprintf("%x", h.Sum(nil)) != "10cf25bbcfe51fd663a1fec88a98e9b858f3a579589bb2ec496b66e4fdd1b201" {
		return fmt.Errorf("файл отличается от проверенной сборки Mullvad 1.3.0. Эта версия пока не поддерживается для повторного использования; это не означает доказанную несовместимость")
	}
	return nil
}
