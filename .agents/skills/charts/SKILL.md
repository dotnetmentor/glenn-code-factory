# Charts (Recharts) - Complete Reference

## Overview

Data visualization using Recharts - a composable charting library built on D3. Fully responsive and declarative.

---

## 1. Installation

```bash
npm install recharts
```

---

## 2. Basic Charts

### Bar Chart
```tsx
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer
} from 'recharts'

const data = [
  { name: 'Jan', sales: 4000, revenue: 2400 },
  { name: 'Feb', sales: 3000, revenue: 1398 },
  { name: 'Mar', sales: 2000, revenue: 9800 },
]

function SalesChart() {
  return (
    <ResponsiveContainer width="100%" height={300}>
      <BarChart data={data}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis dataKey="name" />
        <YAxis />
        <Tooltip />
        <Legend />
        <Bar dataKey="sales" fill="#8884d8" radius={[4, 4, 0, 0]} />
        <Bar dataKey="revenue" fill="#82ca9d" radius={[4, 4, 0, 0]} />
      </BarChart>
    </ResponsiveContainer>
  )
}
```

### Line Chart
```tsx
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts'

function TrendChart({ data }: { data: { date: string; value: number }[] }) {
  return (
    <ResponsiveContainer width="100%" height={300}>
      <LineChart data={data}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis dataKey="date" />
        <YAxis />
        <Tooltip />
        <Line
          type="monotone"
          dataKey="value"
          stroke="#8884d8"
          strokeWidth={2}
          dot={{ fill: '#8884d8' }}
        />
      </LineChart>
    </ResponsiveContainer>
  )
}
```

### Area Chart
```tsx
import { AreaChart, Area, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts'

function RevenueChart({ data }) {
  return (
    <ResponsiveContainer width="100%" height={300}>
      <AreaChart data={data}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis dataKey="name" />
        <YAxis />
        <Tooltip />
        <Area
          type="monotone"
          dataKey="revenue"
          stroke="#82ca9d"
          fill="#82ca9d"
          fillOpacity={0.3}
        />
      </AreaChart>
    </ResponsiveContainer>
  )
}
```

### Pie Chart
```tsx
import { PieChart, Pie, Cell, Tooltip, Legend, ResponsiveContainer } from 'recharts'

const pieData = [
  { name: 'Desktop', value: 400, color: '#8884d8' },
  { name: 'Mobile', value: 300, color: '#82ca9d' },
  { name: 'Tablet', value: 200, color: '#ffc658' },
]

function DeviceChart() {
  return (
    <ResponsiveContainer width="100%" height={300}>
      <PieChart>
        <Pie
          data={pieData}
          cx="50%"
          cy="50%"
          innerRadius={60}  // Donut chart
          outerRadius={100}
          paddingAngle={2}
          dataKey="value"
          label={({ name, percent }) => `${name} ${(percent * 100).toFixed(0)}%`}
        >
          {pieData.map((entry, index) => (
            <Cell key={index} fill={entry.color} />
          ))}
        </Pie>
        <Tooltip />
        <Legend />
      </PieChart>
    </ResponsiveContainer>
  )
}
```

---

## 3. Multi-Series Chart

```tsx
function ComparisonChart({ data }) {
  return (
    <ResponsiveContainer width="100%" height={350}>
      <LineChart data={data}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis dataKey="name" />
        <YAxis />
        <Tooltip />
        <Legend />
        <Line type="monotone" dataKey="sales" stroke="#8884d8" strokeWidth={2} />
        <Line type="monotone" dataKey="revenue" stroke="#82ca9d" strokeWidth={2} />
        <Line type="monotone" dataKey="profit" stroke="#ffc658" strokeWidth={2} />
      </LineChart>
    </ResponsiveContainer>
  )
}
```

---

## 4. Custom Tooltip

```tsx
const CustomTooltip = ({ active, payload, label }) => {
  if (!active || !payload) return null

  return (
    <div className="bg-white p-2 border rounded shadow">
      <p className="font-bold">{label}</p>
      {payload.map((entry, i) => (
        <p key={i} style={{ color: entry.color }}>
          {entry.name}: ${entry.value.toLocaleString()}
        </p>
      ))}
    </div>
  )
}

<LineChart data={data}>
  <Tooltip content={<CustomTooltip />} />
  {/* ... */}
</LineChart>
```

---

## 5. Common Props

### ResponsiveContainer
Always wrap charts in `ResponsiveContainer` for responsive sizing.

### CartesianGrid
`strokeDasharray="3 3"` creates dashed grid lines.

### Axis
- `tickFormatter={(value) => `$${value}`}` - Format tick labels
- `domain={[0, 'dataMax']}` - Set axis range

### Colors
```tsx
const COLORS = ['#8884d8', '#82ca9d', '#ffc658', '#ff7300', '#00C49F']
```

---

## 6. Interactive Charts

```tsx
function InteractiveChart() {
  const [activeMetric, setActiveMetric] = useState<'sales' | 'revenue'>('sales')

  return (
    <div>
      <select value={activeMetric} onChange={e => setActiveMetric(e.target.value as any)}>
        <option value="sales">Sales</option>
        <option value="revenue">Revenue</option>
      </select>

      <ResponsiveContainer width="100%" height={300}>
        <BarChart data={data}>
          <XAxis dataKey="name" />
          <YAxis />
          <Tooltip />
          <Bar dataKey={activeMetric} fill="#8884d8" />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
```

---

## 7. Tips

- Always use `ResponsiveContainer` with percentage width
- Use `type="monotone"` for smooth curves
- Use `radius` prop on Bar for rounded corners
- Use `fillOpacity` on Area for transparency
