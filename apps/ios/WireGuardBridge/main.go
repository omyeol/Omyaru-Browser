package main

/*
#include <stdlib.h>
#include <stdint.h>
*/
import "C"

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"time"
	"unsafe"

	"golang.zx2c4.com/wireguard/conn"
	"golang.zx2c4.com/wireguard/device"
	"golang.zx2c4.com/wireguard/tun/netstack"
)

type wgPeer struct {
	publicKey           string
	presharedKey        string
	endpoint            string
	allowedIPs          []string
	persistentKeepalive int
}

type wgConfig struct {
	privateKey string
	addresses  []netip.Addr
	dnsServers []netip.Addr
	mtu        int
	peers      []wgPeer
}

type wgTunnel struct {
	dev      *device.Device
	tnet     *netstack.Net
	listener net.Listener
	port     int32
	closed   chan struct{}
}

var (
	wgMu     sync.Mutex
	activeWG *wgTunnel
	lastErr  string
)

func setLastErr(msg string) {
	lastErr = msg
}

func decodeWGKeyToHex(b64 string) (string, error) {
	raw, err := base64.StdEncoding.DecodeString(strings.TrimSpace(b64))
	if err != nil {
		return "", err
	}
	if len(raw) != 32 {
		return "", fmt.Errorf("invalid key length %d", len(raw))
	}
	return hex.EncodeToString(raw), nil
}

func resolveEndpoint(raw string) (string, error) {
	host, port, err := net.SplitHostPort(strings.TrimSpace(raw))
	if err != nil {
		return "", fmt.Errorf("invalid endpoint %q: %w", raw, err)
	}
	if ip := net.ParseIP(host); ip != nil {
		return net.JoinHostPort(ip.String(), port), nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	ips, err := net.DefaultResolver.LookupIPAddr(ctx, host)
	if err != nil || len(ips) == 0 {
		return "", fmt.Errorf("failed to resolve endpoint %q: %w", host, err)
	}
	// Prefer IPv4 first for maximum mobile carrier compatibility
	for _, ipa := range ips {
		if v4 := ipa.IP.To4(); v4 != nil {
			return net.JoinHostPort(v4.String(), port), nil
		}
	}
	return net.JoinHostPort(ips[0].IP.String(), port), nil
}

func parseWireGuardConf(text string) (*wgConfig, error) {
	if len(text) > 64*1024 {
		return nil, errors.New("config file exceeds 64KB limit")
	}
	cfg := &wgConfig{
		mtu: 1280,
	}
	section := ""
	var curPeer *wgPeer

	flushPeer := func() {
		if curPeer != nil {
			if len(curPeer.allowedIPs) == 0 {
				curPeer.allowedIPs = []string{"0.0.0.0/0", "::/0"}
			}
			cfg.peers = append(cfg.peers, *curPeer)
			curPeer = nil
		}
	}

	for _, rawLine := range strings.Split(text, "\n") {
		line := strings.TrimSpace(rawLine)
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, ";") {
			continue
		}
		if idx := strings.Index(line, "#"); idx >= 0 {
			line = strings.TrimSpace(line[:idx])
		}
		if strings.HasPrefix(line, "[") && strings.HasSuffix(line, "]") {
			flushPeer()
			section = strings.ToLower(strings.TrimSpace(line[1 : len(line)-1]))
			if section == "peer" {
				curPeer = &wgPeer{}
			}
			continue
		}
		eq := strings.Index(line, "=")
		if eq < 0 {
			continue
		}
		key := strings.ToLower(strings.TrimSpace(line[:eq]))
		val := strings.TrimSpace(line[eq+1:])

		switch key {
		case "preup", "postup", "predown", "postdown":
			return nil, errors.New("external shell hooks (PreUp/PostUp/PreDown/PostDown) are not allowed")
		}

		switch section {
		case "interface":
			switch key {
			case "privatekey":
				hexKey, err := decodeWGKeyToHex(val)
				if err != nil {
					return nil, fmt.Errorf("invalid PrivateKey: %w", err)
				}
				cfg.privateKey = hexKey
			case "address":
				for _, item := range strings.Split(val, ",") {
					s := strings.TrimSpace(item)
					if s == "" {
						continue
					}
					if slash := strings.Index(s, "/"); slash >= 0 {
						s = strings.TrimSpace(s[:slash])
					}
					addr, err := netip.ParseAddr(s)
					if err != nil {
						return nil, fmt.Errorf("invalid Interface Address %q: %w", item, err)
					}
					cfg.addresses = append(cfg.addresses, addr)
				}
			case "dns":
				for _, item := range strings.Split(val, ",") {
					s := strings.TrimSpace(item)
					if s == "" {
						continue
					}
					if addr, err := netip.ParseAddr(s); err == nil {
						cfg.dnsServers = append(cfg.dnsServers, addr)
					}
				}
			case "mtu":
				if m, err := strconv.Atoi(val); err == nil && m >= 1200 && m <= 1500 {
					cfg.mtu = m
				}
			}
		case "peer":
			if curPeer == nil {
				continue
			}
			switch key {
			case "publickey":
				hexKey, err := decodeWGKeyToHex(val)
				if err != nil {
					return nil, fmt.Errorf("invalid Peer PublicKey: %w", err)
				}
				curPeer.publicKey = hexKey
			case "presharedkey":
				if val != "" {
					hexKey, err := decodeWGKeyToHex(val)
					if err != nil {
						return nil, fmt.Errorf("invalid Peer PresharedKey: %w", err)
					}
					curPeer.presharedKey = hexKey
				}
			case "endpoint":
				curPeer.endpoint = val
			case "allowedips":
				for _, item := range strings.Split(val, ",") {
					s := strings.TrimSpace(item)
					if s == "" {
						continue
					}
					if _, err := netip.ParsePrefix(s); err == nil {
						curPeer.allowedIPs = append(curPeer.allowedIPs, s)
					}
				}
			case "persistentkeepalive":
				if n, err := strconv.Atoi(val); err == nil && n >= 0 {
					curPeer.persistentKeepalive = n
				}
			}
		}
	}
	flushPeer()

	if cfg.privateKey == "" {
		return nil, errors.New("missing [Interface] PrivateKey")
	}
	if len(cfg.addresses) == 0 {
		return nil, errors.New("missing [Interface] Address")
	}
	if len(cfg.dnsServers) == 0 {
		cfg.dnsServers = []netip.Addr{netip.MustParseAddr("1.1.1.1")}
	}
	if len(cfg.peers) == 0 {
		return nil, errors.New("missing [Peer] section")
	}
	for i := range cfg.peers {
		if cfg.peers[i].publicKey == "" {
			return nil, errors.New("missing [Peer] PublicKey")
		}
		if cfg.peers[i].endpoint == "" {
			return nil, errors.New("missing [Peer] Endpoint")
		}
		resolved, err := resolveEndpoint(cfg.peers[i].endpoint)
		if err != nil {
			return nil, err
		}
		cfg.peers[i].endpoint = resolved
	}
	return cfg, nil
}

