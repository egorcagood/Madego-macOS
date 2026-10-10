package main

import (
	"context"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"strings"
	"testing"
	"time"

	libp2p "github.com/libp2p/go-libp2p"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/peerstore"
	relayclient "github.com/libp2p/go-libp2p/p2p/protocol/circuitv2/client"
	relay "github.com/libp2p/go-libp2p/p2p/protocol/circuitv2/relay"
	ma "github.com/multiformats/go-multiaddr"
)

func testNode(t *testing.T) (*node, <-chan event) {
	t.Helper()
	seed := make([]byte, 32)
	rand.Read(seed)
	events := make(chan event, 64)
	n, err := newNode(context.Background(), seed, nil, false, func(e event) { events <- e })
	if err != nil {
		t.Fatalf("create real host: %v", err)
	}
	t.Cleanup(func() { n.Close() })
	return n, events
}
func awaitEvent(t *testing.T, events <-chan event, op string) event {
	t.Helper()
	timer := time.NewTimer(10 * time.Second)
	defer timer.Stop()
	for {
		select {
		case e := <-events:
			if e.Op == op {
				return e
			}
		case <-timer.C:
			t.Fatalf("missing %s", op)
		}
	}
}
func pair(t *testing.T) (*node, *node, <-chan event, <-chan event) {
	a, ae := testNode(t)
	b, be := testNode(t)
	a.SetContacts([]string{b.h.ID().String()})
	b.SetContacts([]string{a.h.ID().String()})
	return a, b, ae, be
}
func sendPair(t *testing.T, a, b *node, ae, be <-chan event) {
	t.Helper()
	done := make(chan error, 1)
	id := "035d904a-84aa-40c1-a13f-ff128a2e98d3"
	go func() { done <- a.Send(id, b.h.ID().String(), "text", "hello ☀") }()
	e := awaitEvent(t, be, "received")
	if e.Peer != a.h.ID().String() || e.Text != "hello ☀" || e.ID != id || e.Receipt == "" {
		t.Fatalf("bad authenticated receipt: %+v", e)
	}
	select {
	case err := <-done:
		t.Fatalf("send completed before durable ACK: %v", err)
	case <-time.After(150 * time.Millisecond):
	}
	b.Accept(e.Receipt, true)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	delivered := awaitEvent(t, ae, "sent")
	if !delivered.Delivered || delivered.ID != id {
		t.Fatalf("bad delivery: %+v", delivered)
	}
}
func TestDirectDurableACK(t *testing.T) {
	a, b, ae, be := pair(t)
	if err := a.h.Connect(a.ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()}); err != nil {
		t.Fatal(err)
	}
	sendPair(t, a, b, ae, be)
}
func TestCircuitRelayDurableACK(t *testing.T) {
	a, b, ae, be := pair(t)
	r, err := libp2p.New(libp2p.ListenAddrStrings("/ip4/127.0.0.1/tcp/0"))
	if err != nil {
		t.Fatal(err)
	}
	defer r.Close()
	service, err := relay.New(r)
	if err != nil {
		t.Fatal(err)
	}
	defer service.Close()
	info := peer.AddrInfo{ID: r.ID(), Addrs: r.Addrs()}
	if _, err = relayclient.Reserve(b.ctx, b.h, info); err != nil {
		t.Fatal(err)
	}
	addr := r.Addrs()[0].Encapsulate(ma.StringCast("/p2p/" + r.ID().String() + "/p2p-circuit"))
	a.h.Peerstore().AddAddrs(b.h.ID(), []ma.Multiaddr{addr}, peerstore.TempAddrTTL)
	sendPair(t, a, b, ae, be)
	found := false
	for _, conn := range a.h.Network().ConnsToPeer(b.h.ID()) {
		if conn.Stat().Limited {
			found = true
		}
	}
	if !found {
		t.Fatal("fixture did not use limited relay connection")
	}
}
func TestOutsiderRejected(t *testing.T) {
	a, b, _, be := pair(t)
	b.SetContacts(nil)
	a.h.Connect(a.ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()})
	if err := a.Send("035d904a-84aa-40c1-a13f-ff128a2e98d3", b.h.ID().String(), "text", "blocked"); err == nil {
		t.Fatal("outsider delivered")
	}
	select {
	case e := <-be:
		if e.Op == "received" {
			t.Fatal("outsider emitted receive")
		}
	case <-time.After(100 * time.Millisecond):
	}
}
func TestValidationAndShutdown(t *testing.T) {
	a, b, _, _ := pair(t)
	for _, c := range []struct{ to, kind, text string }{{b.h.ID().String(), "bad", "a"}, {b.h.ID().String(), "text", strings.Repeat("界", 2001)}, {b.h.ID().String(), "server", strings.Repeat("a", 32769)}, {a.h.ID().String(), "text", "a"}} {
		if err := a.Send("035d904a-84aa-40c1-a13f-ff128a2e98d3", c.to, c.kind, c.text); err == nil {
			t.Fatalf("accepted invalid %+v", c)
		}
	}
	a.Close()
	if len(a.h.Network().ListenAddresses()) != 0 || len(a.h.Network().Conns()) != 0 {
		t.Fatal("network survives disable")
	}
	if err := a.Send("035d904a-84aa-40c1-a13f-ff128a2e98d3", b.h.ID().String(), "text", "a"); err == nil {
		t.Fatal("sent after close")
	}
}
func TestWrongRecipientRejected(t *testing.T) {
	a, b, _, be := pair(t)
	a.h.Connect(a.ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()})
	s, err := a.h.NewStream(network.WithAllowLimitedConn(a.ctx, "test"), b.h.ID(), messageProtocol)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	s.SetDeadline(time.Now().Add(time.Second))
	fmt.Fprintln(s, `{"version":1,"id":"035d904a-84aa-40c1-a13f-ff128a2e98d3","to":"`+a.h.ID().String()+`","kind":"text","text":"wrong"}`)
	var ack envelope
	if err := json.NewDecoder(s).Decode(&ack); err == nil {
		t.Fatal("wrong recipient ACK")
	}
	select {
	case e := <-be:
		if e.Op == "received" {
			t.Fatal("wrong recipient emitted receive")
		}
	case <-time.After(100 * time.Millisecond):
	}
}

