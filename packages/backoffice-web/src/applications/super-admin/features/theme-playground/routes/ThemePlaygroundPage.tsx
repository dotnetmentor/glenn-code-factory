import { useState } from 'react'
import {
  Box,
  Typography,
  Button,
  ButtonGroup,
  IconButton,
  Fab,
  TextField,
  Select,
  MenuItem,
  FormControl,
  InputLabel,
  Checkbox,
  Radio,
  RadioGroup,
  FormControlLabel,
  Switch,
  Slider,
  Autocomplete,
  Chip,
  Avatar,
  AvatarGroup,
  Badge,
  List,
  ListItem,
  ListItemText,
  ListItemIcon,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Tooltip,
  Divider,
  Alert,
  AlertTitle,
  LinearProgress,
  CircularProgress,
  Skeleton,
  Card,
  CardHeader,
  CardContent,
  CardActions,
  Paper,
  Accordion,
  AccordionSummary,
  AccordionDetails,
  Tabs,
  Tab,
  Breadcrumbs,
  Link,
  Stepper,
  Step,
  StepLabel,
  Pagination,
  Stack,
  useTheme,
  alpha,
  InputAdornment,
} from '@mui/material'
import { DataGrid } from '@mui/x-data-grid'
import type { GridColDef } from '@mui/x-data-grid'
import PaletteIcon from '@mui/icons-material/Palette'
import AddIcon from '@mui/icons-material/Add'
import EditIcon from '@mui/icons-material/Edit'
import DeleteIcon from '@mui/icons-material/Delete'
import StarIcon from '@mui/icons-material/Star'
import HomeIcon from '@mui/icons-material/Home'
import SettingsIcon from '@mui/icons-material/Settings'
import PersonIcon from '@mui/icons-material/Person'
import MailIcon from '@mui/icons-material/Mail'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import SearchIcon from '@mui/icons-material/Search'
import TrendingUpIcon from '@mui/icons-material/TrendingUp'
import PeopleIcon from '@mui/icons-material/People'
import ShoppingCartIcon from '@mui/icons-material/ShoppingCart'
import AttachMoneyIcon from '@mui/icons-material/AttachMoney'
import BarChartIcon from '@mui/icons-material/BarChart'
import AssignmentIcon from '@mui/icons-material/Assignment'
import RateReviewIcon from '@mui/icons-material/RateReview'
import InventoryIcon from '@mui/icons-material/Inventory'

// =============================================================================
// Theme Playground — read-only preview of the single global MUI theme.
//
// <p>Previously this page hosted a switcher that let you pick from 4 colour
// presets and 5 surface styles. That factory is gone — the app ships ONE
// theme, built from the workspace design tokens. This page now exists purely
// as a visual smoke-test: render every common MUI component so you can see
// at a glance that the theme handles everything cohesively.</p>
// =============================================================================

// ---------------------------------------------------------------------------
// Section wrapper for consistent styling (used in Color Palette & Components)
// ---------------------------------------------------------------------------
function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Paper variant="outlined" sx={{ p: 3, mb: 3 }}>
      <Typography variant="h5" sx={{ mb: 0.5 }}>
        {title}
      </Typography>
      <Divider sx={{ mb: 2.5 }} />
      {children}
    </Paper>
  )
}

function SubSection({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Box sx={{ mb: 2.5 }}>
      <Typography variant="overline" sx={{ mb: 1, display: 'block' }}>
        {label}
      </Typography>
      {children}
    </Box>
  )
}

// ---------------------------------------------------------------------------
// TabPanel helper
// ---------------------------------------------------------------------------
function TabPanel({ children, value, index }: { children: React.ReactNode; value: number; index: number }) {
  if (value !== index) return null
  return <Box sx={{ pt: 3 }}>{children}</Box>
}

// ---------------------------------------------------------------------------
// Autocomplete dummy data
// ---------------------------------------------------------------------------
const autoOptions = [
  { label: 'React' },
  { label: 'TypeScript' },
  { label: 'Material UI' },
  { label: 'Vite' },
  { label: 'TanStack Query' },
]

// ---------------------------------------------------------------------------
// CRUD Page dummy data
// ---------------------------------------------------------------------------
interface Product {
  id: number
  name: string
  category: string
  price: number
  status: 'Active' | 'Draft' | 'Archived'
  stock: number
  createdAt: string
}

