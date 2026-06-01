---
name: tech-stack
description: .NET 9 and React 19 development patterns for this project. Use when: (1) writing React components, (2) creating backend features, (3) using MediatR/CQRS, (4) working with forms, (5) about to use useEffect, (6) using MUI Grid, (7) writing LINQ queries with division, (8) questions about project architecture or patterns
---

# Tech Stack Patterns

## Backend (.NET 9)

- **Vertical slices** - each feature self-contained
- **MediatR** - commands for writes, queries for reads
- **Result<T>** - no exceptions for business logic

## Frontend (React 19 + MUI)

- **TanStack Query** - never manual fetch calls
- **React Hook Form + Zod** - use generated schemas
- **Orval** - generates all API types

### AVOID useEffect

| Instead of useEffect for... | Use |
|----------------------------|-----|
| Fetching data | `useGetApi*` hooks |
| Syncing form with data | `defaultValues` or `values` prop |
| Computing values | `useMemo` or inline |

```tsx
// BAD - causes infinite loops
useEffect(() => { form.reset(data) }, [data, form])

// GOOD
const form = useForm({ values: data })
```

### NEVER use MUI Grid

```tsx
// BAD
<Grid container><Grid item xs={6}>...</Grid></Grid>

// GOOD
<Box sx={{ display: 'flex', gap: 2 }}>...</Box>
```

### MUI styling

Read **`instrument-mui`** skill for Instrument Mono theme variants (`pill`, `quietText`), `palette.instrument.*` shortcuts in `sx`, and when to use theme overrides vs design tokens. Official MUI decision tree: **`material-ui-styling`** skill.

### LINQ Division Pitfall

```csharp
// BAD - PostgreSQL division by zero
.Select(p => new { Rate = p.Won / p.Played })

// GOOD - calculate in memory
var data = await context.Players.ToListAsync();
var items = data.Select(p => new {
    Rate = p.Played > 0 ? (double)p.Won / p.Played : 0
});
```

### Controller Query Sync

Add filter params to BOTH Query record AND Controller method, or Swagger won't expose them.
