# Jellyfin Chat

Jellyfin Chat adds an integrated chat experience to Jellyfin without requiring a separate server, database, or background service.

## Features

- Public server channels
- Administrator announcement channels
- Private one-to-one chats
- Restricted group chats
- Administrator moderation and per-user access controls
- Optional experimental end-to-end encryption using MLS 1.0
- Encrypted device management and recovery after a browser-origin change
- English and German interface localization
- Chat refreshes only while the Chat page is open
- Users can delete their own messages; private chats can be deleted for both participants

## Requirements

- Jellyfin 10.11.11 or later in the 10.11 series
- Jellyfin 12.0 or later in the 12.0 series
- HTTPS is required for end-to-end encryption, except when running on `localhost`

## Installation

1. In Jellyfin, open Dashboard → Plugins → Repositories.
2. Add the repository manifest:

   `https://raw.githubusercontent.com/pmssoftware/jellyfin-plugin-chat/main/manifest.json`

3. Install **Jellyfin Chat** and restart Jellyfin.
4. Open the Chat plugin settings and enable Chat globally and for the users who should have access.

The plugin is self-contained. It stores its state in the normal Jellyfin plugin data directory.

## Encryption

Encryption is experimental. Private and restricted channels use browser-held keys; the server stores encrypted protocol events and cannot read message contents. Each browser/device must join the channel before it can send or decrypt messages.

The administrator can optionally enable additional encryption details in the plugin settings. These include the MLS version, device count, experimental status, and security code.

Deleting a private chat permanently removes the chat, its encrypted history, and its encryption state for both participants.

See [SECURITY.md](SECURITY.md) for security limitations and reporting guidance.

## Development

The project contains a .NET Jellyfin plugin and a bundled browser client. Useful checks include:

```bash
pnpm install --frozen-lockfile
pnpm run test:e2ee
pnpm run test:private-policy
dotnet build Jellyfin.Chat.slnx -c Release
```

## Roadmap

See [ROADMAP.md](ROADMAP.md). Planned work includes a separate web/native client, Android TV integration, richer group management, and support for connecting compatible local Jellyfin servers.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
