# Security Policy

## Supported versions

Security fixes are applied to the latest version published from the `main` branch.

## Reporting a vulnerability

Please do not publish exploit details, credentials, access tokens, private media paths, or personal data in a public issue.

Report suspected vulnerabilities privately to the repository owner through GitHub's private vulnerability reporting feature, if enabled. If private reporting is unavailable, contact the maintainer through a trusted private channel and provide:
- The affected version or commit.
- A concise description of the impact.
- Reproduction steps that do not expose real customer data.
- Relevant logs with secrets and personal information removed.

## Deployment security notes

- Keep the Windows firewall rule for TCP 8096 limited to Private networks. Do not expose the viewer port to Public networks or the internet.
- Remote administration must use HTTPS on TCP 8443 with a certificate trusted by administrator devices. Do not share the PFX file or its password.
- Keep Supabase service-role keys and other secret keys out of the repository, application binaries, browser code, and logs. The publishable key is public by design.
- Run the application under a Windows account with only the permissions it needs.
- Back up the SQLite database, posters, and original media independently; scheduled application backups do not include video files.
- Download release artifacts only from this repository's official GitHub Actions or Releases pages. Do not bypass browser or antivirus warnings for files from unknown sources.

## Verification

Builds are produced by the Windows GitHub Actions workflow. A successful build verifies compilation and the tests included in that workflow; it does not replace runtime testing on the target Windows machine or code signing.
