import { Alert, AlertTitle, Box, Link, Stack, Typography } from '@mui/material'

/**
 * The two small, one-time manual steps that remain after a successful
 * environment import. Surfaced both as a permanent reference on the page and
 * (more prominently) right after an import completes.
 */
export function PostImportCaveats() {
  return (
    <Alert severity="info" sx={{ '& .MuiAlert-message': { width: '100%' } }}>
      <AlertTitle>Two small manual steps remain</AlertTitle>
      <Typography variant="body2" sx={{ mb: 1.5 }}>
        Everything else — users, workspaces, projects, secrets, GitHub/Fly/runtime
        credentials — is restored from the blob with no re-authorization. Runtimes
        re-clone code from GitHub and re-provision Fly infrastructure on their own.
        Only these two housekeeping items need a human:
      </Typography>
      <Stack component="ol" spacing={1.5} sx={{ pl: 2.5, m: 0 }}>
        <Box component="li">
          <Typography variant="body2" sx={{ fontWeight: 600 }}>
            Point the GitHub App webhook URL at this environment&apos;s domain
          </Typography>
          <Typography variant="body2" color="text.secondary">
            GitHub delivers webhooks to a URL configured in the{' '}
            <Link
              href="https://github.com/settings/apps"
              target="_blank"
              rel="noreferrer noopener"
            >
              GitHub App settings
            </Link>{' '}
            (GitHub-side, not in our database). If this environment is on a new
            domain, update that one URL field once. The webhook secret itself came
            over in the blob — this is a one-time setting, not a re-authorization.
          </Typography>
        </Box>
        <Box component="li">
          <Typography variant="body2" sx={{ fontWeight: 600 }}>
            Ensure the Cloudflare subdomain pool exists in this environment&apos;s
            Cloudflare account
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Tunnels and subdomains rebuild automatically on boot (tunnel IDs are
            account-specific, so they are created fresh). Just make sure the target
            Cloudflare account has its subdomain pool allocated — the Cloudflare API
            token came over in the blob.
          </Typography>
        </Box>
      </Stack>
    </Alert>
  )
}
