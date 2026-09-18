# Repository access and security administration

## Ownership and responsibilities

This repository belongs to the personal GitHub account `OmarEAbdelaal`.
GitHub personal repositories have an owner and collaborators; organization
roles such as Read, Triage, Write, Maintain, Admin, teams, and domain/SSO policies
cannot be provisioned here as organization roles.

| Responsibility | Assignment and authority |
| --- | --- |
| Repository owner / security contact | `@OmarEAbdelaal`: manages access, settings, reports, releases, and code ownership. |
| Code owner | `@OmarEAbdelaal`, as defined in `.github/CODEOWNERS`; review responsibility does not grant access. |
| Collaborators | Only people expressly authorized by APG; work through pull requests. No additional collaborator is granted access by these files. |
| Automation | Build jobs use read-only repository contents. Only the separate release job requests `contents: write`. Actions may not approve pull requests. |

The owner should enable account two-factor authentication or passkeys, review
collaborator/app/deploy-key access regularly, and remove access when authorization
ends. Account authentication requirements cannot be enforced by this policy file.
Use narrowly scoped, expiring credentials for approved integrations.

## Branch and release controls

The `APG main security` ruleset targets the default branch and `main`:

- All changes go through a pull request, including owner changes.
- The `installer` check from GitHub Actions must pass on an up-to-date branch.
- Review conversations must be resolved and stale approvals dismissed.
- Force pushes and branch deletion are blocked, with no bypass actors.

Only the owner has repository access at initial setup. The required independent
approval count is therefore **zero**, and mandatory code-owner approval is off:
GitHub does not permit authors to approve their own pull requests. CODEOWNERS
still identifies the reviewer for other contributors. After APG authorizes a
second maintainer, update CODEOWNERS and require at least one independent approval,
code-owner approval, and approval of the most recent push. Do not add a bypass
just to avoid review.

The `APG release tag integrity` ruleset blocks changes and deletion of existing
`v*` tags, with no bypass actors. Release automation publishes only a successfully
built commit that is on `main`'s history. Manual publication must run from `main`
and use a `v*` tag; an existing tag must identify that same commit.

The pre-existing disabled `Claude protection` ruleset is retained unchanged.
Ruleset enforcement is a GitHub setting, not a consequence of this document.

## Security and Actions settings

The repository security baseline is:

- Secret scanning and push protection enabled.
- Dependabot alerts and security updates enabled; weekly Actions/NuGet update PRs
  configured in `.github/dependabot.yml`. Updates still require review and CI.
- Private vulnerability reporting enabled; fallback contact in `SECURITY.md`.
- Default Actions token permissions read-only; Actions cannot approve PRs.
- All outside collaborators require approval to run fork pull-request workflows.
- Workflow actions pinned to full commit SHAs, with SHA pinning enforced in settings.
- Checkout credentials are not persisted; untrusted PR builds cannot publish releases.

The installer build keeps its existing behavior of skipping unavailable/failing
Revit targets if another target succeeds. A passing `installer` check confirms
that an installer was produced, not that every supported Revit target compiled
or that the application is free of vulnerabilities.

## Visibility and licensing

The [APG proprietary internal-use license](../LICENSE) permits use only by
APG-authorized employees and contractors for APG work. It preserves third-party
licenses and previously granted rights. It is not an open-source license.

While this repository is public, anyone can read it and GitHub users can fork it
under GitHub's terms. CODEOWNERS and branch rules do not restrict reading or downloads.
Restricting access to specific people requires private visibility. Organization
teams and any eligible enterprise SSO/domain policies require an APG organization
and the appropriate GitHub plan; an email-domain list alone is not a public-repo
access control. Before a visibility change, check release/update access and plan
support for private-repository protections. Existing public copies cannot be recalled.

References: [personal repository permissions](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/repository-access-and-collaboration/permission-levels-for-a-personal-account-repository),
[rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets),
[licensing](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).
