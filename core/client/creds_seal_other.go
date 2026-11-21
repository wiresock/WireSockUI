//go:build !windows

package main

// Outside Windows there is no DPAPI; the cache file itself is 0600.
func sealCreds(plain []byte) ([]byte, error)    { return plain, nil }
func unsealCreds(sealed []byte) ([]byte, error) { return sealed, nil }
