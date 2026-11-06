# Architecture (0.1)

`PeerFerry.App` is a native WPF client. `PeerFerry.Core` owns state, adapters, invitations, discovery, TLS transport and the torrent engine. `PeerFerry.Tests` exercises the real engine over local TCP/TLS rather than mocking file transfer.

## Data flow

```text
WPF -> MeshNode -> allowed NIC address:45831 -> TLS + group proof -> remote MeshNode
          |                                                       |
    loopback proxy                                          loopback engine
          |                                                       |
    local MonoTorrent <---------- BitTorrent inside TLS -----------+
```

Each remote device has a local loopback proxy port. Verified peer addresses are introduced to MonoTorrent only as those proxy endpoints. MonoTorrent listens on 127.0.0.1 and its outgoing factory rejects non-loopback addresses. Engine DHT/LPD/PEX/UPnP/NAT-PMP are disabled. Torrent metadata rejects trackers, web seeds, DHT nodes and foreign group IDs. Plain BitTorrent is confined to loopback; TLS protects network transfers.

## Discovery and identity

- Device ID: SHA-256 of an ECDSA P-256 self-signed certificate. The private key stays in the local profile.
- Group key: 32 random bytes. Invitations include the key, group name, device ID and currently selected endpoints. A link is a reusable membership capability, not a public share URL.
- UDP port 45832; multicast 239.255.84.71 plus subnet broadcast every five seconds. Packets contain a timestamp and an HMAC. Incoming packet interface indices must belong to the selection.
- Persistent peers, validated invitations and authenticated contact gossip supplement discovery. There is no subnet sweep or public discovery service.
- TCP uses source binding and Windows `IP_UNICAST_IF`. Candidate paths prefer matching subnets and ZeroTier. Failed attempts try other selected paths. Keepalive detects broken TCP connections. A binding inventory change cancels existing tunnels and recreates listeners/proxies.

## Handshake

TLS requires certificates from both devices. The client pins the expected server fingerprint. Before granting access the server sends 32 random bytes and its ID. Client HMAC binds the nonce, both certificate IDs and a domain-separated protocol label. Server returns an independently labelled HMAC. Neither endpoint sends the group secret over the connection. Catalog, metadata and tunnel requests are processed only after verification. Length-prefixed JSON has bounded frames and handshake deadlines; the tunnel then carries raw BitTorrent bytes.

Group members can introduce other peers and participate in all group torrents. No individual member revocation or per-file ACL is claimed. This is a friends-group model, not a hostile multi-tenant service. Same-user local processes are outside the transport trust boundary.

## Files and persistence

Versioned JSON settings are atomically replaced under a write semaphore. Torrent metadata and fast-resume data are local. Each download gets a fresh per-hash directory; existing paths are not overwritten. Metadata checks Windows path segments, reserved names, duplicates, file/parent conflicts, external sources and group binding. Symlinks/reparse points are excluded. Changing a published source pauses its torrent. Completed and partial downloads automatically seed into the same group.

Transfers are isolated by group ID. An adapter/settings restart saves progress, restores managers, then reintroduces authenticated proxies. Queue concurrency is configurable. A small wrapper waits out MonoTorrent 3.0.2's asynchronous Starting state before stopping it, avoiding a mode transition race covered by pause/resume/remove tests.

Initial publication reuses the piece verification performed by TorrentCreator after checking source timestamps/lengths. It initializes a complete FastResume bitfield instead of reading the same large source twice. Downloaded files still undergo the engine's normal piece verification.

Bandwidth limits are enforced by shared monotonic-clock limiters in the outer transport, across all torrent tunnels, from the first transferred bytes. Engine rate limits are disabled: MonoTorrent initializes them on a later logic tick, which otherwise permits an initial unthrottled burst.

## Updates

Velopack handles packages and process replacement. GitHub Releases serves the update feed and packages over HTTPS. Auto-check downloads a package but requires an explicit restart action. Transfers stop cleanly before applying the update. Release trust is the configured GitHub repository; package hashes are integrity checks, not independent publisher authentication. Authenticode signing is not configured in this initial build.