const products: Product[] = [
  { id: 1, name: 'Wireless Headphones Pro', category: 'Electronics', price: 149.99, status: 'Active', stock: 234, createdAt: '2026-01-15' },
  { id: 2, name: 'Organic Cotton T-Shirt', category: 'Apparel', price: 34.99, status: 'Active', stock: 1200, createdAt: '2026-01-18' },
  { id: 3, name: 'Standing Desk Frame', category: 'Furniture', price: 399.00, status: 'Active', stock: 56, createdAt: '2026-01-20' },
  { id: 4, name: 'Ceramic Pour-Over Set', category: 'Kitchen', price: 42.50, status: 'Draft', stock: 0, createdAt: '2026-01-22' },
  { id: 5, name: 'Leather Notebook Cover', category: 'Accessories', price: 28.00, status: 'Active', stock: 340, createdAt: '2026-02-01' },
  { id: 6, name: 'Bluetooth Speaker Mini', category: 'Electronics', price: 59.99, status: 'Active', stock: 780, createdAt: '2026-02-03' },
  { id: 7, name: 'Recycled Glass Vase', category: 'Home Decor', price: 65.00, status: 'Archived', stock: 12, createdAt: '2026-02-05' },
  { id: 8, name: 'Merino Wool Socks (3-pack)', category: 'Apparel', price: 24.99, status: 'Active', stock: 2100, createdAt: '2026-02-08' },
  { id: 9, name: 'Minimalist Wall Clock', category: 'Home Decor', price: 89.00, status: 'Draft', stock: 0, createdAt: '2026-02-10' },
  { id: 10, name: 'Portable USB-C Hub', category: 'Electronics', price: 45.00, status: 'Active', stock: 430, createdAt: '2026-02-14' },
]

const productColumns: GridColDef<Product>[] = [
  { field: 'name', headerName: 'Product Name', flex: 1.5, minWidth: 200 },
  {
    field: 'category',
    headerName: 'Category',
    width: 140,
    renderCell: (params) => <Chip label={params.value} size="small" />,
  },
  { field: 'price', headerName: 'Price', width: 110, valueFormatter: (value: number) => `$${value.toFixed(2)}` },
  { field: 'stock', headerName: 'Stock', width: 90, type: 'number' },
  {
    field: 'status',
    headerName: 'Status',
    width: 120,
    renderCell: (params) => {
      const colorMap: Record<string, 'success' | 'warning' | 'default'> = {
        Active: 'success',
        Draft: 'warning',
        Archived: 'default',
      }
      return <Chip label={params.value} size="small" color={colorMap[params.value as string] ?? 'default'} />
    },
  },
  { field: 'createdAt', headerName: 'Created', width: 120 },
  {
    field: 'actions',
    headerName: 'Actions',
    width: 100,
    sortable: false,
    renderCell: () => (
      <Stack direction="row" spacing={0.5}>
        <IconButton size="small"><EditIcon fontSize="small" /></IconButton>
        <IconButton size="small" color="error"><DeleteIcon fontSize="small" /></IconButton>
      </Stack>
    ),
  },
]

// ---------------------------------------------------------------------------
// Dashboard dummy data
// ---------------------------------------------------------------------------
interface Order {
  id: string
  customer: string
  amount: number
  status: 'Completed' | 'Processing' | 'Pending' | 'Cancelled'
  date: string
}

const recentOrders: Order[] = [
  { id: 'ORD-4821', customer: 'Alice Johnson', amount: 234.50, status: 'Completed', date: 'Feb 19, 2026' },
  { id: 'ORD-4820', customer: 'Marcus Chen', amount: 89.99, status: 'Processing', date: 'Feb 19, 2026' },
  { id: 'ORD-4819', customer: 'Sarah Williams', amount: 412.00, status: 'Completed', date: 'Feb 18, 2026' },
  { id: 'ORD-4818', customer: 'James Miller', amount: 67.25, status: 'Pending', date: 'Feb 18, 2026' },
  { id: 'ORD-4817', customer: 'Emma Davis', amount: 158.75, status: 'Cancelled', date: 'Feb 17, 2026' },
  { id: 'ORD-4816', customer: 'Robert Taylor', amount: 299.00, status: 'Completed', date: 'Feb 17, 2026' },
]

const orderStatusColor: Record<string, 'success' | 'info' | 'warning' | 'error'> = {
  Completed: 'success',
  Processing: 'info',
  Pending: 'warning',
  Cancelled: 'error',
}

