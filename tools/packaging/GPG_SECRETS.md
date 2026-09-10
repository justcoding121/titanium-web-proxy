# GPG release checksums

`release.yml` always attaches `SHA256SUMS`. When the following secrets are set, it also attaches armored signatures:

| Secret | Purpose |
| --- | --- |
| `GPG_PRIVATE_KEY` | ASCII-armored private key (or `gpg --export-secret-keys --armor`) |
| `GPG_PASSPHRASE` | Optional passphrase for the key |

Generate once (example):

```shell
gpg --batch --passphrase '' --quick-generate-key 'Titanium Releases <you@example.com>' default default 2y
gpg --export-secret-keys --armor 'Titanium Releases' | gh secret set GPG_PRIVATE_KEY
gpg --export --armor 'Titanium Releases' > titanium-releases.asc   # publish this public key on the website/docs
```

Do not paste private keys into chat.
