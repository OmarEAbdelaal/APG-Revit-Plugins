# Security policy

## Reporting a vulnerability

Use [GitHub's private vulnerability reporting](https://github.com/OmarEAbdelaal/APG-Revit-Plugins/security/advisories/new)
to report security issues confidentially to the repository maintainer,
[@OmarEAbdelaal](https://github.com/OmarEAbdelaal).
If that form is unavailable, email **omar.e.abdelaal@gmail.com** (the contact
already published in this repository). Start with a brief description and
arrange a secure channel before sharing sensitive attachments.

Do not put vulnerabilities, credentials, exploit details, or confidential project
data in public issues, pull requests, Actions logs, or discussions. Include the
affected plugin/version, Revit version, expected and actual behavior, impact, and
minimal reproduction steps using a sanitized sample. Never attach client models,
personal information, or live credentials. Coordinate disclosure with the maintainer.

The maintainer will assess impact and coordinate a fix or mitigation. No guaranteed
response time, paid support, or bug bounty is offered by this policy. If a report
is not acknowledged, follow up using the alternate private channel.

## Versions and scope

Reports are accepted for the latest published release and the current `main`
branch. Older versions should be upgraded; fixes are normally delivered in a new
release rather than backported. Revit compatibility is documented in README.md
and does not itself promise security maintenance for every historical release.

Relevant reports include the Revit add-ins, MCP connections and command loading,
installer/update downloads, dependency handling, and repository automation.
Report vulnerabilities in third-party products to their maintainers as well.

## Safe development and operation

- Keep credentials in GitHub Actions secrets or an approved credential store.
  Never commit tokens, private keys, signing certificates, `.env` files, or client data.
- If a credential is exposed, revoke or rotate it immediately. Deleting a file
  does not remove the credential from history, forks, caches, or existing downloads.
  Notify the maintainer privately and coordinate any history cleanup separately.
- Treat MCP command sets and downloaded add-ins as executable code. Install only
  approved versions, keep MCP endpoints local, and do not expose them to untrusted networks.
- Use pull requests for `main`; review dependency, workflow, installer, and security
  changes before merging. Do not approve an unfamiliar fork's workflow until reviewed.
- A proprietary license does not make a public repository confidential. Keep
  confidential APG/client material outside this public repository.

See [repository access and security administration](docs/REPOSITORY_SECURITY.md)
for responsibilities and the settings that enforce this policy.
