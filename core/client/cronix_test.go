package main

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestVkBackoffDelayGrowsAndCaps(t *testing.T) {
	want := []time.Duration{5 * time.Second, 10 * time.Second, 20 * time.Second, 40 * time.Second}
	for i, w := range want {
		if got := vkBackoffDelay(i + 1); got != w {
			t.Fatalf("failures=%d: got %v want %v", i+1, got, w)
		}
	}
	if got := vkBackoffDelay(50); got != vkBackoffMax {
		t.Fatalf("cap: got %v want %v", got, vkBackoffMax)
	}
}

func TestVkBackoffSharedWindowDoesNotMultiply(t *testing.T) {
	noteVkAuthSuccess()
	defer noteVkAuthSuccess()
	now := time.Now()
	first, grew := noteVkAuthFailure(now)
	if !grew || first < vkBackoffBase {
		t.Fatalf("first failure: wait=%v grew=%v", first, grew)
	}
	// Seven more streams failing inside the same window must not grow it.
	for i := 0; i < 7; i++ {
		w, g := noteVkAuthFailure(now.Add(time.Second))
		if g || w > first {
			t.Fatalf("parallel failure %d grew the window: wait=%v grew=%v", i, w, g)
		}
	}
	// After the window, the next failure doubles.
	second, grew := noteVkAuthFailure(now.Add(first + time.Second))
	if !grew || second < 2*vkBackoffBase {
		t.Fatalf("second window: wait=%v grew=%v", second, grew)
	}
	noteVkAuthSuccess()
	if w, _ := noteVkAuthFailure(time.Now()); w >= 2*vkBackoffBase {
		t.Fatalf("success did not reset backoff: %v", w)
	}
}

func TestExternalCaptchaTokenRoundTrip(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	done := make(chan struct{})
	var token string
	var err error
	go func() {
		token, err = solveCaptchaExternally(ctx, "https://id.vk.ru/not_robot_captcha?session_token=x", 3)
		close(done)
	}()
	id := waitForWaiter(t)
	deliverExternalCaptcha(id, "tok-123", nil)
	<-done
	if err != nil || token != "tok-123" {
		t.Fatalf("got token=%q err=%v", token, err)
	}
}

func TestExternalCaptchaCancelAndUnknownID(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() {
		_, err := solveCaptchaExternally(ctx, "https://id.vk.ru/x", 0)
		done <- err
	}()
	id := waitForWaiter(t)
	deliverExternalCaptcha("no-such-id", "ignored", nil) // must not panic or unblock
	deliverExternalCaptcha(id, "", errCaptchaCancelled)
	if err := <-done; !errors.Is(err, errCaptchaCancelled) {
		t.Fatalf("want cancel error, got %v", err)
	}
}

func TestControlReaderShutsDownOnEOFAndCommand(t *testing.T) {
	for name, input := range map[string]string{
		"eof":      "garbage line\n",
		"shutdown": `{"cmd":"shutdown"}` + "\n" + `{"cmd":"never-read"}` + "\n",
	} {
		called := make(chan struct{}, 2)
		startControlReader(strings.NewReader(input), func() { called <- struct{}{} })
		select {
		case <-called:
		case <-time.After(2 * time.Second):
			t.Fatalf("%s: shutdown callback not called", name)
		}
	}
}

func TestCredsCacheRoundTrip(t *testing.T) {
	dir := t.TempDir()
	credsCachePath = filepath.Join(dir, "creds.bin")
	defer func() { credsCachePath = "" }()

	cache := getStreamCache(0)
	cache.mutex.Lock()
	cache.creds = TurnCredentials{Username: "u", Password: "secret-pass", ServerAddr: "1.2.3.4:3478",
		ExpiresAt: time.Now().Add(5 * time.Minute), Link: "abc"}
	cache.mutex.Unlock()
	saveCredsCache()

	raw, err := os.ReadFile(credsCachePath)
	if err != nil {
		t.Fatal(err)
	}
	if isWindowsBuild && bytes.Contains(raw, []byte("secret-pass")) {
		t.Fatal("credentials stored in plain text on Windows")
	}

	cache.invalidate(0)
	loadCredsCache()
	cache.mutex.RLock()
	got := cache.creds
	cache.mutex.RUnlock()
	if got.Password != "secret-pass" || got.Link != "abc" {
		t.Fatalf("restored creds mismatch: %+v", got)
	}
	cache.invalidate(0)
}

func waitForWaiter(t *testing.T) string {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		externalCaptchaMu.Lock()
		for id := range externalCaptchaWaiters {
			externalCaptchaMu.Unlock()
			return id
		}
		externalCaptchaMu.Unlock()
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatal("captcha waiter never registered")
	return ""
}
