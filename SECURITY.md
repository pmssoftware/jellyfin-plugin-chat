# Security Policy

Please report vulnerabilities privately through GitHub Security Advisories rather than a public issue. Do not include Jellyfin access tokens, passwords, message history, user data, server addresses, or logs in a report unless a maintainer explicitly requests a narrowly scoped sample.

Version 1.1 message bodies use experimental MLS 1.0 end-to-end encryption. The server still sees metadata, and clients must use HTTPS because a modified web client could expose local keys or plaintext. The bundled MLS library has not received a formal security audit; this release must not be treated as equivalent to an audited native messenger.

Messages created by version 1.0 remain unencrypted until retention removes them. Browser storage contains endpoint key material and decrypted local history. Clearing it removes access to that browser's encrypted history.
