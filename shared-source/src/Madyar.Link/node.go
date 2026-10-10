package main

import (
	"bufio"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	libp2p "github.com/libp2p/go-libp2p"
	dht "github.com/libp2p/go-libp2p-kad-dht"
	"github.com/libp2p/go-libp2p/core/crypto"
	"github.com/libp2p/go-libp2p/core/discovery"
	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/protocol"
	routing "github.com/libp2p/go-libp2p/p2p/discovery/routing"
	"github.com/libp2p/go-libp2p/p2p/host/autorelay"
	connmgr "github.com/libp2p/go-libp2p/p2p/net/connmgr"
	tcp "github.com/libp2p/go-libp2p/p2p/transport/tcp"
)

const messageProtocol protocol.ID = "/madyar/message/1"
const healthProtocol protocol.ID = "/madyar/health/1"
const relayHop protocol.ID = "/libp2p/circuit/relay/0.2.0/hop"
const maxFrame = 64 * 1024
const maxContacts = 64
const streamTimeout = 25 * time.Second

type event struct {
	Invite    string   `json:"invite,omitempty"`
	Op        string   `json:"op"`
	Peer      string   `json:"peer,omitempty"`
	Addresses []string `json:"addresses,omitempty"`
	ID        string   `json:"id,omitempty"`
	Kind      string   `json:"kind,omitempty"`
	Text      string   `json:"text,omitempty"`
	Receipt   string   `json:"receipt,omitempty"`
	Online    bool     `json:"online"`
	Delivered bool     `json:"delivered"`
	Detail    string   `json:"detail,omitempty"`
}
type envelope struct {
	Version  int    `json:"version"`
	ID       string `json:"id"`
	To       string `json:"to,omitempty"`
	Kind     string `json:"kind,omitempty"`
	Text     string `json:"text,omitempty"`
	Accepted bool   `json:"accepted,omitempty"`
}
type pendingReceipt struct {
	invite string // Empty means ordinary contact-bound receipt.
	peer   peer.ID
	result chan bool
}
type node struct {
	invites          map[string]time.Time
	introSends       chan struct{}
	discoveryRefresh chan struct{}
	h                host.Host
	dht              *dht.IpfsDHT
	ctx              context.Context
	cancel           context.CancelFunc
	mu               sync.Mutex
	closed           bool
	contacts         map[peer.ID]bool
	pending          map[string]pendingReceipt
	online           map[peer.ID]bool
	emit             func(event)
	wg               sync.WaitGroup
	slots            chan struct{}
	refresh          chan struct{}
	ready            chan struct{}
	closeOnce        sync.Once
	keyBytes         []byte
}

