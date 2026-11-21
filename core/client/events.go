package main

// Machine-readable control channel for a host application (the cronixWG UI).
//
// With -events-json the client writes one JSON object per line to stdout and
// reads one JSON command per line from stdin. Human-readable logs keep going
// to stderr through the standard log package, so the two never mix.
//
// Events (stdout):
//   {"event":"starting","version":"...","streams":8,"wrap":true,"udp":true}
//   {"event":"captcha_required","id":"c1","stream":0,"redirect_uri":"https://id.vk.ru/not_robot_captcha?..."}
//   {"event":"captcha_solved","id":"c1"}
//   {"event":"captcha_failed","id":"c1","error":"..."}
//   {"event":"turn_ip","ip":"95.142.0.1"}
//   {"event":"stream_up","stream":0,"connected":1}
//   {"event":"stream_down","stream":0,"connected":0}
//   {"event":"established","stream":0}           first DTLS session is ready; the WireGuard tunnel can start
//   {"event":"vk_backoff","seconds":40,"error":"..."}
//   {"event":"fatal","error":"..."}
//   {"event":"exit"}
//
// Commands (stdin):
//   {"cmd":"captcha_token","id":"c1","token":"<success_token>"}
//   {"cmd":"captcha_cancel","id":"c1"}
//   {"cmd":"shutdown"}
//
// When stdin is closed (the host died or closed the pipe) the client shuts
// down, so a crashed UI never leaves an orphaned proxy behind.

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"sync"
	"sync/atomic"
)

var (
	eventsEnabled   bool
	eventsMu        sync.Mutex
	establishedOnce atomic.Bool
)

func emitEvent(name string, fields map[string]any) {
	if !eventsEnabled {
		return
	}
	payload := make(map[string]any, len(fields)+1)
	for k, v := range fields {
		payload[k] = v
	}
	payload["event"] = name
	line, err := json.Marshal(payload)
	if err != nil {
		log.Printf("[Events] marshal %s: %v", name, err)
		return
	}
	eventsMu.Lock()
	defer eventsMu.Unlock()
	_, _ = os.Stdout.Write(append(line, '\n'))
}

// reportTurnIP announces the TURN server address. Without -events-json it
// keeps the original plain stdout line that routes.ps1 / routes.sh parse.
func reportTurnIP(ip net.IP) {
	if eventsEnabled {
		emitEvent("turn_ip", map[string]any{"ip": ip.String()})
		return
	}
	fmt.Println(ip)
}

type controlCommand struct {
	Cmd   string `json:"cmd"`
	ID    string `json:"id"`
	Token string `json:"token"`
}

// startControlReader consumes stdin commands until EOF. onShutdown is called
// once, either on an explicit shutdown command or when stdin closes.
func startControlReader(in io.Reader, onShutdown func()) {
	go func() {
		scanner := bufio.NewScanner(in)
		scanner.Buffer(make([]byte, 64*1024), 1024*1024)
		for scanner.Scan() {
			var cmd controlCommand
			if err := json.Unmarshal(scanner.Bytes(), &cmd); err != nil {
				log.Printf("[Control] ignoring malformed command: %v", err)
				continue
			}
			switch cmd.Cmd {
			case "captcha_token":
				deliverExternalCaptcha(cmd.ID, cmd.Token, nil)
			case "captcha_cancel":
				deliverExternalCaptcha(cmd.ID, "", errCaptchaCancelled)
			case "shutdown":
				log.Printf("[Control] shutdown requested by host")
				onShutdown()
				return
			default:
				log.Printf("[Control] unknown command %q", cmd.Cmd)
			}
		}
		log.Printf("[Control] stdin closed, shutting down")
		onShutdown()
	}()
}