// ---------------------------------------------------------------------------
// Tab: Color Palette
// ---------------------------------------------------------------------------
function ColorPaletteTab() {
  const theme = useTheme()

  return (
    <Section title="Color Palette">
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
        {(
          [
            { name: 'Primary', palette: theme.palette.primary },
            { name: 'Secondary', palette: theme.palette.secondary },
            { name: 'Error', palette: theme.palette.error },
            { name: 'Warning', palette: theme.palette.warning },
            { name: 'Info', palette: theme.palette.info },
            { name: 'Success', palette: theme.palette.success },
          ] as const
        ).map(({ name, palette }) => (
          <Box key={name}>
            <Typography variant="subtitle2" sx={{ mb: 1 }}>
              {name}
            </Typography>
            <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap' }}>
              {(
                [
                  { label: 'Light', value: palette.light },
                  { label: 'Main', value: palette.main },
                  { label: 'Dark', value: palette.dark },
                  { label: 'Contrast', value: palette.contrastText },
                ] as const
              ).map(({ label, value }) => (
                <Box key={label} sx={{ textAlign: 'center' }}>
                  <Box
                    sx={{
                      width: 72,
                      height: 48,
                      borderRadius: 1.5,
                      bgcolor: value,
                      border: '1px solid',
                      borderColor: 'divider',
                      mb: 0.5,
                    }}
                  />
                  <Typography variant="caption" sx={{ display: 'block', fontSize: '0.7rem' }}>
                    {label}
                  </Typography>
                  <Typography
                    variant="caption"
                    sx={{
                      fontFamily: 'monospace',
                      fontSize: '0.65rem',
                      color: 'text.disabled',
                    }}
                  >
                    {value}
                  </Typography>
                </Box>
              ))}
            </Box>
          </Box>
        ))}

        {/* Neutral / grey palette */}
        <Box>
          <Typography variant="subtitle2" sx={{ mb: 1 }}>
            Neutral / Grey
          </Typography>
          <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap' }}>
            {(
              Object.entries(theme.palette.grey) as [string, string][]
            ).map(([key, value]) => (
              <Box key={key} sx={{ textAlign: 'center' }}>
                <Box
                  sx={{
                    width: 52,
                    height: 36,
                    borderRadius: 1,
                    bgcolor: value,
                    border: '1px solid',
                    borderColor: 'divider',
                    mb: 0.25,
                  }}
                />
                <Typography variant="caption" sx={{ display: 'block', fontSize: '0.6rem' }}>
                  {key}
                </Typography>
              </Box>
            ))}
          </Box>
        </Box>

        {/* Background & text */}
        <Box>
          <Typography variant="subtitle2" sx={{ mb: 1 }}>
            Background & Text
          </Typography>
          <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap' }}>
            {[
              { label: 'bg.default', value: theme.palette.background.default },
              { label: 'bg.paper', value: theme.palette.background.paper },
              { label: 'text.primary', value: theme.palette.text.primary },
              { label: 'text.secondary', value: theme.palette.text.secondary },
              { label: 'text.disabled', value: theme.palette.text.disabled },
              { label: 'divider', value: theme.palette.divider },
            ].map(({ label, value }) => (
              <Box key={label} sx={{ textAlign: 'center' }}>
                <Box
                  sx={{
                    width: 72,
                    height: 48,
                    borderRadius: 1.5,
                    bgcolor: value,
                    border: '1px solid',
                    borderColor: 'divider',
                    mb: 0.5,
                  }}
                />
                <Typography variant="caption" sx={{ display: 'block', fontSize: '0.65rem' }}>
                  {label}
                </Typography>
                <Typography
                  variant="caption"
                  sx={{ fontFamily: 'monospace', fontSize: '0.6rem', color: 'text.disabled' }}
                >
                  {value}
                </Typography>
              </Box>
            ))}
          </Box>
        </Box>
      </Box>
    </Section>
  )
}

