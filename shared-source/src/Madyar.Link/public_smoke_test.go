package main

import (
	"context"
	"crypto/rand"
	"os"
	"strings"
	"testing"
	"time"
)

// Explicit opt-in: public connectivity is never required by the offline test suite.
func TestPublicBootstrapRelaySmoke(t *testing.T) {
	if os.Getenv("MADYAR_PUBLIC_SMOKE") != "1" {
		t.Skip("explicit public network opt-in required")
	}
	seed := make([]byte, 32)
	rand.Read(seed)
	n, err := newNode(context.Background(), seed, nil, true, func(e event) {
		if e.Op == "state" {
			t.Logf("state=%s", e.Detail)
		}
	})
	clear(seed)
	if err != nil {
		t.Fatal(err)
	}
	defer n.Close()
	deadline := time.NewTimer(65 * time.Second)
	defer deadline.Stop()
	ticker := time.NewTicker(10 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			count := 0
			for _, p := range n.h.Peerstore().Peers() {
				supported, _ := n.h.Peerstore().SupportsProtocols(p, relayHop)
				if len(supported) > 0 {
					count++
				}
			}
			reserved := 0
			for _, addr := range n.h.Addrs() {
				if strings.Contains(addr.String(), "p2p-circuit") {
					reserved++
				}
			}
			t.Logf("connections=%d dhtPeers=%d hopCandidates=%d relayAddresses=%d", len(n.h.Network().Conns()), n.dht.RoutingTable().Size(), count, reserved)
		case <-deadline.C:
			if len(n.h.Network().Conns()) == 0 {
				t.Error("no public bootstrap connection established")
			}
			if n.dht.RoutingTable().Size() == 0 {
				t.Error("public DHT routing table remained empty")
			}
			return
		}
	}
}

// Two independent identities learn each other's addresses solely via public pair-provider routing.
func TestPublicPairDiscoveryDeliverySmoke(t *testing.T) {
	if os.Getenv("MADYAR_PUBLIC_SMOKE") != "1" {
		t.Skip("explicit public network opt-in required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Second)
	defer cancel()
	seedA, seedB := make([]byte, 32), make([]byte, 32)
	rand.Read(seedA)
	rand.Read(seedB)
	events := make(chan event, 256)
	emit := func(e event) {
		if e.Op == "state" {
			t.Logf("state=%s", e.Detail)
		}
		select {
		case events <- e:
		default:
		}
	}
	a, err := newNode(ctx, seedA, nil, true, emit)
	clear(seedA)
	if err != nil {
		t.Fatal(err)
	}
	defer a.Close()
	b, err := newNode(ctx, seedB, []string{a.h.ID().String()}, true, emit)
	clear(seedB)
	if err != nil {
		t.Fatal(err)
	}
	defer b.Close()
	a.SetContacts([]string{b.h.ID().String()})
	if len(a.h.Peerstore().Addrs(b.h.ID())) != 0 {
		t.Fatal("remote address known before public discovery")
	}
	go func() {
		for {
			select {
			case e := <-events:
				if e.Op == "received" {
					b.Accept(e.Receipt, true)
				}
			case <-ctx.Done():
				return
			}
		}
	}()
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	id := "2c7d9d21-31a5-4559-b199-845d7bb0a748"
	for {
		select {
		case <-ctx.Done():
			t.Fatal("public pair discovery/delivery timed out without fixture addresses")
		case <-ticker.C:
			if len(a.h.Peerstore().Addrs(b.h.ID())) == 0 {
				continue
			}
			t.Logf("public provider discovery yielded pinned recipient addresses=%d", len(a.h.Peerstore().Addrs(b.h.ID())))
			if err := a.Send(id, b.h.ID().String(), "text", "public transport acceptance probe"); err != nil {
				t.Logf("send status=%s", err.Error())
				continue
			}
			relayed := false
			for _, connection := range a.h.Network().ConnsToPeer(b.h.ID()) {
				if connection.Stat().Limited {
					relayed = true
				}
			}
			t.Logf("PASS authenticated encrypted message and matching durable ACK; limitedRelay=%t; same-Mac topology only", relayed)
			return
		}
	}
}

// Explicit opt-in, same-Mac public topology: issuer starts with zero contacts;
// requester learns only the pinned issuer through its invite namespace, never fixture addresses.
func TestPublicInvitationDiscoveryDeliverySmoke(t *testing.T) {
	if os.Getenv("MADYAR_PUBLIC_SMOKE") != "1" {
		t.Skip("explicit public network opt-in required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 180*time.Second)
	defer cancel()
	seedA, seedB := make([]byte, 32), make([]byte, 32)
	rand.Read(seedA)
	rand.Read(seedB)
	events := make(chan event, 64)
	issuer, err := newNode(ctx, seedA, nil, true, func(e event) {
		if e.Op == "state" {
			t.Logf("issuer state=%s", e.Detail)
		}
		select {
		case events <- e:
		default:
		}
	})
	clear(seedA)
	if err != nil {
		t.Fatal(err)
	}
	defer issuer.Close()
	requester, err := newNode(ctx, seedB, []string{issuer.h.ID().String()}, true, func(e event) {
		if e.Op == "state" {
			t.Logf("requester state=%s", e.Detail)
		}
	})
	clear(seedB)
	if err != nil {
		t.Fatal(err)
	}
	defer requester.Close()
	if err = issuer.SetInvites([]issuedInvite{{testInvite, time.Now().Add(time.Hour)}}); err != nil {
		t.Fatal(err)
	}
	if len(issuer.peers()) != 0 || len(requester.h.Peerstore().Addrs(issuer.h.ID())) != 0 {
		t.Fatal("public introduction fixture was pre-connected")
	}
	go func() {
		for {
			select {
			case e := <-events:
				if e.Op == "intro_received" {
					issuer.Accept(e.Receipt, e.Peer == requester.h.ID().String() && e.Invite == testInvite)
				}
			case <-ctx.Done():
				return
			}
		}
	}()
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			t.Fatal("public invitation delivery timed out without fixture addresses")
		case <-ticker.C:
			if err = requester.SendIntroduction(requestID, issuer.h.ID().String(), testInvite, "MDC1-public-transport-probe"); err != nil {
				t.Logf("intro status=%s", err)
				continue
			}
			limited := false
			for _, c := range requester.h.Network().ConnsToPeer(issuer.h.ID()) {
				limited = limited || c.Stat().Limited
			}
			if issuer.contact(requester.h.ID()) {
				t.Fatal("introduction granted contact access")
			}
			t.Logf("PASS public invite rendezvous, pinned authenticated issuer, request ACK; limitedRelay=%t; same-Mac topology only", limited)
			return
		}
	}
}
