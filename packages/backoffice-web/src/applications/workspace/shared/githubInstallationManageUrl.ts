import type { GithubInstallationListItem } from '../../../api/queries-commands'

/**
 * GitHub installation settings URL where the user can add or remove repos
 * granted to the GlennCode GitHub App.
 *
 * `installationId` on the DTO is GitHub's numeric installation id (not our DB guid).
 */
export function buildGithubInstallationManageUrl(
  installation: GithubInstallationListItem | null | undefined,
): string | null {
  if (!installation) return null
  const numericId = installation.installationId
  if (!numericId) return null
  const accountType = installation.accountType?.toLowerCase() ?? ''
  if (accountType === 'organization' && installation.accountLogin) {
    return `https://github.com/organizations/${installation.accountLogin}/settings/installations/${numericId}`
  }
  return `https://github.com/settings/installations/${numericId}`
}