func buildUAPIConfig(cfg *wgConfig) string {
	var b strings.Builder
	b.WriteString("private_key=")
	b.WriteString(cfg.privateKey)
	b.WriteByte('\n')
	for _, p := range cfg.peers {
		b.WriteString("public_key=")
		b.WriteString(p.publicKey)
		b.WriteByte('\n')
		if p.presharedKey != "" {
			b.WriteString("preshared_key=")
			b.WriteString(p.presharedKey)
			b.WriteByte('\n')
		}
		b.WriteString("endpoint=")
		b.WriteString(p.endpoint)
		b.WriteByte('\n')
		for _, cidr := range p.allowedIPs {
			b.WriteString("allowed_ip=")
			b.WriteString(cidr)
			b.WriteByte('\n')
		}
		if p.persistentKeepalive > 0 {
			b.WriteString("persistent_keepalive_interval=")
			b.WriteString(strconv.Itoa(p.persistentKeepalive))
			b.WriteByte('\n')
		}
	}
	return b.String()
}

func handleSOCKS5Client(client net.Conn, tnet *netstack.Net) {
	defer client.Close()
	_ = client.SetDeadline(time.Now().Add(15 * time.Second))

	// 1. SOCKS5 Greeting: [VER, NMETHODS, METHODS...]
	var hdr [2]byte
	if _, err := io.ReadFull(client, hdr[:]); err != nil {
		return
	}
	if hdr[0] != 0x05 {
		return
	}
	nMethods := int(hdr[1])
	if nMethods > 0 {
		methods := make([]byte, nMethods)
		if _, err := io.ReadFull(client, methods); err != nil {
			return
		}
	}
	if _, err := client.Write([]byte{0x05, 0x00}); err != nil {
		return
	}

	// 2. SOCKS5 Request: [VER, CMD, RSV, ATYP, DST.ADDR, DST.PORT]
	var req [4]byte
	if _, err := io.ReadFull(client, req[:]); err != nil {
		return
	}
	if req[0] != 0x05 || req[1] != 0x01 {
		_, _ = client.Write([]byte{0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	var host string
	switch req[3] {
	case 0x01: // IPv4
		var b [4]byte
		if _, err := io.ReadFull(client, b[:]); err != nil {
			return
		}
		host = net.IP(b[:]).String()
	case 0x03: // Domain name (resolved inside WireGuard netstack tunnel!)
		var l [1]byte
		if _, err := io.ReadFull(client, l[:]); err != nil {
			return
		}
		name := make([]byte, int(l[0]))
		if _, err := io.ReadFull(client, name); err != nil {
			return
		}
		host = string(name)
	case 0x04: // IPv6
		var b [16]byte
		if _, err := io.ReadFull(client, b[:]); err != nil {
			return
		}
		host = net.IP(b[:]).String()
	default:
		_, _ = client.Write([]byte{0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	var portBuf [2]byte
	if _, err := io.ReadFull(client, portBuf[:]); err != nil {
		return
	}
	port := binary.BigEndian.Uint16(portBuf[:])
	target := net.JoinHostPort(host, strconv.Itoa(int(port)))

	// Clear handshake deadline before dialing & proxying
	_ = client.SetDeadline(time.Time{})

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	remote, err := tnet.DialContext(ctx, "tcp", target)
	cancel()
	if err != nil {
		_, _ = client.Write([]byte{0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}
	defer remote.Close()

	if _, err := client.Write([]byte{0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0}); err != nil {
		return
	}

	done := make(chan struct{}, 2)
	go func() {
		_, _ = io.Copy(remote, client)
		done <- struct{}{}
	}()
	go func() {
		_, _ = io.Copy(client, remote)
		done <- struct{}{}
	}()
	<-done
}

func stopActiveLocked() {
	if activeWG == nil {
		return
	}
	t := activeWG
	activeWG = nil
	close(t.closed)
	_ = t.listener.Close()
	t.dev.Close()
}

//export omyaru_wg_start
func omyaru_wg_start(configText *C.char) C.int32_t {
	if configText == nil {
		wgMu.Lock()
		setLastErr("empty config")
		wgMu.Unlock()
		return -1
	}
	raw := C.GoString(configText)

	// Parse & resolve outside lock so UI never stalls
	cfg, err := parseWireGuardConf(raw)
	if err != nil {
		wgMu.Lock()
		setLastErr(err.Error())
		wgMu.Unlock()
		return -1
	}

	wgMu.Lock()
	defer wgMu.Unlock()
	stopActiveLocked()

	tunDev, tnet, err := netstack.CreateNetTUN(cfg.addresses, cfg.dnsServers, cfg.mtu)
	if err != nil {
		setLastErr("netstack init failed: " + err.Error())
		return -1
	}

	dev := device.NewDevice(tunDev, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	if err := dev.IpcSet(buildUAPIConfig(cfg)); err != nil {
		dev.Close()
		setLastErr("wireguard config rejected: " + err.Error())
		return -1
	}
	if err := dev.Up(); err != nil {
		dev.Close()
		setLastErr("wireguard up failed: " + err.Error())
		return -1
	}

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		dev.Close()
		setLastErr("local proxy bind failed: " + err.Error())
		return -1
	}
	tcpAddr, ok := ln.Addr().(*net.TCPAddr)
	if !ok || tcpAddr.Port <= 0 {
		_ = ln.Close()
		dev.Close()
		setLastErr("invalid local proxy port")
		return -1
	}

	t := &wgTunnel{
		dev:      dev,
		tnet:     tnet,
		listener: ln,
		port:     int32(tcpAddr.Port),
		closed:   make(chan struct{}),
	}
	activeWG = t
	setLastErr("")

	go func(tunnel *wgTunnel) {
		for {
			c, err := tunnel.listener.Accept()
			if err != nil {
				select {
				case <-tunnel.closed:
					return
				default:
					time.Sleep(15 * time.Millisecond)
					continue
				}
			}
			go handleSOCKS5Client(c, tunnel.tnet)
		}
	}(t)

	return C.int32_t(t.port)
}

//export omyaru_wg_stop
func omyaru_wg_stop() {
	wgMu.Lock()
	defer wgMu.Unlock()
	stopActiveLocked()
}

//export omyaru_wg_port
func omyaru_wg_port() C.int32_t {
	wgMu.Lock()
	defer wgMu.Unlock()
	if activeWG == nil {
		return 0
	}
	return C.int32_t(activeWG.port)
}

//export omyaru_wg_last_error
func omyaru_wg_last_error() *C.char {
	wgMu.Lock()
	msg := lastErr
	wgMu.Unlock()
	return C.CString(msg)
}

//export omyaru_wg_stats_json
func omyaru_wg_stats_json() *C.char {
	wgMu.Lock()
	t := activeWG
	wgMu.Unlock()

	type wgStats struct {
		Running          bool   `json:"running"`
		Port             int32  `json:"port"`
		RxBytes          uint64 `json:"rxBytes"`
		TxBytes          uint64 `json:"txBytes"`
		LastHandshakeSec int64  `json:"lastHandshakeSec"`
	}
	st := wgStats{}
	if t != nil {
		st.Running = true
		st.Port = t.port
		if raw, err := t.dev.IpcGet(); err == nil {
			for _, line := range strings.Split(raw, "\n") {
				eq := strings.Index(line, "=")
				if eq < 0 {
					continue
				}
				k := line[:eq]
				v := strings.TrimSpace(line[eq+1:])
				switch k {
				case "rx_bytes":
					if n, e := strconv.ParseUint(v, 10, 64); e == nil {
						st.RxBytes += n
					}
				case "tx_bytes":
					if n, e := strconv.ParseUint(v, 10, 64); e == nil {
						st.TxBytes += n
					}
				case "last_handshake_time_sec":
					if n, e := strconv.ParseInt(v, 10, 64); e == nil && n > st.LastHandshakeSec {
						st.LastHandshakeSec = n
					}
				}
			}
		}
	}
	b, _ := json.Marshal(st)
	return C.CString(string(b))
}

//export omyaru_wg_free_string
func omyaru_wg_free_string(s *C.char) {
	if s != nil {
		C.free(unsafe.Pointer(s))
	}
}

func main() {}
