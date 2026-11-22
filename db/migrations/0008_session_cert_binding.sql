-- With the mTLS gateway enabled, a device session is bound to the SHA-256 fingerprint of the client certificate it
-- logged in with: a stolen access/refresh token is useless without that certificate's private key.
-- NULL = session created without a gateway (direct/LAN access); such sessions are not bound.
ALTER TABLE device_session ADD COLUMN cert_fingerprint text;
