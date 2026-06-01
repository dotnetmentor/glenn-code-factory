import { useNavigate } from 'react-router-dom'
import {
  Box,
  ButtonBase,
  Card,
  CardContent,
  Container,
  Typography,
} from '@mui/material'
import TuneIcon from '@mui/icons-material/Tune'
import AutoAwesomeIcon from '@mui/icons-material/AutoAwesome'

/**
 * The two-tile chooser between manual stack picking and the AI-curated flow.
 * Centered, breathable; the spec is explicit that this surface should be
 * clarity-first rather than feature-rich (Spec 16, Scene 1/2).
 */
export function OnboardingChooser() {
  const navigate = useNavigate()

  return (
    <Container maxWidth="md" sx={{ py: 8 }}>
      <Box sx={{ textAlign: 'center', mb: 6 }}>
        <Typography variant="h3" component="h1" sx={{ fontWeight: 600, mb: 1 }}>
          New project
        </Typography>
        <Typography variant="body1" color="text.secondary">
          How do you want to set up your runtime?
        </Typography>
      </Box>

      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' },
          gap: 3,
        }}
      >
        <ChooserTile
          icon={<TuneIcon sx={{ fontSize: 40 }} />}
          title="Pick my stack"
          description="Languages and services you choose. For the technical user."
          onClick={() => navigate('/super-admin/projects/new/manual')}
        />
        <ChooserTile
          icon={<AutoAwesomeIcon sx={{ fontSize: 40 }} />}
          title="Let the agent figure it out"
          description="Describe your idea, the agent builds the stack."
          onClick={() => navigate('/super-admin/projects/new/ai')}
        />
      </Box>
    </Container>
  )
}

interface ChooserTileProps {
  icon: React.ReactNode
  title: string
  description: string
  onClick: () => void
}

function ChooserTile({ icon, title, description, onClick }: ChooserTileProps) {
  return (
    <ButtonBase
      onClick={onClick}
      sx={{
        display: 'block',
        textAlign: 'left',
        borderRadius: 2,
        width: '100%',
      }}
    >
      <Card
        variant="outlined"
        sx={{
          height: '100%',
          transition: 'border-color 120ms, transform 120ms',
          '&:hover': {
            borderColor: 'primary.main',
            transform: 'translateY(-2px)',
          },
        }}
      >
        <CardContent sx={{ p: 4, display: 'flex', flexDirection: 'column', gap: 2, minHeight: 200 }}>
          <Box sx={{ color: 'primary.main' }}>{icon}</Box>
          <Typography variant="h5" component="h2" sx={{ fontWeight: 600 }}>
            {title}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            {description}
          </Typography>
        </CardContent>
      </Card>
    </ButtonBase>
  )
}
