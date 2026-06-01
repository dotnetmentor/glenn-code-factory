/**
 * Frontend-side help text for GitHub system settings.
 * The backend catalog ships short one-liners; we augment with richer
 * UI-facing guidance and an optional documentation link per field.
 *
 * Keyed by the canonical setting key (e.g. "GitHub:AppId").
 * Falls back to the catalog `description` when a key is absent here.
 */
export interface GitHubFieldHelp {
  help: string
  docUrl?: string
}

export const GITHUB_FIELD_HELP: Record<string, GitHubFieldHelp> = {
  'GitHub:AppId': {
    help:
      'Find at Settings → Developer settings → GitHub Apps → [your app]. The "App ID" near the top of the page.',
    docUrl: 'https://docs.github.com/en/apps/creating-github-apps',
  },
  'GitHub:ClientId': {
    help:
      'On the same App settings page, in the "About" section. Public identifier for the App.',
  },
  'GitHub:ClientSecret': {
    help:
      'Same App settings page → "Client secrets" → Generate a new secret. Copy immediately, GitHub only shows it once.',
  },
  'GitHub:WebhookSecret': {
    help:
      'Set this when configuring the Webhook on your App. Use a strong random string. Used to verify webhook payloads.',
  },
  'GitHub:PrivateKeyPem': {
    help:
      'Bottom of the App settings page → "Generate a private key" downloads a .pem file. Paste the FULL contents including BEGIN/END lines.',
  },
  'GitHub:AppSlug': {
    help:
      'URL slug of the GitHub App, e.g. "my-app" from github.com/apps/my-app.',
  },
  'GitHub:InstallUrl': {
    help:
      'Format: https://github.com/apps/<your-app-slug>/installations/new — the public install URL.',
  },
  'GitHub:OAuthRedirectUri': {
    help:
      'In App settings → "Identifying and authorizing users" → "User authorization callback URL". Set to <your-domain>/api/github/login/callback.',
  },
  'GitHub:AppInstallRedirectUri': {
    help:
      'In App settings → "Setup URL". Set to <your-domain>/api/github/install/callback so the user lands back here after installing.',
  },
}
