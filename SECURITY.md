# Security policy

## Supported versions

Security fixes are applied to the latest version on the default branch.
Older versions are not guaranteed to receive backported fixes.

## Reporting a vulnerability

Please do not publish security vulnerabilities in a public issue. Report them
privately to the project maintainers with:

- a description and impact assessment,
- a minimal reproducer or proof of concept,
- affected version and operating system,
- any relevant Access file or SQL fixture that can be shared safely.

Important security areas include linked-database path traversal, malformed or
hostile Access files, temporary-file handling during atomic writes, and resource
exhaustion through very large long values.

The optional AccessCrypto package implements compatibility with the Access
password-encryption profile; it is not a replacement for database-server
authentication. Passwords must be supplied by the host application and are not
logged or included in connection-string rendering. Report any case where a
plaintext page or password-derived key is written to a temporary/staged file.
If `Mirror Mode=file` is enabled, protect the SQLite mirror separately: it is a
provider cache and is intentionally not encrypted by the Access codec.
