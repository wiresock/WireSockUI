package main

// External captcha mode: instead of running the local reverse proxy on
// localhost:8765 and opening the system browser, the client hands the VK
// redirect_uri to the host application (captcha_required event) and waits for
// the success_token it sends back on stdin. The host shows the page in an
// embedded WebView2 and reads success_token from the
// captchaNotRobot.check response.

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"sync"
	"sync/atomic"
)

var (
	externalCaptcha     bool
	errCaptchaCancelled = errors.New("captcha cancelled by user")

	externalCaptchaSeq     atomic.Int64
	externalCaptchaMu      sync.Mutex
	externalCaptchaWaiters = map[string]chan externalCaptchaResult{}
)

type externalCaptchaResult struct {
	token string
	err   error
}

func deliverExternalCaptcha(id, token string, err error) {
	externalCaptchaMu.Lock()
	ch, ok := externalCaptchaWaiters[id]
	if ok {
		delete(externalCaptchaWaiters, id)
	}
	externalCaptchaMu.Unlock()
	if !ok {
		return
	}
	if err == nil && token == "" {
		err = errors.New("host returned an empty captcha token")
	}
	ch <- externalCaptchaResult{token: token, err: err}
}

func solveCaptchaExternally(ctx context.Context, redirectURI string, streamID int) (string, error) {
	if redirectURI == "" {
		return "", fmt.Errorf("external captcha needs redirect_uri")
	}
	id := "c" + strconv.FormatInt(externalCaptchaSeq.Add(1), 10)
	ch := make(chan externalCaptchaResult, 1)

	externalCaptchaMu.Lock()
	externalCaptchaWaiters[id] = ch
	externalCaptchaMu.Unlock()
	defer func() {
		externalCaptchaMu.Lock()
		delete(externalCaptchaWaiters, id)
		externalCaptchaMu.Unlock()
	}()

	emitEvent("captcha_required", map[string]any{"id": id, "stream": streamID, "redirect_uri": redirectURI})

	select {
	case res := <-ch:
		if res.err != nil {
			emitEvent("captcha_failed", map[string]any{"id": id, "error": res.err.Error()})
			return "", res.err
		}
		emitEvent("captcha_solved", map[string]any{"id": id})
		return res.token, nil
	case <-ctx.Done():
		emitEvent("captcha_failed", map[string]any{"id": id, "error": "timeout"})
		return "", ctx.Err()
	}
}
