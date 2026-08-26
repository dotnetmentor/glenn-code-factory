import { useState } from 'react'
import {
  Box,
  Stack,
  Tab,
  Tabs,
  Typography,
} from '@mui/material'
import { BoxesTab } from '../components/BoxesTab'
import { SnapshotsTab } from '../components/SnapshotsTab'

type TabKey = 'boxes' | 'snapshots'

/**
 * Operator surface for cleaning up Box resources (boxes + snapshots) that
 * linger after runtimes are deleted. Two tabs, identical chrome: filter row,
 * table, bulk-delete confirmation. The linkage badge on each row tells
 * operators which resources still map back to a live DB runtime — those are
 * not safe to nuke without breaking something.
 *
 * <p>Orphan boxes self-archive at TTL — compute cost has already stopped by
 * the time they show up here. Cleanup is hygiene plus reclaiming snapshot
 * storage, not a cost fire-drill.</p>
 */
export function BoxCleanupPage() {
  const [tab, setTab] = useState<TabKey>('boxes')

  return (
    <Stack spacing={3}>
          <Box>
            <Typography variant="overline" color="text.secondary">
              Super admin
            </Typography>
            <Typography variant="h4" component="h1" sx={{ mb: 0.5 }}>
              Box cleanup
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Delete boxes and snapshots &mdash; destructive, irreversible.
              Orphan boxes self-archive at TTL (cost already stopped), so this
              is hygiene + snapshot storage. Use the linkage badge to spot
              which resources are safe to nuke.
            </Typography>
          </Box>

          <Tabs
            value={tab}
            onChange={(_, v: TabKey) => setTab(v)}
            sx={{ borderBottom: 1, borderColor: 'divider' }}
            aria-label="Box cleanup tabs"
          >
            <Tab value="boxes" label="Boxes" />
            <Tab value="snapshots" label="Snapshots" />
          </Tabs>

          {tab === 'boxes' ? <BoxesTab /> : <SnapshotsTab />}
        </Stack>
  )
}
