// ProJict's Tailscale bridge (#a0d11f05): Tailscale's tsnet runs inside ProJict (userspace networking: no VPN adapter,
// no Tailscale app), and every connection to this node's port on your tailnet is passed, byte for byte, to ProJict's
// API on this computer. ProJict's TLS (your phone pins its certificate) and its tokens go through untouched; the
// bridge never sees what's inside.
//
// Built into projict-tailscale.dll by build.ps1 here (go build -buildmode=c-shared), which ProJict loads.
package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"path/filepath"
	"sync"
	"time"
	"unsafe"

	"tailscale.com/tsnet"
)

var (
	mu      sync.Mutex
	srv     *tsnet.Server
	ln      net.Listener
	logFile *os.File
)

func reply(v any) *C.char {
	b, _ := json.Marshal(v)
	return C.CString(string(b))
}

func failed(err error) *C.char { return reply(map[string]any{"error": err.Error()}) }

// ProJictTsStart starts the node, kept in dir (its identity survives restarts), named hostname on the tailnet. An
// empty authKey signs in through the browser (Status gives the address); a key joins at once. Connections to
// listenPort on the tailnet go to ProJict's API at targetPort on this computer. An ephemeral node (a test's) leaves the
// tailnet by itself once it's gone.
//
//export ProJictTsStart
func ProJictTsStart(dir, hostname, authKey *C.char, listenPort, targetPort, ephemeral C.int) *C.char {
	mu.Lock()
	defer mu.Unlock()
	if srv != nil {
		return reply(map[string]any{"error": "already started"})
	}
	d := C.GoString(dir)
	if err := os.MkdirAll(d, 0o700); err != nil {
		return failed(err)
	}
	// Nothing goes to Tailscale's log servers: ProJict keeps its own log, in the node's folder.
	os.Setenv("TS_NO_LOGS_NO_SUPPORT", "true")
	f, err := os.OpenFile(filepath.Join(d, "tailscale.log"), os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
	if err != nil {
		return failed(err)
	}
	logger := log.New(f, "", log.LstdFlags)
	s := &tsnet.Server{
		Dir:       d,
		Hostname:  C.GoString(hostname),
		AuthKey:   C.GoString(authKey),
		Ephemeral: ephemeral != 0,
		Logf:      logger.Printf,
		UserLogf:  logger.Printf,
	}
	if err := s.Start(); err != nil {
		f.Close()
		return failed(err)
	}
	l, err := s.Listen("tcp", fmt.Sprintf(":%d", int(listenPort)))
	if err != nil {
		s.Close()
		f.Close()
		return failed(err)
	}
	srv, ln, logFile = s, l, f
	go serve(l, int(targetPort), logger)
	return reply(map[string]any{"ok": true})
}

// The bridge reaches ProJict from 127.0.0.2, so ProJict tells its connections from local agents' (127.0.0.1): wrong
// tokens over the tailnet don't lock out agents on this computer, nor the other way round.
var bridgeAddr = &net.TCPAddr{IP: net.IPv4(127, 0, 0, 2)}

func serve(l net.Listener, targetPort int, logger *log.Logger) {
	target := fmt.Sprintf("127.0.0.1:%d", targetPort)
	for {
		c, err := l.Accept()
		if err != nil {
			return
		}
		go func(c net.Conn) {
			defer c.Close()
			dialer := net.Dialer{Timeout: 5 * time.Second, LocalAddr: bridgeAddr}
			t, err := dialer.Dial("tcp", target)
			if err != nil {
				// Where 127.0.0.2 can't be used, from 127.0.0.1 as anything else on this computer.
				t, err = net.DialTimeout("tcp", target, 5*time.Second)
			}
			if err != nil {
				logger.Printf("bridge: can't reach ProJict at %s: %v", target, err)
				return
			}
			defer t.Close()
			done := make(chan struct{}, 2)
			go func() { io.Copy(t, c); done <- struct{}{} }()
			go func() { io.Copy(c, t); done <- struct{}{} }()
			<-done
		}(c)
	}
}

// ProJictTsStatus says where the node is: its state (NoState, NeedsLogin, Starting, Running...), the address to sign
// in at while it needs one, and its tailnet addresses and name once it's in.
//
//export ProJictTsStatus
func ProJictTsStatus() *C.char {
	mu.Lock()
	s := srv
	mu.Unlock()
	if s == nil {
		return reply(map[string]any{"state": "Stopped"})
	}
	lc, err := s.LocalClient()
	if err != nil {
		return failed(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	st, err := lc.StatusWithoutPeers(ctx)
	if err != nil {
		return failed(err)
	}
	out := map[string]any{"state": st.BackendState, "authUrl": st.AuthURL, "ips": st.TailscaleIPs}
	if st.Self != nil {
		out["dnsName"] = st.Self.DNSName
	}
	return reply(out)
}

// ProJictTsCheck connects to this node's own tailnet address at port, as a phone would, sends a line and reads what
// comes back (through the bridge, from whatever listens on ProJict's port). For tests.
//
//export ProJictTsCheck
func ProJictTsCheck(port C.int) *C.char {
	mu.Lock()
	s := srv
	mu.Unlock()
	if s == nil {
		return reply(map[string]any{"error": "not started"})
	}
	ip4, _ := s.TailscaleIPs()
	if !ip4.IsValid() {
		return reply(map[string]any{"error": "no tailnet address yet"})
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	began := time.Now()
	c, err := s.Dial(ctx, "tcp", fmt.Sprintf("%s:%d", ip4, int(port)))
	if err != nil {
		return failed(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(10 * time.Second))
	if _, err := c.Write([]byte("projict-check\n")); err != nil {
		return failed(err)
	}
	buf := make([]byte, 64)
	n, err := c.Read(buf)
	if err != nil {
		return failed(err)
	}
	return reply(map[string]any{"reply": string(buf[:n]), "ms": time.Since(began).Milliseconds()})
}

// ProJictTsSignOut signs the node out of the tailnet (it needs a sign-in again) and stops it.
//
//export ProJictTsSignOut
func ProJictTsSignOut() *C.char {
	mu.Lock()
	s := srv
	mu.Unlock()
	if s == nil {
		return reply(map[string]any{"error": "not started"})
	}
	lc, err := s.LocalClient()
	if err != nil {
		return failed(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := lc.Logout(ctx); err != nil {
		return failed(err)
	}
	ProJictTsStop()
	return reply(map[string]any{"ok": true})
}

// ProJictTsStop takes the node off the tailnet for now (it keeps its identity in its folder for next time).
//
//export ProJictTsStop
func ProJictTsStop() {
	mu.Lock()
	defer mu.Unlock()
	if ln != nil {
		ln.Close()
	}
	if srv != nil {
		srv.Close()
	}
	if logFile != nil {
		logFile.Close()
	}
	srv, ln, logFile = nil, nil, nil
}

// ProJictTsFree frees a string the bridge returned.
//
//export ProJictTsFree
func ProJictTsFree(p *C.char) { C.free(unsafe.Pointer(p)) }

func main() {}
