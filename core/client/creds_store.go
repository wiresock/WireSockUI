package main

// Persistent cache of VK TURN credentials (-creds-cache <file>).
//
// TURN credentials from VK live roughly ten minutes. Keeping them on disk
// lets a quick reconnect or app restart reuse them instead of walking the
// whole token chain again, which is exactly where VK shows the captcha and
// starts rate-limiting the IP. On Windows the file is encrypted with DPAPI
// for the current user; elsewhere it is written with 0600 permissions.

import (
	"encoding/json"
	"log"
	"os"
	"path/filepath"
	"sync"
	"time"
)

var (
	credsCachePath string
	credsCacheMu   sync.Mutex
)

type storedCreds struct {
	CacheID    int       `json:"cache_id"`
	Link       string    `json:"link"`
	Username   string    `json:"username"`
	Password   string    `json:"password"`
	ServerAddr string    `json:"server_addr"`
	ExpiresAt  time.Time `json:"expires_at"`
}

func loadCredsCache() {
	if credsCachePath == "" {
		return
	}
	credsCacheMu.Lock()
	defer credsCacheMu.Unlock()

	sealed, err := os.ReadFile(credsCachePath)
	if err != nil {
		if !os.IsNotExist(err) {
			log.Printf("[Creds Cache] read %s: %v", credsCachePath, err)
		}
		return
	}
	raw, err := unsealCreds(sealed)
	if err != nil {
		log.Printf("[Creds Cache] cannot decrypt cache, ignoring it: %v", err)
		return
	}
	var entries []storedCreds
	if err := json.Unmarshal(raw, &entries); err != nil {
		log.Printf("[Creds Cache] corrupt cache, ignoring it: %v", err)
		return
	}
	loaded := 0
	now := time.Now()
	for _, e := range entries {
		if !now.Before(e.ExpiresAt) || e.Username == "" {
			continue
		}
		cache := getStreamCache(e.CacheID * streamsPerCache)
		cache.mutex.Lock()
		cache.creds = TurnCredentials{
			Username:   e.Username,
			Password:   e.Password,
			ServerAddr: e.ServerAddr,
			ExpiresAt:  e.ExpiresAt,
			Link:       e.Link,
		}
		cache.mutex.Unlock()
		loaded++
	}
	if loaded > 0 {
		log.Printf("[Creds Cache] restored %d credential set(s) from disk", loaded)
	}
}

// saveCredsCache snapshots every still-valid credential set. Callers must not
// hold any StreamCredentialsCache mutex.
func saveCredsCache() {
	if credsCachePath == "" {
		return
	}
	credentialsStore.mu.RLock()
	ids := make(map[int]*StreamCredentialsCache, len(credentialsStore.caches))
	for id, c := range credentialsStore.caches {
		ids[id] = c
	}
	credentialsStore.mu.RUnlock()

	now := time.Now()
	entries := make([]storedCreds, 0, len(ids))
	for id, c := range ids {
		c.mutex.RLock()
		cr := c.creds
		c.mutex.RUnlock()
		if cr.Username == "" || !now.Before(cr.ExpiresAt) {
			continue
		}
		entries = append(entries, storedCreds{
			CacheID: id, Link: cr.Link, Username: cr.Username, Password: cr.Password,
			ServerAddr: cr.ServerAddr, ExpiresAt: cr.ExpiresAt,
		})
	}

	raw, err := json.Marshal(entries)
	if err != nil {
		log.Printf("[Creds Cache] marshal: %v", err)
		return
	}
	sealed, err := sealCreds(raw)
	if err != nil {
		log.Printf("[Creds Cache] encrypt: %v", err)
		return
	}

	credsCacheMu.Lock()
	defer credsCacheMu.Unlock()
	if err := os.MkdirAll(filepath.Dir(credsCachePath), 0o700); err != nil {
		log.Printf("[Creds Cache] mkdir: %v", err)
		return
	}
	tmp := credsCachePath + ".tmp"
	if err := os.WriteFile(tmp, sealed, 0o600); err != nil {
		log.Printf("[Creds Cache] write: %v", err)
		return
	}
	if err := os.Rename(tmp, credsCachePath); err != nil {
		log.Printf("[Creds Cache] replace: %v", err)
		_ = os.Remove(tmp)
	}
}