func newNode(parent context.Context, seed []byte, contacts []string, internet bool, emit func(event)) (*node, error) {
	if len(seed) != ed25519.SeedSize {
		return nil, errors.New("invalid_identity")
	}
	private := ed25519.NewKeyFromSeed(seed)
	key, err := crypto.UnmarshalEd25519PrivateKey(private)
	if err != nil {
		return nil, errors.New("invalid_identity")
	}
	ctx, cancel := context.WithCancel(parent)
	n := &node{invites: map[string]time.Time{}, introSends: make(chan struct{}, 8), discoveryRefresh: make(chan struct{}, 1), ctx: ctx, cancel: cancel, emit: emit, contacts: map[peer.ID]bool{}, pending: map[string]pendingReceipt{}, online: map[peer.ID]bool{}, slots: make(chan struct{}, 8), refresh: make(chan struct{}, 1), ready: make(chan struct{}), keyBytes: private}
	if err = n.SetContacts(contacts); err != nil {
		cancel()
		return nil, err
	}
	cm, err := connmgr.NewConnManager(32, 64, connmgr.WithGracePeriod(time.Minute))
	if err != nil {
		cancel()
		return nil, err
	}
	options := []libp2p.Option{libp2p.Identity(key), libp2p.NoTransports, libp2p.Transport(tcp.NewTCPTransport), libp2p.ConnectionManager(cm), libp2p.DisableMetrics()}
	if internet {
		options = append(options, libp2p.ListenAddrStrings("/ip4/0.0.0.0/tcp/0", "/ip6/::/tcp/0"), libp2p.EnableHolePunching(), libp2p.EnableAutoRelayWithPeerSource(n.relayCandidates, autorelay.WithMinCandidates(1), autorelay.WithMaxCandidates(8), autorelay.WithNumRelays(2), autorelay.WithBootDelay(5*time.Second), autorelay.WithMinInterval(30*time.Second)))
	} else {
		options = append(options, libp2p.ListenAddrStrings("/ip4/127.0.0.1/tcp/0"))
	}
	n.h, err = libp2p.New(options...)
	close(n.ready)
	if err != nil {
		cancel()
		cm.Close()
		return nil, errors.New("host_start_failed")
	}
	if n.contacts[n.h.ID()] {
		n.h.Close()
		cancel()
		return nil, errors.New("invalid_contacts")
	}
	n.h.SetStreamHandler(messageProtocol, n.receive)
	n.h.SetStreamHandler(healthProtocol, n.health)
	n.h.SetStreamHandler(introductionProtocol, n.receiveIntroduction)
	if internet {
		n.dht, err = newClientDHT(n.h)
		if err != nil {
			n.h.Close()
			cancel()
			return nil, errors.New("discovery_start_failed")
		}
		n.wg.Add(1)
		go n.discoveryLoop()
	}
	n.wg.Add(1)
	go n.presenceLoop()
	return n, nil
}
func (n *node) SetContacts(ids []string) error {
	if len(ids) > maxContacts {
		return errors.New("too_many_contacts")
	}
	contacts := map[peer.ID]bool{}
	for _, id := range ids {
		p, err := peer.Decode(id)
		if err != nil {
			return errors.New("invalid_contacts")
		}
		contacts[p] = true
	}
	n.mu.Lock()
	defer n.mu.Unlock()
	if n.closed {
		return errors.New("disabled")
	}
	if n.h != nil && contacts[n.h.ID()] {
		return errors.New("invalid_contacts")
	}
	previous := n.contacts
	n.contacts = contacts
	for token, pending := range n.pending {
		if pending.invite == "" && !contacts[pending.peer] {
			select {
			case pending.result <- false:
			default:
			}
			delete(n.pending, token)
		}
	}
	if n.h != nil {
		for p := range previous {
			if !contacts[p] {
				delete(n.online, p)
				hasIntroduction := false
				for _, pending := range n.pending {
					if pending.peer == p && pending.invite != "" {
						hasIntroduction = true
						break
					}
				}
				if !hasIntroduction {
					n.h.Network().ClosePeer(p)
				}
			}
		}
	}
	select {
	case n.refresh <- struct{}{}:
	default:
	}
	select {
	case n.discoveryRefresh <- struct{}{}:
	default:
	}
	return nil
}
func (n *node) contact(p peer.ID) bool {
	n.mu.Lock()
	defer n.mu.Unlock()
	return !n.closed && n.contacts[p]
}
func (n *node) begin() bool {
	n.mu.Lock()
	defer n.mu.Unlock()
	if n.closed {
		return false
	}
	n.wg.Add(1)
	return true
}
func validEnvelope(e envelope) error {
	if e.Version != 1 {
		return errors.New("unsupported_version")
	}
	u, err := uuid.Parse(e.ID)
	if err != nil || u == uuid.Nil || len(e.ID) != 36 {
		return errors.New("invalid_id")
	}
	if !utf8.ValidString(e.Text) {
		return errors.New("invalid_text")
	}
	count := utf8.RuneCountInString(e.Text)
	switch e.Kind {
	case "text":
		if count == 0 || count > 2000 {
			return errors.New("message_too_large")
		}
	case "server":
		if count == 0 || count > 32768 {
			return errors.New("message_too_large")
		}
	default:
		return errors.New("unsupported_kind")
	}
	raw, err := json.Marshal(e)
	if err != nil || len(raw)+1 > maxFrame {
		return errors.New("frame_too_large")
	}
	return nil
}
func writeFrame(w io.Writer, v any) error {
	raw, err := json.Marshal(v)
	if err != nil {
		return err
	}
	if len(raw)+1 > maxFrame {
		return errors.New("frame_too_large")
	}
	raw = append(raw, '\n')
	_, err = w.Write(raw)
	return err
}
func readFrame(r io.Reader, v any) error {
	reader := bufio.NewReaderSize(io.LimitReader(r, maxFrame+1), 4096)
	var raw []byte
	for {
		part, prefix, err := reader.ReadLine()
		if err != nil {
			return err
		}
		raw = append(raw, part...)
		if len(raw) >= maxFrame {
			return errors.New("frame_too_large")
		}
		if !prefix {
			break
		}
	}
	decoder := json.NewDecoder(strings.NewReader(string(raw)))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(v); err != nil {
		return err
	}
	var extra any
	if err := decoder.Decode(&extra); err != io.EOF {
		return errors.New("invalid_frame")
	}
	return nil
}
func (n *node) Send(id, to, kind, text string) (err error) {
	defer func() {
		detail := "acknowledged"
		if err != nil {
			detail = err.Error()
		}
		n.emit(event{Op: "sent", Peer: to, ID: id, Delivered: err == nil, Detail: detail})
	}()
	recipient, decodeErr := peer.Decode(to)
	if decodeErr != nil || !n.contact(recipient) {
		return errors.New("unknown_contact")
	}
	e := envelope{Version: 1, ID: id, To: to, Kind: kind, Text: text}
	if err = validEnvelope(e); err != nil {
		return err
	}
	if !n.begin() {
		return errors.New("disabled")
	}
	defer n.wg.Done()
	ctx, cancel := context.WithTimeout(n.ctx, streamTimeout)
	defer cancel()
	s, openErr := n.h.NewStream(network.WithAllowLimitedConn(ctx, "madyar encrypted message"), recipient, messageProtocol)
	if openErr != nil {
		return errors.New("route_unavailable")
	}
	defer s.Close()
	s.SetDeadline(time.Now().Add(streamTimeout))
	if s.Conn().RemotePeer() != recipient || !n.contact(recipient) {
		s.Reset()
		return errors.New("peer_rejected")
	}
	if writeFrame(s, e) != nil {
		s.Reset()
		return errors.New("send_failed")
	}
	var ack envelope
	if readFrame(s, &ack) != nil {
		return errors.New("ack_missing")
	}
	if ack.Version != 1 || ack.ID != id || !ack.Accepted || ack.To != n.h.ID().String() || ack.Kind != "" || ack.Text != "" || !n.contact(recipient) {
		return errors.New("ack_rejected")
	}
	return nil
}
func (n *node) receive(s network.Stream) {
	if !n.begin() {
		s.Reset()
		return
	}
	defer n.wg.Done()
	defer s.Close()
	select {
	case n.slots <- struct{}{}:
		defer func() { <-n.slots }()
	default:
		s.Reset()
		return
	}
	sender := s.Conn().RemotePeer()
	if !n.contact(sender) {
		s.Reset()
		return
	}
	s.SetDeadline(time.Now().Add(streamTimeout))
	var e envelope
	if readFrame(s, &e) != nil || validEnvelope(e) != nil || e.Accepted || e.To != n.h.ID().String() {
		s.Reset()
		return
	}
	bytes := make([]byte, 24)
	if _, err := rand.Read(bytes); err != nil {
		s.Reset()
		return
	}
	token := hex.EncodeToString(bytes)
	result := make(chan bool, 1)
	n.mu.Lock()
	if n.closed || !n.contacts[sender] {
		n.mu.Unlock()
		s.Reset()
		return
	}
	n.pending[token] = pendingReceipt{peer: sender, result: result}
	n.mu.Unlock()
	defer func() { n.mu.Lock(); delete(n.pending, token); n.mu.Unlock() }()
	n.emit(event{Op: "received", Receipt: token, Peer: sender.String(), ID: e.ID, Kind: e.Kind, Text: e.Text})
	timer := time.NewTimer(streamTimeout)
	defer timer.Stop()
	select {
	case accepted := <-result:
		if accepted && n.contact(sender) {
			writeFrame(s, envelope{Version: 1, ID: e.ID, To: sender.String(), Accepted: true})
		} else {
			s.Reset()
		}
	case <-timer.C:
		s.Reset()
	case <-n.ctx.Done():
		s.Reset()
	}
}
func (n *node) Accept(token string, accepted bool) {
	n.mu.Lock()
	defer n.mu.Unlock()
	if p, ok := n.pending[token]; ok {
		delete(n.pending, token)
		if n.closed || (p.invite == "" && !n.contacts[p.peer]) || (p.invite != "" && !n.activeInviteLocked(p.invite, time.Now())) {
			accepted = false
		}
		select {
		case p.result <- accepted:
		default:
		}
	}
}
func (n *node) health(s network.Stream) {
	if !n.begin() {
		s.Reset()
		return
	}
	defer n.wg.Done()
	defer s.Close()
	s.SetDeadline(time.Now().Add(5 * time.Second))
	if !n.contact(s.Conn().RemotePeer()) {
		s.Reset()
		return
	}
	writeFrame(s, struct {
		Version int `json:"version"`
	}{1})
}
func (n *node) peers() []peer.ID {
	n.mu.Lock()
	defer n.mu.Unlock()
	ids := make([]peer.ID, 0, len(n.contacts))
	for p := range n.contacts {
		ids = append(ids, p)
	}
	return ids
}
func (n *node) presenceLoop() {
	defer n.wg.Done()
	ticker := time.NewTicker(30 * time.Second)
	defer ticker.Stop()
	for {
		n.probeContacts()
		select {
		case <-n.ctx.Done():
			return
		case <-ticker.C:
		case <-n.refresh:
		}
	}
}
func (n *node) probeContacts() {
	peers := n.peers()
	var wg sync.WaitGroup
	slots := make(chan struct{}, 4)
	for _, p := range peers {
		select {
		case slots <- struct{}{}:
		case <-n.ctx.Done():
			wg.Wait()
			return
		}
		wg.Add(1)
		go func(p peer.ID) {
			defer wg.Done()
			defer func() { <-slots }()
			ctx, cancel := context.WithTimeout(n.ctx, 5*time.Second)
			defer cancel()
			online := false
			s, err := n.h.NewStream(network.WithAllowLimitedConn(ctx, "madyar contact health"), p, healthProtocol)
			if err == nil {
				s.SetDeadline(time.Now().Add(5 * time.Second))
				var response struct {
					Version int `json:"version"`
				}
				online = readFrame(s, &response) == nil && response.Version == 1 && s.Conn().RemotePeer() == p
				s.Close()
			}
			n.mu.Lock()
			changed := n.online[p] != online
			valid := !n.closed && n.contacts[p]
			if valid {
				n.online[p] = online
			}
			n.mu.Unlock()
			if valid && changed {
				n.emit(event{Op: "peer", Peer: p.String(), Online: online})
			}
		}(p)
	}
	wg.Wait()
}
func pairNamespace(a, b peer.ID) string {
	ids := []string{a.String(), b.String()}
	sort.Strings(ids)
	sum := sha256.Sum256([]byte("madyar/pair/1\x00" + ids[0] + "\x00" + ids[1]))
	return "madyar/pair/1/" + hex.EncodeToString(sum[:])
}
func (n *node) discoveryLoop() {
	defer n.wg.Done()
	boot := dht.GetDefaultBootstrapPeerAddrInfos()
	var wg sync.WaitGroup
	for _, info := range boot {
		wg.Add(1)
		go func(info peer.AddrInfo) {
			defer wg.Done()
			ctx, cancel := context.WithTimeout(n.ctx, 12*time.Second)
			defer cancel()
			n.h.Connect(ctx, info)
		}(info)
	}
	wg.Wait()
	if n.ctx.Err() != nil {
		return
	}
	n.dht.Bootstrap(n.ctx)
	n.emit(event{Op: "state", Detail: "discovery_started"})
	router := routing.NewRoutingDiscovery(n.dht)
	advertised := map[peer.ID]time.Time{}
	advertisedInvites := map[string]time.Time{}
	ticker := time.NewTicker(30 * time.Second)
	defer ticker.Stop()
	for {
		n.advertiseInvites(router, advertisedInvites)
		peers := n.peers()
		slots := make(chan struct{}, 4)
		published := make(chan peer.ID, len(peers))
		for _, p := range peers {
			select {
			case slots <- struct{}{}:
			case <-n.ctx.Done():
				wg.Wait()
				return
			}
			wg.Add(1)
			publish := time.Since(advertised[p]) >= 30*time.Minute
			go func(p peer.ID, publish bool) {
				defer wg.Done()
				defer func() { <-slots }()
				ns := pairNamespace(n.h.ID(), p)
				if publish {
					ctx, cancel := context.WithTimeout(n.ctx, 20*time.Second)
					_, err := router.Advertise(ctx, ns, discovery.TTL(time.Hour))
					cancel()
					if err != nil {
						n.emit(event{Op: "state", Detail: "advertise_unavailable"})
					} else {
						published <- p
					}
				}
				ctx, cancel := context.WithTimeout(n.ctx, 20*time.Second)
				defer cancel()
				if !n.contact(p) {
					return
				}
				found, err := router.FindPeers(ctx, ns, discovery.Limit(4))
				if err != nil {
					return
				}
				for info := range found {
					if info.ID == p && n.contact(p) {
						n.h.Connect(ctx, info)
					}
				}
			}(p, publish)
		}
		wg.Wait()
		close(published)
		for p := range published {
			advertised[p] = time.Now()
		}
		for p := range advertised {
			if !n.contact(p) {
				delete(advertised, p)
			}
		}
		select {
		case <-n.ctx.Done():
			return
		case <-ticker.C:
		case <-n.discoveryRefresh:
		}
	}
}
func (n *node) relayCandidates(ctx context.Context, count int) <-chan peer.AddrInfo {
	out := make(chan peer.AddrInfo)
	go func() {
		defer close(out)
		select {
		case <-n.ready:
		case <-ctx.Done():
			return
		}
		if n.h == nil {
			return
		}
		provided := 0
		peers := n.h.Peerstore().Peers()
		if len(peers) > 128 {
			peers = peers[:128]
		}
		for _, p := range peers {
			if p == n.h.ID() {
				continue
			}
			supported, _ := n.h.Peerstore().SupportsProtocols(p, relayHop)
			if len(supported) == 0 {
				continue
			}
			info := n.h.Peerstore().PeerInfo(p)
			if len(info.Addrs) == 0 {
				continue
			}
			select {
			case out <- info:
				provided++
			case <-ctx.Done():
				return
			}
			if provided >= count {
				return
			}
		}
	}()
	return out
}
func (n *node) Close() {
	n.closeOnce.Do(func() {
		n.mu.Lock()
		n.closed = true
		n.mu.Unlock()
		n.cancel()
		if n.dht != nil {
			n.dht.Close()
		}
		n.h.Close()
		n.wg.Wait()
		clear(n.keyBytes)
	})
}

func newClientDHT(h host.Host) (*dht.IpfsDHT, error) {
	return dht.New(h, dht.Mode(dht.ModeClient), dht.BootstrapPeers(dht.GetDefaultBootstrapPeerAddrInfos()...))
}
