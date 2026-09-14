# Jellyfin Chat

Jellyfin Chat adds self-contained chat and announcement channels to Jellyfin. It uses Jellyfin accounts and runs inside the Jellyfin server; no separate chat server, database service, or account system is required.

## Version 1.1

- Experimental MLS 1.0 end-to-end encryption for all new message bodies
- Automatic per-browser device registration, group joining, and key rotation
- Local device-identity pinning and visible per-channel security codes
- Administrator revocation of lost encrypted devices
- Browser-only private key and decrypted-message storage

## Version 1.0

- A public chat channel for every authenticated Jellyfin user
- Administrator-created chat and announcement channels
- Announcement channels that only administrators can post to
- Message history with configurable retention
- Message refreshes only while the Chat pane is actually open and visible
- Administrator message deletion and user muting
- Global chat enable/disable plus per-user access controls
- A list of Jellyfin users who have logged in at least once
- Configurable tab name, message length, and rate limit
- English and German interfaces
- Automatic CustomTabs setup and a resilient Jellyfin Web sidebar bridge
- Automatic removal of only this plugin's tab and transformation during uninstall
- Jellyfin 10.11 and experimental Jellyfin 12 builds

Messages are stored in `chat-state.json` inside the plugin's Jellyfin data directory. Writes are serialized and replaced atomically. The file has an explicit schema version so a future release can migrate it to a larger embedded database without changing the public API.

## Requirements

- Jellyfin 10.11.11 for the tested build target
- CustomTabs
- File Transformation

Jellyfin 12 support compiles against the 12.0 packages but has not been tested on a running Jellyfin 12 server. CustomTabs runtime support for Jellyfin 12 must also be confirmed before the home tab should be considered supported.

## Install from the Jellyfin catalog

After the first GitHub release, add this repository URL in **Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/pmssoftware/jellyfin-plugin-chat/main/manifest.json
```

Install **Jellyfin Chat**, restart Jellyfin, and force-refresh Jellyfin Web once. The Chat tab is created automatically when CustomTabs and File Transformation are available.

## Security and moderation

- Every data endpoint requires a valid Jellyfin session.
- New message bodies are encrypted in the browser with MLS 1.0 before they reach Jellyfin.
- Device key packages are exchanged automatically; a newly connected browser may briefly wait for an existing encrypted device to add it to the channel.
- Private key material and decrypted message history stay in that browser's IndexedDB storage.
- Per-channel device identities are pinned locally and each channel displays a security code for out-of-band comparison.
- Message endpoints enforce both the global switch and the current user's access setting.
- Channel management, settings, message deletion, and user muting require administrator elevation.
- Administrators can revoke lost encrypted devices and delete ciphertext, but cannot decrypt message bodies through the plugin API.
- Message bodies are rendered as plain text, not HTML.
- Message length is enforced by the encrypted client; posting frequency is enforced by the server.
- Hidden home tabs and background browser pages do not poll for messages.

End-to-end encryption is experimental. The bundled `ts-mls` implementation has not received a formal security audit, and a web client delivered by a compromised Jellyfin server could be modified before it runs. Use HTTPS, compare the displayed security code when trust matters, and do not treat this release as equivalent to an audited native messenger. Jellyfin still sees channel membership, sender, timestamps, and message sizes. Clearing browser storage removes local keys and decrypted history; there is no recovery mechanism yet.

Messages created by version 1.0 remain plaintext in the old server data until normal retention removes them. They are not imported into or displayed by the encrypted client. See `THIRD_PARTY_NOTICES` for bundled cryptography credits.

## Roadmap

The roadmap is directional; later features will be designed without breaking the version 1.0 endpoints or stored identities.

### 1.x

- Replies, reactions, mentions, and unread counts
- Private groups and invitations using the encrypted group layer
- Device recovery and a fuller device-verification interface
- Improved live delivery beyond short polling
- Reporting and more granular channel permissions

### 2.x

- Separate responsive web client using Jellyfin authentication
- Mobile clients and notification support
- Native key storage and stronger device-verification flows

### 3.x

- Administrator-approved pairing between Jellyfin servers
- Shared channels, signed server identities, offline synchronization, and remote-user attribution
- A read-first Android TV chat and announcements experience

Automatic LAN discovery will never establish trust by itself. Cross-server communication will require approval by administrators on both servers.

## Development

Build Jellyfin 10.11:

```sh
dotnet build src/Jellyfin.Plugin.Chat/Jellyfin.Plugin.Chat.csproj --configuration Release --property:TargetFrameworks=net9.0
```

Build Jellyfin 12:

```sh
dotnet build src/Jellyfin.Plugin.Chat/Jellyfin.Plugin.Chat.csproj --configuration Release --property:TargetFrameworks=net10.0
```

## License

MIT. See [LICENSE](LICENSE).