// Fails if a removed contact's stream survives disable or a late receipt ACK succeeds.
func TestRemovedContactClosesSession(t *testing.T) {
	a, b, ae, be := pair(t)
	a.h.Connect(a.ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()})
	done := make(chan error, 1)
	go func() { done <- a.Send("035d904a-84aa-40c1-a13f-ff128a2e98d3", b.h.ID().String(), "text", "pending") }()
	e := awaitEvent(t, be, "received")
	b.mu.Lock()
	delete(b.online, a.h.ID())
	b.mu.Unlock()
	if err := b.SetContacts(nil); err != nil {
		t.Fatal(err)
	}
	b.Accept(e.Receipt, true)
	if err := <-done; err == nil {
		t.Fatal("removed contact received ACK")
	}
	if len(b.h.Network().ConnsToPeer(a.h.ID())) != 0 {
		t.Fatal("removed contact network session survives")
	}
	sent := awaitEvent(t, ae, "sent")
	if sent.Delivered {
		t.Fatal("late receipt delivered")
	}
}
func TestOversizedInboundFrameRejected(t *testing.T) {
	a, b, _, be := pair(t)
	a.h.Connect(a.ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()})
	s, err := a.h.NewStream(a.ctx, b.h.ID(), messageProtocol)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	s.SetDeadline(time.Now().Add(2 * time.Second))
	fmt.Fprintln(s, `{"version":1,"id":"035d904a-84aa-40c1-a13f-ff128a2e98d3","to":"`+b.h.ID().String()+`","kind":"server","text":"`+strings.Repeat("a", 70000)+`"}`)
	var ack envelope
	if err := json.NewDecoder(s).Decode(&ack); err == nil {
		t.Fatal("oversize frame ACK")
	}
	select {
	case e := <-be:
		if e.Op == "received" {
			t.Fatal("oversize emitted receive")
		}
	case <-time.After(100 * time.Millisecond):
	}
}
func TestNegativeReceiptIsNotDelivered(t *testing.T) {
	a, b, ae, be := pair(t)
	a.h.Connect(a.ctx, peer.AddrInfo{ID: b.h.ID(), Addrs: b.h.Addrs()})
	done := make(chan error, 1)
	go func() {
		done <- a.Send("035d904a-84aa-40c1-a13f-ff128a2e98d3", b.h.ID().String(), "server", "vless://one")
	}()
	e := awaitEvent(t, be, "received")
	b.Accept("unrelated-receipt", true)
	b.Accept(e.Receipt, false)
	if err := <-done; err == nil {
		t.Fatal("rejected commit ACKed")
	}
	if sent := awaitEvent(t, ae, "sent"); sent.Delivered {
		t.Fatal("rejected commit delivered")
	}
}

// Fails if production config is rejected by the pinned public DHT implementation.
func TestPublicDHTConfigurationStartsOffline(t *testing.T) {
	n, _ := testNode(t)
	router, err := newClientDHT(n.h)
	if err != nil {
		t.Fatalf("public client DHT configuration rejected: %v", err)
	}
	router.Close()
}
