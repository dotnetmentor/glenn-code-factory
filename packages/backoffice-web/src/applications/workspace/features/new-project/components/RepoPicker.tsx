import { useMemo, useState } from 'react'
import { Link as RouterLink } from 'react-router-dom'
import {
  Alert,
  Avatar,
  Box,
  Chip,
  CircularProgress,
  InputAdornment,
  List,
  ListItemAvatar,
  ListItemButton,
  ListItemText,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import LockIcon from '@mui/icons-material/Lock'
import SearchIcon from '@mui/icons-material/Search'
import type { GithubRepoListItemDto } from '../../../../../api/queries-commands'
import { ManageGitHubAccessHint } from '../../../shared'

interface RepoPickerProps {
  repos: GithubRepoListItemDto[]
  isLoading: boolean
  isFetching: boolean
  errorMessage: string | null
  selected: { owner: string; name: string } | null
  onSelect: (repo: GithubRepoListItemDto) => void
  /**
   * Workspace slug, used to build "Open existing project" links for repos
   * that already have a project in the current workspace.
   */
  slug?: string
  /**
   * URL to GitHub's installation-manage page. When provided, a small
   * "Can't see your repository? Modify access" link is rendered
   * below the list so users can adjust the GitHub App's repo selection.
   */
  manageAccessUrl?: string | null
}

/**
 * Scrollable list of repos with a client-side search filter. Repos returned
 * from the GitHub installation endpoint are typically small (per-installation),
 * so a client-side filter is enough.
 *
 * Repos already linked to a project in the current workspace are rendered
 * as visually-disabled rows with a small "Open existing project" affordance
 * so the user can navigate to the existing project instead of trying to
 * create a duplicate. Used repos are sorted to the bottom so the actionable
 * options stay at the top.
 */
export function RepoPicker({
  repos,
  isLoading,
  isFetching,
  errorMessage,
  selected,
  onSelect,
  slug,
  manageAccessUrl,
}: RepoPickerProps) {
  const [search, setSearch] = useState('')

  const filteredRepos = useMemo(() => {
    const q = search.trim().toLowerCase()
    const matchesQuery = (r: GithubRepoListItemDto) =>
      !q ||
      r.name.toLowerCase().includes(q) ||
      r.owner.toLowerCase().includes(q) ||
      `${r.owner}/${r.name}`.toLowerCase().includes(q)

    const matched = repos.filter(matchesQuery)

    // Sort: unlinked repos first (alpha), then linked repos (alpha). Within
    // each bucket, sort case-insensitively by `owner/name`.
    return matched.slice().sort((a, b) => {
      const aLinked = !!a.linkedProjectId
      const bLinked = !!b.linkedProjectId
      if (aLinked !== bLinked) return aLinked ? 1 : -1
      const aKey = `${a.owner}/${a.name}`.toLowerCase()
      const bKey = `${b.owner}/${b.name}`.toLowerCase()
      return aKey < bKey ? -1 : aKey > bKey ? 1 : 0
    })
  }, [repos, search])

  if (errorMessage) {
    return <Alert severity="error">{errorMessage}</Alert>
  }

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
        <CircularProgress size={24} />
      </Box>
    )
  }

  if (repos.length === 0) {
    return (
      <Stack spacing={1.5}>
        <Alert severity="info">
          No repositories found for this installation. Make sure the GitHub App
          has been granted access to at least one repository.
        </Alert>
        {manageAccessUrl && <ManageGitHubAccessHint url={manageAccessUrl} />}
      </Stack>
    )
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={2} alignItems="center">
        <TextField
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search repositories"
          size="small"
          fullWidth
          InputProps={{
            startAdornment: (
              <InputAdornment position="start">
                <SearchIcon fontSize="small" />
              </InputAdornment>
            ),
          }}
          inputProps={{ 'aria-label': 'Search repositories' }}
          sx={{ maxWidth: 480 }}
        />
        {isFetching && !isLoading && <CircularProgress size={16} />}
      </Stack>

      {filteredRepos.length === 0 ? (
        <Box sx={{ py: 3, textAlign: 'center' }}>
          <Typography variant="body2" color="text.secondary">
            No repositories match &ldquo;{search}&rdquo;.
          </Typography>
        </Box>
      ) : (
        <Box
          sx={{
            maxHeight: 420,
            overflow: 'auto',
            border: 1,
            borderColor: 'divider',
            borderRadius: 1,
          }}
        >
          <List disablePadding>
            {filteredRepos.map((repo) => {
              const isSelected =
                selected?.owner === repo.owner && selected?.name === repo.name
              const isLinked = !!repo.linkedProjectId
              const linkedTitle = repo.linkedProjectName
                ? `Used by '${repo.linkedProjectName}'`
                : 'Already used by another project'

              const primary = (
                <Stack direction="row" spacing={1} alignItems="center">
                  <Typography
                    variant="body2"
                    fontWeight={500}
                    sx={isLinked ? { color: 'text.secondary' } : undefined}
                  >
                    {repo.owner}/{repo.name}
                  </Typography>
                  {repo.private && (
                    <Chip
                      size="small"
                      label="Private"
                      color="warning"
                      variant="outlined"
                      icon={<LockIcon sx={{ fontSize: 12 }} />}
                    />
                  )}
                  {isLinked && (
                    <Tooltip title={linkedTitle} placement="top" enterDelay={400}>
                      <Typography
                        component="span"
                        sx={{
                          fontSize: '0.75rem',
                          fontStyle: 'italic',
                          color: 'text.secondary',
                        }}
                      >
                        already used
                      </Typography>
                    </Tooltip>
                  )}
                </Stack>
              )

              const secondary = repo.defaultBranch ? (
                <Box
                  component="span"
                  sx={{ color: 'text.secondary', fontSize: '0.75rem' }}
                >
                  default branch: {repo.defaultBranch}
                </Box>
              ) : null

              if (isLinked) {
                // Disabled-row treatment + "Open existing project" affordance.
                // We render the same visual shape as the active rows so the list
                // doesn't shift, just at lower opacity and with the click intent
                // redirected to navigation.
                return (
                  <Box
                    key={`${repo.owner}/${repo.name}`}
                    sx={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: 1,
                      px: 2,
                      py: 1.25,
                      opacity: 0.55,
                      borderBottom: 1,
                      borderColor: 'divider',
                      '&:last-of-type': { borderBottom: 0 },
                    }}
                  >
                    <Avatar
                      src={`https://avatars.githubusercontent.com/${repo.owner}`}
                      alt={repo.owner}
                      sx={{ width: 28, height: 28, mr: 1 }}
                      variant="rounded"
                    >
                      {repo.owner.charAt(0).toUpperCase()}
                    </Avatar>
                    <Box sx={{ flex: 1, minWidth: 0 }}>
                      {primary}
                      {secondary}
                    </Box>
                    {slug && repo.linkedProjectId && (
                      <Box
                        component={RouterLink}
                        to={`/w/${slug}/projects/${repo.linkedProjectId}`}
                        sx={{
                          flexShrink: 0,
                          fontSize: '0.75rem',
                          fontWeight: 500,
                          color: 'primary.main',
                          textDecoration: 'none',
                          opacity: 0.95,
                          '&:hover': { textDecoration: 'underline' },
                        }}
                      >
                        Open existing project →
                      </Box>
                    )}
                  </Box>
                )
              }

              return (
                <ListItemButton
                  key={`${repo.owner}/${repo.name}`}
                  selected={isSelected}
                  onClick={() => onSelect(repo)}
                  sx={{ py: 1.25 }}
                >
                  <ListItemAvatar>
                    <Avatar
                      src={`https://avatars.githubusercontent.com/${repo.owner}`}
                      alt={repo.owner}
                      sx={{ width: 28, height: 28 }}
                      variant="rounded"
                    >
                      {repo.owner.charAt(0).toUpperCase()}
                    </Avatar>
                  </ListItemAvatar>
                  <ListItemText primary={primary} secondary={secondary} />
                </ListItemButton>
              )
            })}
          </List>
        </Box>
      )}

      {manageAccessUrl && <ManageGitHubAccessHint url={manageAccessUrl} />}
    </Stack>
  )
}
