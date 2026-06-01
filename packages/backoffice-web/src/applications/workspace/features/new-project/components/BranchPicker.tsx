import {
  Alert,
  Box,
  Chip,
  CircularProgress,
  List,
  ListItemButton,
  ListItemText,
  Stack,
  Typography,
} from '@mui/material'
import type { GithubBranchListItemDto } from '../../../../../api/queries-commands'

interface BranchPickerProps {
  branches: GithubBranchListItemDto[]
  isLoading: boolean
  isFetching: boolean
  errorMessage: string | null
  selectedBranch: string | null
  onSelect: (branchName: string) => void
}

/**
 * Renders the branch list for the selected repo. The default branch is
 * surfaced via the `isDefault` flag from the backend; the page is responsible
 * for pre-selecting it via state on the first successful load.
 */
export function BranchPicker({
  branches,
  isLoading,
  isFetching,
  errorMessage,
  selectedBranch,
  onSelect,
}: BranchPickerProps) {
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

  if (branches.length === 0) {
    return (
      <Alert severity="info">
        No branches were returned for this repository.
      </Alert>
    )
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={2} alignItems="center">
        <Typography variant="body2" color="text.secondary">
          Pick the branch you want to spin up. The default branch is pre-selected.
        </Typography>
        {isFetching && !isLoading && <CircularProgress size={16} />}
      </Stack>

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
          {branches.map((branch) => {
            const isSelected = selectedBranch === branch.name
            return (
              <ListItemButton
                key={branch.name}
                selected={isSelected}
                onClick={() => onSelect(branch.name)}
                sx={{ py: 1.25 }}
              >
                <ListItemText
                  primary={
                    <Stack direction="row" spacing={1} alignItems="center">
                      <Typography variant="body2" fontWeight={500}>
                        {branch.name}
                      </Typography>
                      {branch.isDefault && (
                        <Chip size="small" label="Default" color="primary" variant="outlined" />
                      )}
                    </Stack>
                  }
                />
              </ListItemButton>
            )
          })}
        </List>
      </Box>
    </Stack>
  )
}