// ---------------------------------------------------------------------------
// Tab: Components
// ---------------------------------------------------------------------------
function ComponentsTab() {
  const theme = useTheme()
  const [compTabValue, setCompTabValue] = useState(0)
  const [activeStep, setActiveStep] = useState(1)
  const [sliderValue, setSliderValue] = useState<number>(40)
  const [switchChecked, setSwitchChecked] = useState(true)
  const [radioValue, setRadioValue] = useState('option1')

  return (
    <>
      {/* Typography */}
      <Section title="Typography">
        <Stack spacing={1.5}>
          {(
            ['h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'subtitle1', 'subtitle2', 'body1', 'body2', 'caption', 'overline'] as const
          ).map((variant) => (
            <Box key={variant} sx={{ display: 'flex', alignItems: 'baseline', gap: 2 }}>
              <Typography
                variant="caption"
                sx={{
                  fontFamily: 'monospace',
                  fontSize: '0.7rem',
                  color: 'text.disabled',
                  minWidth: 72,
                  textAlign: 'right',
                  flexShrink: 0,
                }}
              >
                {variant}
              </Typography>
              <Typography variant={variant}>
                {variant.startsWith('h')
                  ? `Heading ${variant.slice(1)} - The quick brown fox`
                  : variant === 'overline'
                    ? 'Overline text'
                    : `${variant.charAt(0).toUpperCase() + variant.slice(1)} - The quick brown fox jumps over the lazy dog`}
              </Typography>
            </Box>
          ))}
        </Stack>
      </Section>

      {/* Buttons */}
      <Section title="Buttons">
        <SubSection label="Contained">
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
            {(['primary', 'secondary', 'error', 'warning', 'info', 'success'] as const).map((c) => (
              <Button key={c} variant="contained" color={c}>
                {c}
              </Button>
            ))}
            <Button variant="contained" disabled>
              Disabled
            </Button>
          </Stack>
        </SubSection>

        <SubSection label="Outlined">
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
            {(['primary', 'secondary', 'error', 'warning', 'info', 'success'] as const).map((c) => (
              <Button key={c} variant="outlined" color={c}>
                {c}
              </Button>
            ))}
            <Button variant="outlined" disabled>
              Disabled
            </Button>
          </Stack>
        </SubSection>

        <SubSection label="Text">
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
            {(['primary', 'secondary', 'error', 'warning', 'info', 'success'] as const).map((c) => (
              <Button key={c} variant="text" color={c}>
                {c}
              </Button>
            ))}
            <Button variant="text" disabled>
              Disabled
            </Button>
          </Stack>
        </SubSection>

        <SubSection label="Sizes">
          <Stack direction="row" spacing={1} alignItems="center">
            <Button variant="contained" size="small">Small</Button>
            <Button variant="contained" size="medium">Medium</Button>
            <Button variant="contained" size="large">Large</Button>
          </Stack>
        </SubSection>

        <SubSection label="With Icons">
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
            <Button variant="contained" startIcon={<AddIcon />}>Create</Button>
            <Button variant="outlined" startIcon={<EditIcon />}>Edit</Button>
            <Button variant="text" color="error" startIcon={<DeleteIcon />}>Delete</Button>
          </Stack>
        </SubSection>

        <SubSection label="Icon Buttons">
          <Stack direction="row" spacing={1} alignItems="center">
            <IconButton color="primary"><AddIcon /></IconButton>
            <IconButton color="secondary"><EditIcon /></IconButton>
            <IconButton color="error"><DeleteIcon /></IconButton>
            <IconButton disabled><StarIcon /></IconButton>
          </Stack>
        </SubSection>

        <SubSection label="Button Group">
          <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
            <ButtonGroup variant="contained">
              <Button>One</Button>
              <Button>Two</Button>
              <Button>Three</Button>
            </ButtonGroup>
            <ButtonGroup variant="outlined">
              <Button>One</Button>
              <Button>Two</Button>
              <Button>Three</Button>
            </ButtonGroup>
          </Stack>
        </SubSection>

        <SubSection label="Loading Button (simulated)">
          <Button variant="contained" disabled>
            <CircularProgress size={16} sx={{ mr: 1, color: 'inherit' }} />
            Loading...
          </Button>
        </SubSection>

        <SubSection label="Floating Action Buttons">
          <Stack direction="row" spacing={2} alignItems="center">
            <Fab color="primary" size="small"><AddIcon /></Fab>
            <Fab color="primary" size="medium"><AddIcon /></Fab>
            <Fab color="primary" size="large"><AddIcon /></Fab>
            <Fab variant="extended" color="primary" size="medium">
              <AddIcon sx={{ mr: 1 }} />
              Create
            </Fab>
          </Stack>
        </SubSection>
      </Section>

      {/* Form Inputs */}
      <Section title="Form Inputs">
        <SubSection label="Text Fields">
          <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
            <TextField label="Outlined" variant="outlined" defaultValue="Hello" />
            <TextField label="Filled" variant="filled" defaultValue="Hello" />
            <TextField label="Standard" variant="standard" defaultValue="Hello" />
            <TextField label="Disabled" disabled defaultValue="Disabled" />
            <TextField label="Error" error helperText="Something went wrong" />
            <TextField label="Multiline" multiline rows={2} defaultValue="Multiple lines..." />
          </Stack>
        </SubSection>

        <SubSection label="Select">
          <FormControl sx={{ minWidth: 180 }}>
            <InputLabel>Role</InputLabel>
            <Select label="Role" defaultValue="admin">
              <MenuItem value="admin">Admin</MenuItem>
              <MenuItem value="editor">Editor</MenuItem>
              <MenuItem value="viewer">Viewer</MenuItem>
            </Select>
          </FormControl>
        </SubSection>

        <SubSection label="Checkbox, Radio, Switch">
          <Stack direction="row" spacing={3} alignItems="flex-start" flexWrap="wrap" useFlexGap>
            <Box>
              <FormControlLabel control={<Checkbox defaultChecked />} label="Checked" />
              <FormControlLabel control={<Checkbox />} label="Unchecked" />
              <FormControlLabel control={<Checkbox disabled />} label="Disabled" />
              <FormControlLabel control={<Checkbox indeterminate />} label="Indeterminate" />
            </Box>
            <Box>
              <RadioGroup value={radioValue} onChange={(e) => setRadioValue(e.target.value)}>
                <FormControlLabel value="option1" control={<Radio />} label="Option 1" />
                <FormControlLabel value="option2" control={<Radio />} label="Option 2" />
                <FormControlLabel value="option3" control={<Radio disabled />} label="Disabled" />
              </RadioGroup>
            </Box>
            <Box>
              <FormControlLabel
                control={<Switch checked={switchChecked} onChange={(e) => setSwitchChecked(e.target.checked)} />}
                label="Enabled"
              />
              <FormControlLabel control={<Switch disabled />} label="Disabled" />
            </Box>
          </Stack>
        </SubSection>

        <SubSection label="Slider">
          <Box sx={{ maxWidth: 320, px: 1 }}>
            <Slider
              value={sliderValue}
              onChange={(_, v) => setSliderValue(v as number)}
              valueLabelDisplay="auto"
            />
            <Slider defaultValue={[20, 60]} valueLabelDisplay="auto" />
            <Slider disabled defaultValue={30} />
          </Box>
        </SubSection>

        <SubSection label="Autocomplete">
          <Autocomplete
            options={autoOptions}
            sx={{ maxWidth: 320 }}
            renderInput={(params) => <TextField {...params} label="Technology" />}
          />
        </SubSection>
      </Section>

      {/* Data Display */}
      <Section title="Data Display">
        <SubSection label="Chips">
          <Stack spacing={1.5}>
            <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
              <Chip label="Default" />
              <Chip label="Primary" color="primary" />
              <Chip label="Secondary" color="secondary" />
              <Chip label="Success" color="success" />
              <Chip label="Error" color="error" />
              <Chip label="Warning" color="warning" />
              <Chip label="Info" color="info" />
            </Stack>
            <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
              <Chip label="Outlined" variant="outlined" />
              <Chip label="Primary" variant="outlined" color="primary" />
              <Chip label="Deletable" onDelete={() => {}} />
              <Chip label="Clickable" onClick={() => {}} />
              <Chip avatar={<Avatar>J</Avatar>} label="With Avatar" />
              <Chip icon={<StarIcon />} label="With Icon" color="primary" />
              <Chip label="Disabled" disabled />
            </Stack>
          </Stack>
        </SubSection>

        <SubSection label="Avatars">
          <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap" useFlexGap>
            <Avatar>JD</Avatar>
            <Avatar sx={{ bgcolor: 'primary.main', color: 'primary.contrastText' }}>AB</Avatar>
            <Avatar sx={{ bgcolor: 'error.main', color: 'error.contrastText' }}><PersonIcon /></Avatar>
            <Avatar sx={{ bgcolor: 'success.main', color: 'success.contrastText', width: 48, height: 48 }}>LG</Avatar>
            <AvatarGroup max={4}>
              <Avatar>A</Avatar>
              <Avatar>B</Avatar>
              <Avatar>C</Avatar>
              <Avatar>D</Avatar>
              <Avatar>E</Avatar>
            </AvatarGroup>
          </Stack>
        </SubSection>

        <SubSection label="Badges">
          <Stack direction="row" spacing={3} alignItems="center">
            <Badge badgeContent={4} color="primary"><MailIcon /></Badge>
            <Badge badgeContent={99} color="error"><MailIcon /></Badge>
            <Badge variant="dot" color="success"><MailIcon /></Badge>
            <Badge badgeContent={0} showZero color="warning"><MailIcon /></Badge>
          </Stack>
        </SubSection>

        <SubSection label="List">
          <Paper variant="outlined" sx={{ maxWidth: 360 }}>
            <List dense>
              {[
                { icon: <HomeIcon />, primary: 'Dashboard', secondary: 'Overview & analytics' },
                { icon: <PersonIcon />, primary: 'Users', secondary: 'Manage team members' },
                { icon: <SettingsIcon />, primary: 'Settings', secondary: 'Application config' },
              ].map((item) => (
                <ListItem key={item.primary}>
                  <ListItemIcon sx={{ minWidth: 36 }}>{item.icon}</ListItemIcon>
                  <ListItemText primary={item.primary} secondary={item.secondary} />
                </ListItem>
              ))}
            </List>
          </Paper>
        </SubSection>

        <SubSection label="Table">
          <TableContainer component={Paper} variant="outlined" sx={{ maxWidth: 560 }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Name</TableCell>
                  <TableCell>Role</TableCell>
                  <TableCell align="right">Status</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {[
                  { name: 'Alice Johnson', role: 'Admin', status: 'Active' },
                  { name: 'Bob Smith', role: 'Editor', status: 'Active' },
                  { name: 'Carol White', role: 'Viewer', status: 'Inactive' },
                ].map((row) => (
                  <TableRow key={row.name}>
                    <TableCell>
                      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                        <Avatar sx={{ width: 24, height: 24, fontSize: '0.75rem' }}>
                          {row.name[0]}
                        </Avatar>
                        {row.name}
                      </Box>
                    </TableCell>
                    <TableCell>{row.role}</TableCell>
                    <TableCell align="right">
                      <Chip
                        label={row.status}
                        size="small"
                        color={row.status === 'Active' ? 'success' : 'default'}
                      />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
        </SubSection>

        <SubSection label="Tooltip">
          <Stack direction="row" spacing={2}>
            <Tooltip title="This is a tooltip" arrow>
              <Button variant="outlined">Hover me</Button>
            </Tooltip>
            <Tooltip title="Top placement" placement="top" arrow>
              <Chip label="Top tooltip" />
            </Tooltip>
          </Stack>
        </SubSection>

        <SubSection label="Divider">
          <Box sx={{ maxWidth: 360 }}>
            <Typography variant="body2">Above the divider</Typography>
            <Divider sx={{ my: 1 }} />
            <Typography variant="body2">Below the divider</Typography>
            <Divider sx={{ my: 1 }}>
              <Chip label="OR" size="small" />
            </Divider>
            <Typography variant="body2">With chip in divider</Typography>
          </Box>
        </SubSection>
      </Section>

      {/* Feedback */}
      <Section title="Feedback">
        <SubSection label="Alerts">
          <Stack spacing={1.5}>
            {(['success', 'info', 'warning', 'error'] as const).map((severity) => (
              <Alert key={severity} severity={severity}>
                <AlertTitle sx={{ textTransform: 'capitalize', fontWeight: 600, fontSize: '0.875rem' }}>
                  {severity}
                </AlertTitle>
                This is a {severity} alert with a descriptive message.
              </Alert>
            ))}
            <Alert severity="info" variant="outlined">
              Outlined variant alert
            </Alert>
            <Alert severity="success" variant="filled">
              Filled variant alert
            </Alert>
            {(['success', 'error', 'warning', 'info'] as const).map((severity) => (
              <Alert key={`filled-${severity}`} severity={severity} variant="filled">
                Filled {severity} alert - text should be white on colored background.
              </Alert>
            ))}
          </Stack>
        </SubSection>

        <SubSection label="Progress">
          <Stack spacing={2.5} sx={{ maxWidth: 400 }}>
            <Box>
              <Typography variant="caption" sx={{ mb: 0.5, display: 'block' }}>Linear (indeterminate)</Typography>
              <LinearProgress />
            </Box>
            <Box>
              <Typography variant="caption" sx={{ mb: 0.5, display: 'block' }}>Linear (determinate 60%)</Typography>
              <LinearProgress variant="determinate" value={60} />
            </Box>
            <Stack direction="row" spacing={3} alignItems="center">
              <Box sx={{ textAlign: 'center' }}>
                <CircularProgress size={32} />
                <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>Default</Typography>
              </Box>
              <Box sx={{ textAlign: 'center' }}>
                <CircularProgress size={32} variant="determinate" value={75} />
                <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>75%</Typography>
              </Box>
              <Box sx={{ textAlign: 'center' }}>
                <CircularProgress size={32} color="secondary" />
                <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>Secondary</Typography>
              </Box>
            </Stack>
          </Stack>
        </SubSection>

        <SubSection label="Skeleton">
          <Stack spacing={1} sx={{ maxWidth: 360 }}>
            <Skeleton variant="text" width="80%" />
            <Skeleton variant="text" width="60%" />
            <Stack direction="row" spacing={2} alignItems="center">
              <Skeleton variant="circular" width={40} height={40} />
              <Box sx={{ flex: 1 }}>
                <Skeleton variant="text" width="90%" />
                <Skeleton variant="text" width="50%" />
              </Box>
            </Stack>
            <Skeleton variant="rectangular" height={80} sx={{ borderRadius: 1 }} />
            <Skeleton variant="rounded" height={48} sx={{ borderRadius: 2 }} />
          </Stack>
        </SubSection>
      </Section>

      {/* Surfaces */}
      <Section title="Surfaces">
        <SubSection label="Card">
          <Box sx={{ display: 'flex', gap: 2, flexWrap: 'wrap' }}>
            <Card sx={{ maxWidth: 320, flex: '1 1 280px' }}>
              <CardHeader
                avatar={<Avatar sx={{ bgcolor: 'primary.main', color: 'primary.contrastText' }}>P</Avatar>}
                title="Project Alpha"
                subheader="Last updated 2 hours ago"
              />
              <CardContent>
                <Typography variant="body2">
                  A sample card showcasing the CardHeader, CardContent, and CardActions regions with the current theme styling.
                </Typography>
              </CardContent>
              <CardActions>
                <Button size="small">Learn More</Button>
                <Button size="small" color="primary">Action</Button>
              </CardActions>
            </Card>

            <Card sx={{ maxWidth: 320, flex: '1 1 280px' }}>
              <CardContent>
                <Typography variant="h6" sx={{ mb: 1 }}>
                  Simple Card
                </Typography>
                <Typography variant="body2">
                  Cards contain content and actions about a single subject. They are entry points to more detailed information.
                </Typography>
              </CardContent>
              <CardActions>
                <Button size="small">Share</Button>
                <Button size="small" color="error">Delete</Button>
              </CardActions>
            </Card>
          </Box>
        </SubSection>

        <SubSection label="Paper Elevations">
          <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
            {[0, 1, 2, 3].map((elev) => (
              <Paper
                key={elev}
                elevation={elev}
                sx={{
                  width: 120,
                  height: 80,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                <Typography variant="caption">elevation={elev}</Typography>
              </Paper>
            ))}
          </Stack>
        </SubSection>

        <SubSection label="Accordion">
          <Box sx={{ maxWidth: 560 }}>
            <Stack spacing={1}>
              {[
                { title: 'General Settings', body: 'Configure your account name, email, and default preferences here.' },
                { title: 'Notifications', body: 'Manage your email and push notification preferences across all channels.' },
                { title: 'Privacy & Security', body: 'Control who can see your profile and manage two-factor authentication.' },
              ].map((item, i) => (
                <Accordion key={i} defaultExpanded={i === 0}>
                  <AccordionSummary expandIcon={<ExpandMoreIcon />}>
                    <Typography variant="subtitle2">{item.title}</Typography>
                  </AccordionSummary>
                  <AccordionDetails>
                    <Typography variant="body2">{item.body}</Typography>
                  </AccordionDetails>
                </Accordion>
              ))}
            </Stack>
          </Box>
        </SubSection>
      </Section>

      {/* Navigation */}
      <Section title="Navigation">
        <SubSection label="Tabs">
          <Box sx={{ borderBottom: 1, borderColor: 'divider', maxWidth: 480 }}>
            <Tabs value={compTabValue} onChange={(_, v) => setCompTabValue(v)}>
              <Tab label="Overview" />
              <Tab label="Analytics" />
              <Tab label="Settings" />
              <Tab label="Disabled" disabled />
            </Tabs>
          </Box>
          <Box sx={{ p: 2 }}>
            <Typography variant="body2" color="text.secondary">
              Tab panel content for tab index {compTabValue}.
            </Typography>
          </Box>
        </SubSection>

        <SubSection label="Breadcrumbs">
          <Stack spacing={1.5}>
            <Breadcrumbs separator={<NavigateNextIcon fontSize="small" />}>
              <Link underline="hover" color="inherit" href="#" onClick={(e: React.MouseEvent) => e.preventDefault()}>
                Home
              </Link>
              <Link underline="hover" color="inherit" href="#" onClick={(e: React.MouseEvent) => e.preventDefault()}>
                Projects
              </Link>
              <Typography color="text.primary">Current Page</Typography>
            </Breadcrumbs>
            <Breadcrumbs>
              <Link underline="hover" color="inherit" href="#" onClick={(e: React.MouseEvent) => e.preventDefault()}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                  <HomeIcon sx={{ fontSize: 16 }} /> Home
                </Box>
              </Link>
              <Link underline="hover" color="inherit" href="#" onClick={(e: React.MouseEvent) => e.preventDefault()}>
                Library
              </Link>
              <Typography color="text.primary">Data</Typography>
            </Breadcrumbs>
          </Stack>
        </SubSection>

        <SubSection label="Stepper">
          <Box sx={{ maxWidth: 560 }}>
            <Stepper activeStep={activeStep}>
              {['Select plan', 'Configure', 'Review & deploy'].map((label) => (
                <Step key={label}>
                  <StepLabel>{label}</StepLabel>
                </Step>
              ))}
            </Stepper>
            <Stack direction="row" spacing={1} sx={{ mt: 2 }}>
              <Button
                variant="outlined"
                size="small"
                disabled={activeStep === 0}
                onClick={() => setActiveStep((s) => s - 1)}
              >
                Back
              </Button>
              <Button
                variant="contained"
                size="small"
                disabled={activeStep === 2}
                onClick={() => setActiveStep((s) => s + 1)}
              >
                Next
              </Button>
            </Stack>
          </Box>
        </SubSection>

        <SubSection label="Pagination">
          <Stack spacing={1.5}>
            <Pagination count={10} color="primary" />
            <Pagination count={10} variant="outlined" shape="rounded" />
          </Stack>
        </SubSection>
      </Section>

      {/* Layout */}
      <Section title="Layout">
        <SubSection label="Stack (horizontal)">
          <Stack direction="row" spacing={1}>
            {[1, 2, 3, 4].map((n) => (
              <Box
                key={n}
                sx={{
                  width: 64,
                  height: 40,
                  borderRadius: 1,
                  bgcolor: alpha(theme.palette.primary.main, 0.08 + n * 0.06),
                  border: '1px solid',
                  borderColor: alpha(theme.palette.primary.main, 0.2),
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                <Typography variant="caption" sx={{ fontWeight: 600 }}>{n}</Typography>
              </Box>
            ))}
          </Stack>
        </SubSection>

        <SubSection label="Grid-like layout (Box with flex)">
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
            {[
              { w: '100%', label: 'Full width' },
              { w: 'calc(50% - 4px)', label: '1/2' },
              { w: 'calc(50% - 4px)', label: '1/2' },
              { w: 'calc(33.33% - 6px)', label: '1/3' },
              { w: 'calc(33.33% - 6px)', label: '1/3' },
              { w: 'calc(33.33% - 6px)', label: '1/3' },
              { w: 'calc(25% - 6px)', label: '1/4' },
              { w: 'calc(25% - 6px)', label: '1/4' },
              { w: 'calc(25% - 6px)', label: '1/4' },
              { w: 'calc(25% - 6px)', label: '1/4' },
            ].map((item, i) => (
              <Box
                key={i}
                sx={{
                  width: item.w,
                  height: 40,
                  borderRadius: 1,
                  bgcolor: alpha(theme.palette.info.main, 0.08),
                  border: '1px solid',
                  borderColor: alpha(theme.palette.info.main, 0.2),
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                <Typography variant="caption" sx={{ fontWeight: 500 }}>{item.label}</Typography>
              </Box>
            ))}
          </Box>
        </SubSection>
      </Section>
    </>
  )
}

// ---------------------------------------------------------------------------
// Tab: CRUD Page
// ---------------------------------------------------------------------------
function CrudPageTab() {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
      {/* Page header */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h4">Products</Typography>
          <Typography variant="body2">Manage your product catalog, pricing, and inventory.</Typography>
        </Box>
        <Button variant="contained" startIcon={<AddIcon />}>
          New Product
        </Button>
      </Box>

      {/* Search bar */}
      <TextField
        placeholder="Search products..."
        size="small"
        slotProps={{
          input: {
            startAdornment: (
              <InputAdornment position="start">
                <SearchIcon />
              </InputAdornment>
            ),
          },
        }}
        sx={{ maxWidth: 360 }}
      />

      {/* Data grid */}
      <Paper variant="outlined" sx={{ display: 'flex', flexDirection: 'column' }}>
        <DataGrid
          rows={products}
          columns={productColumns}
          pageSizeOptions={[5, 10, 25]}
          initialState={{
            pagination: { paginationModel: { pageSize: 5 } },
          }}
          autoHeight
        />
      </Paper>
    </Box>
  )
}

// ---------------------------------------------------------------------------
// Tab: Dashboard
// ---------------------------------------------------------------------------
function DashboardTab() {
  const stats = [
    { label: 'Total Revenue', value: '$48,290', change: '+12.5%', icon: <AttachMoneyIcon /> },
    { label: 'Active Users', value: '2,847', change: '+8.2%', icon: <PeopleIcon /> },
    { label: 'Orders', value: '1,234', change: '+23.1%', icon: <ShoppingCartIcon /> },
    { label: 'Conversion Rate', value: '3.24%', change: '-0.4%', icon: <BarChartIcon /> },
  ]

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
      {/* Welcome alert */}
      <Alert severity="info">
        <AlertTitle>Welcome back!</AlertTitle>
        You have 3 pending reviews and 12 new orders since your last visit.
      </Alert>

      {/* Stat cards */}
      <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 2 }}>
        {stats.map((stat) => (
          <Card key={stat.label}>
            <CardContent>
              <Stack direction="row" justifyContent="space-between" alignItems="flex-start">
                <Box>
                  <Typography variant="body2">{stat.label}</Typography>
                  <Typography variant="h4">{stat.value}</Typography>
                  <Chip
                    label={stat.change}
                    size="small"
                    color={stat.change.startsWith('+') ? 'success' : 'error'}
                    icon={<TrendingUpIcon />}
                  />
                </Box>
                <Avatar>{stat.icon}</Avatar>
              </Stack>
            </CardContent>
          </Card>
        ))}
      </Box>

      {/* Recent Orders */}
      <Card>
        <CardHeader title="Recent Orders" subheader="Latest transactions from your store" />
        <TableContainer>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Order ID</TableCell>
                <TableCell>Customer</TableCell>
                <TableCell>Date</TableCell>
                <TableCell align="right">Amount</TableCell>
                <TableCell align="right">Status</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {recentOrders.map((order) => (
                <TableRow key={order.id}>
                  <TableCell>
                    <Typography variant="body2">{order.id}</Typography>
                  </TableCell>
                  <TableCell>
                    <Stack direction="row" spacing={1} alignItems="center">
                      <Avatar sx={{ width: 28, height: 28 }}>
                        {order.customer[0]}
                      </Avatar>
                      <Typography variant="body2">{order.customer}</Typography>
                    </Stack>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">{order.date}</Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Typography variant="body2">${order.amount.toFixed(2)}</Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Chip label={order.status} size="small" color={orderStatusColor[order.status]} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      </Card>

      {/* Quick Actions */}
      <Card>
        <CardHeader title="Quick Actions" subheader="Common tasks you can do right now" />
        <CardContent>
          <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
            <Button variant="outlined" startIcon={<AddIcon />}>
              Add Product
            </Button>
            <Button variant="outlined" startIcon={<InventoryIcon />}>
              View Inventory
            </Button>
            <Button variant="outlined" startIcon={<AssignmentIcon />}>
              Generate Report
            </Button>
            <Button variant="outlined" startIcon={<RateReviewIcon />}>
              Pending Reviews
            </Button>
          </Stack>
        </CardContent>
      </Card>
    </Box>
  )
}

// ---------------------------------------------------------------------------
// Main page
// ---------------------------------------------------------------------------
export function ThemePlaygroundPage() {
  const [activeTab, setActiveTab] = useState(0)

  return (
    <Box sx={{ maxWidth: 1100, mx: 'auto', py: 4, px: 2 }}>
      {/* Page header */}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, mb: 3 }}>
        <PaletteIcon sx={{ fontSize: 28, color: 'text.secondary' }} />
        <Box>
          <Typography variant="h3">Theme Playground</Typography>
          <Typography variant="body2" sx={{ mt: 0.25 }}>
            A comprehensive preview of all MUI components with the single global theme applied.
          </Typography>
        </Box>
      </Box>

      {/* Tabs */}
      <Box sx={{ borderBottom: 1, borderColor: 'divider' }}>
        <Tabs value={activeTab} onChange={(_, v) => setActiveTab(v)}>
          <Tab label="Color Palette" />
          <Tab label="Components" />
          <Tab label="CRUD Page" />
          <Tab label="Dashboard" />
        </Tabs>
      </Box>

      {/* Tab panels */}
      <TabPanel value={activeTab} index={0}>
        <ColorPaletteTab />
      </TabPanel>

      <TabPanel value={activeTab} index={1}>
        <ComponentsTab />
      </TabPanel>

      <TabPanel value={activeTab} index={2}>
        <CrudPageTab />
      </TabPanel>

      <TabPanel value={activeTab} index={3}>
        <DashboardTab />
      </TabPanel>
    </Box>
  )
}
