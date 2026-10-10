package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"os"
	"os/signal"
	"sync"
	"time"

	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/peerstore"
	ma "github.com/multiformats/go-multiaddr"
)

const maxInit = 2 * 1024 * 1024
const maxCommand = 128 * 1024

type command struct {
	Invites   []issuedInvite `json:"invites,omitempty"`
	Invite    string         `json:"invite,omitempty"`
	Op        string         `json:"op"`
	Seed      string         `json:"seed,omitempty"`
	Contacts  []string       `json:"contacts,omitempty"`
	Internet  bool           `json:"internet"`
	ID        string         `json:"id,omitempty"`
	To        string         `json:"to,omitempty"`
	Kind      string         `json:"kind,omitempty"`
	Text      string         `json:"text,omitempty"`
	Receipt   string         `json:"receipt,omitempty"`
	Accepted  bool           `json:"accepted"`
	Peer      string         `json:"peer,omitempty"`
	Addresses []string       `json:"addresses,omitempty"`
}

func parseCommand(raw []byte) (command, error) {
	var c command
	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&c); err != nil {
		return c, errors.New("invalid_command")
	}
	var extra any
	if decoder.Decode(&extra) != io.EOF {
		return c, errors.New("invalid_command")
	}
	return c, nil
}
func run(parent context.Context, input io.Reader, output io.Writer) error {
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	if closer, ok := input.(io.ReadCloser); ok {
		defer closer.Close()
	}
	lines := make(chan []byte, 1)
	scanDone := make(chan error, 1)
	go func() {
		scanner := bufio.NewScanner(input)
		scanner.Buffer(make([]byte, 4096), maxInit+1)
		for scanner.Scan() {
			raw := append([]byte(nil), scanner.Bytes()...)
			clear(scanner.Bytes())
			select {
			case lines <- raw:
			case <-ctx.Done():
				return
			}
		}
		scanDone <- scanner.Err()
		close(lines)
	}()
	var raw []byte
	select {
	case <-ctx.Done():
		return errors.New("disabled")
	case raw = <-lines:
	}
	if len(raw) == 0 || len(raw) > maxInit {
		return errors.New("invalid_init")
	}
	start, err := parseCommand(raw)
	clear(raw)
	if err != nil || start.Op != "start" {
		return errors.New("invalid_init")
	}
	seed, err := base64.StdEncoding.Strict().DecodeString(start.Seed)
	start.Seed = ""
	if err != nil || len(seed) != 32 {
		clear(seed)
		return errors.New("invalid_identity")
	}
	events := make(chan event, 64)
	writerDone := make(chan struct{})
	go func() {
		defer close(writerDone)
		encoder := json.NewEncoder(output)
		encoder.SetEscapeHTML(false)
		for e := range events {
			if encoder.Encode(e) != nil {
				cancel()
				return
			}
		}
	}()
	emit := func(e event) {
		raw, err := json.Marshal(e)
		if err != nil || len(raw)+1 > maxCommand {
			e = event{Op: "state", Detail: "event_too_large"}
		}
		select {
		case events <- e:
		default:
		}
	}
	var sends sync.WaitGroup
	var n *node
	defer func() {
		if n != nil {
			n.Close()
		}
		sends.Wait()
		close(events)
		select {
		case <-writerDone:
		case <-time.After(time.Second):
		}
	}()
	n, err = newNode(ctx, seed, start.Contacts, start.Internet, emit)
	clear(seed)
	if err != nil {
		return err
	}
	addresses := []string{}
	for _, addr := range n.h.Addrs() {
		addresses = append(addresses, addr.String())
	}
	emit(event{Op: "ready", Peer: n.h.ID().String(), Addresses: addresses})
	slots := make(chan struct{}, 8)
	for {
		select {
		case <-ctx.Done():
			return nil
		case raw, ok := <-lines:
			if !ok {
				err := <-scanDone
				if err != nil {
					return errors.New("command_too_large")
				}
				return nil
			}
			if len(raw) > maxCommand {
				emit(event{Op: "state", Detail: "command_too_large"})
				continue
			}
			c, err := parseCommand(raw)
			clear(raw)
			if err != nil {
				emit(event{Op: "state", Detail: "invalid_command"})
				continue
			}
			switch c.Op {
			case "stop":
				return nil
			case "contacts":
				if err = n.SetContacts(c.Contacts); err != nil {
					emit(event{Op: "state", Detail: err.Error()})
				} else {
					emit(event{Op: "state", Detail: "contacts_updated"})
				}
			case "invites":
				if err = n.SetInvites(c.Invites); err != nil {
					emit(event{Op: "state", Detail: err.Error()})
				} else {
					emit(event{Op: "state", Detail: "invites_updated"})
				}
			case "intro_send":
				select {
				case slots <- struct{}{}:
					sends.Add(1)
					go func(c command) {
						defer sends.Done()
						defer func() { <-slots }()
						n.SendIntroduction(c.ID, c.To, c.Invite, c.Text)
					}(c)
				default:
					emit(event{Op: "intro_sent", Peer: c.To, ID: c.ID, Detail: "busy"})
				}
			case "accept":
				n.Accept(c.Receipt, c.Accepted)
			case "send":
				select {
				case slots <- struct{}{}:
					sends.Add(1)
					go func(c command) { defer sends.Done(); defer func() { <-slots }(); n.Send(c.ID, c.To, c.Kind, c.Text) }(c)
				default:
					emit(event{Op: "sent", Peer: c.To, ID: c.ID, Detail: "busy"})
				}
			case "fixtureaddr":
				if start.Internet {
					emit(event{Op: "state", Detail: "fixture_disabled"})
					continue
				}
				if err = fixtureAddr(n, c.Peer, c.Addresses); err != nil {
					emit(event{Op: "state", Detail: err.Error()})
				} else {
					emit(event{Op: "state", Detail: "fixture_ready"})
				}
			default:
				emit(event{Op: "state", Detail: "invalid_command"})
			}
		}
	}
}
func fixtureAddr(n *node, id string, addresses []string) error {
	p, err := peer.Decode(id)
	if err != nil || !n.contact(p) || len(addresses) == 0 || len(addresses) > 16 {
		return errors.New("invalid_fixture")
	}
	parsed := make([]ma.Multiaddr, 0, len(addresses))
	for _, s := range addresses {
		addr, err := ma.NewMultiaddr(s)
		if err != nil {
			return errors.New("invalid_fixture")
		}
		protocols := addr.Protocols()
		if len(protocols) > 0 && protocols[len(protocols)-1].Code == ma.P_P2P {
			info, err := peer.AddrInfoFromP2pAddr(addr)
			if err != nil || info.ID != p {
				return errors.New("invalid_fixture")
			}
			parsed = append(parsed, info.Addrs...)
		} else {
			parsed = append(parsed, addr)
		}
	}
	n.h.Peerstore().AddAddrs(p, parsed, peerstore.TempAddrTTL)
	return nil
}
func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	if run(ctx, os.Stdin, os.Stdout) != nil {
		os.Exit(1)
	}
}
