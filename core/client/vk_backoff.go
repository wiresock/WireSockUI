package main

// Exponential backoff for failed VK credential fetches.
//
// When VK starts refusing login.vk.ru (connection refused, rate limits) every
// stream used to retry after two seconds, which only extends the ban. All
// streams now share one backoff window: the first failure waits 5 s, each
// further failure doubles it up to 5 minutes, and a success resets it.
// Failures that arrive while a window is already open just wait it out
// without growing it, so N parallel streams do not multiply the delay.

import (
	"math/rand"
	"sync"
	"time"
)

const (
	vkBackoffBase = 5 * time.Second
	vkBackoffMax  = 5 * time.Minute
)

var vkBackoff struct {
	mu       sync.Mutex
	failures int
	until    time.Time
}

func vkBackoffDelay(failures int) time.Duration {
	d := vkBackoffBase
	for i := 1; i < failures && d < vkBackoffMax; i++ {
		d *= 2
	}
	if d > vkBackoffMax {
		d = vkBackoffMax
	}
	return d
}

// noteVkAuthFailure returns how long the calling stream should wait.
func noteVkAuthFailure(now time.Time) (wait time.Duration, grew bool) {
	vkBackoff.mu.Lock()
	defer vkBackoff.mu.Unlock()
	if now.Before(vkBackoff.until) {
		return vkBackoff.until.Sub(now), false
	}
	vkBackoff.failures++
	d := vkBackoffDelay(vkBackoff.failures)
	d += time.Duration(rand.Int63n(int64(d) / 5)) // up to +20% jitter
	vkBackoff.until = now.Add(d)
	return d, true
}

func noteVkAuthSuccess() {
	vkBackoff.mu.Lock()
	vkBackoff.failures = 0
	vkBackoff.until = time.Time{}
	vkBackoff.mu.Unlock()
}
