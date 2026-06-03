# PDF Generation - Complete Reference

## Overview

Client-side PDF generation using `@react-pdf/renderer`. No server required - PDFs are generated entirely in the browser.

---

## 1. Installation

```bash
npm install @react-pdf/renderer
```

---

## 2. Basic PDF Component

```tsx
import { Document, Page, Text, View, StyleSheet } from '@react-pdf/renderer'

const styles = StyleSheet.create({
  page: {
    padding: 40,
    fontSize: 11,
    fontFamily: 'Helvetica',
  },
  title: {
    fontSize: 24,
    fontWeight: 'bold',
    marginBottom: 20,
  },
  section: {
    marginBottom: 15,
  },
  row: {
    flexDirection: 'row',
    marginBottom: 4,
  },
  label: {
    width: 100,
    color: '#666',
  },
  value: {
    flex: 1,
  },
})

interface InvoiceData {
  invoiceNumber: string
  date: string
  items: { description: string; quantity: number; price: number }[]
}

export function InvoicePDF({ data }: { data: InvoiceData }) {
  const total = data.items.reduce((sum, item) => sum + item.quantity * item.price, 0)

  return (
    <Document>
      <Page size="A4" style={styles.page}>
        <Text style={styles.title}>INVOICE</Text>

        <View style={styles.section}>
          <View style={styles.row}>
            <Text style={styles.label}>Invoice #:</Text>
            <Text style={styles.value}>{data.invoiceNumber}</Text>
          </View>
          <View style={styles.row}>
            <Text style={styles.label}>Date:</Text>
            <Text style={styles.value}>{data.date}</Text>
          </View>
        </View>

        {/* Table */}
        <View style={{ marginTop: 20 }}>
          {data.items.map((item, i) => (
            <View key={i} style={styles.row}>
              <Text style={{ flex: 2 }}>{item.description}</Text>
              <Text style={{ flex: 1 }}>{item.quantity}</Text>
              <Text style={{ flex: 1 }}>${item.price}</Text>
              <Text style={{ flex: 1 }}>${item.quantity * item.price}</Text>
            </View>
          ))}
        </View>

        <Text style={{ marginTop: 20, fontWeight: 'bold' }}>
          Total: ${total.toFixed(2)}
        </Text>
      </Page>
    </Document>
  )
}
```

---

## 3. Download PDF

```tsx
import { pdf } from '@react-pdf/renderer'

function DownloadButton({ data }: { data: InvoiceData }) {
  const [isGenerating, setIsGenerating] = useState(false)

  const handleDownload = async () => {
    setIsGenerating(true)
    try {
      // Generate PDF blob
      const blob = await pdf(<InvoicePDF data={data} />).toBlob()

      // Create download link
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `${data.invoiceNumber}.pdf`
      link.click()

      // Cleanup
      URL.revokeObjectURL(url)
    } finally {
      setIsGenerating(false)
    }
  }

  return (
    <button onClick={handleDownload} disabled={isGenerating}>
      {isGenerating ? 'Generating...' : 'Download PDF'}
    </button>
  )
}
```

---

## 4. Preview PDF

```tsx
import { PDFViewer } from '@react-pdf/renderer'

function PDFPreview({ data }: { data: InvoiceData }) {
  return (
    <PDFViewer width="100%" height={600}>
      <InvoicePDF data={data} />
    </PDFViewer>
  )
}
```

---

## 5. Common Styling Patterns

### Tables
```tsx
const tableStyles = StyleSheet.create({
  table: {
    display: 'table',
    width: 'auto',
  },
  tableRow: {
    flexDirection: 'row',
    borderBottomWidth: 1,
    borderBottomColor: '#eee',
    padding: 8,
  },
  tableHeader: {
    backgroundColor: '#f5f5f5',
    fontWeight: 'bold',
  },
  colWide: { flex: 2 },
  colNormal: { flex: 1 },
  colNarrow: { flex: 0.5 },
})
```

### Headers & Footers
```tsx
<Page>
  {/* Fixed header */}
  <View fixed style={{ top: 0, left: 0, right: 0 }}>
    <Text>Company Name</Text>
  </View>

  {/* Content */}
  <View style={{ marginTop: 50 }}>
    {/* ... */}
  </View>

  {/* Fixed footer */}
  <Text
    fixed
    style={{ position: 'absolute', bottom: 20, left: 0, right: 0, textAlign: 'center' }}
    render={({ pageNumber, totalPages }) => `Page ${pageNumber} of ${totalPages}`}
  />
</Page>
```

### Images
```tsx
import { Image } from '@react-pdf/renderer'

<Image src="/logo.png" style={{ width: 100, height: 50 }} />
```

---

## 6. Tips

- Use `StyleSheet.create()` for performance
- Use `fixed` prop for headers/footers that repeat
- Use `break` prop on View to force page breaks
- Use `minPresenceAhead` to avoid orphaned content

---

## 7. Troubleshooting

### Fonts not working
Register custom fonts before use:
```tsx
import { Font } from '@react-pdf/renderer'

Font.register({
  family: 'Roboto',
  src: '/fonts/Roboto-Regular.ttf',
})
```

### PDF too slow
- Avoid complex calculations in render
- Use memo for static content
- Consider server-side generation for complex PDFs
