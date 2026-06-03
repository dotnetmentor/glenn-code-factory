# Map Integration (Mapbox) - Complete Reference

## Overview

Interactive maps using Mapbox GL JS with React via `react-map-gl`. Features:
- Vector tiles (fast, smooth)
- Custom markers & popups
- Geolocation
- Geocoding (address search)

---

## Quick Start: Secrets

Set a Mapbox public token in `packages/backoffice-web/.env.local` (gitignored):

```bash
VITE_MAPBOX_TOKEN=pk.your_public_token_here
```

Get a token at https://account.mapbox.com/access-tokens/ — use a **public** token scoped to your URLs.

**Usage in code:**
```tsx
const MAPBOX_TOKEN = import.meta.env.VITE_MAPBOX_TOKEN
```

---

## 1. Installation

Already installed in this project:
```bash
npm install mapbox-gl react-map-gl
```

---

## 2. Basic Map

```tsx
import Map from 'react-map-gl/mapbox'
import 'mapbox-gl/dist/mapbox-gl.css'

const MAPBOX_TOKEN = import.meta.env.VITE_MAPBOX_TOKEN

function BasicMap() {
  return (
    <Map
      mapboxAccessToken={MAPBOX_TOKEN}
      initialViewState={{
        longitude: -122.4,
        latitude: 37.8,
        zoom: 12,
      }}
      style={{ width: '100%', height: 400 }}
      mapStyle="mapbox://styles/mapbox/streets-v12"
    />
  )
}
```

---

## 3. Controlled Map with State

```tsx
import { useState, useCallback } from 'react'
import Map, { ViewState } from 'react-map-gl/mapbox'

function ControlledMap() {
  const [viewState, setViewState] = useState<ViewState>({
    longitude: -122.4,
    latitude: 37.8,
    zoom: 12,
  })

  return (
    <Map
      {...viewState}
      onMove={(evt) => setViewState(evt.viewState)}
      mapboxAccessToken={MAPBOX_TOKEN}
      style={{ width: '100%', height: 400 }}
      mapStyle="mapbox://styles/mapbox/streets-v12"
    />
  )
}
```

---

## 4. Markers

```tsx
import Map, { Marker } from 'react-map-gl/mapbox'
import { Pin } from './Pin' // Your custom pin component

interface Location {
  id: string
  name: string
  longitude: number
  latitude: number
}

function MapWithMarkers({ locations }: { locations: Location[] }) {
  return (
    <Map
      mapboxAccessToken={MAPBOX_TOKEN}
      initialViewState={{
        longitude: locations[0]?.longitude ?? 0,
        latitude: locations[0]?.latitude ?? 0,
        zoom: 10,
      }}
      style={{ width: '100%', height: 500 }}
      mapStyle="mapbox://styles/mapbox/light-v11"
    >
      {locations.map((loc) => (
        <Marker
          key={loc.id}
          longitude={loc.longitude}
          latitude={loc.latitude}
          anchor="bottom"
        >
          <Pin size={24} />
        </Marker>
      ))}
    </Map>
  )
}

// Simple Pin component
function Pin({ size = 20 }: { size?: number }) {
  return (
    <svg height={size} viewBox="0 0 24 24" fill="#d00">
      <path d="M12 0C7.58 0 4 3.58 4 8c0 5.25 8 13 8 13s8-7.75 8-13c0-4.42-3.58-8-8-8z" />
    </svg>
  )
}
```

---

## 5. Popups

```tsx
import Map, { Marker, Popup } from 'react-map-gl/mapbox'
import { useState } from 'react'

function MapWithPopups({ locations }) {
  const [selectedLocation, setSelectedLocation] = useState<Location | null>(null)

  return (
    <Map
      mapboxAccessToken={MAPBOX_TOKEN}
      initialViewState={{ longitude: -122.4, latitude: 37.8, zoom: 10 }}
      style={{ width: '100%', height: 500 }}
      mapStyle="mapbox://styles/mapbox/streets-v12"
    >
      {locations.map((loc) => (
        <Marker
          key={loc.id}
          longitude={loc.longitude}
          latitude={loc.latitude}
          onClick={(e) => {
            e.originalEvent.stopPropagation()
            setSelectedLocation(loc)
          }}
        >
          <Pin />
        </Marker>
      ))}

      {selectedLocation && (
        <Popup
          longitude={selectedLocation.longitude}
          latitude={selectedLocation.latitude}
          anchor="top"
          onClose={() => setSelectedLocation(null)}
          closeOnClick={false}
        >
          <div>
            <h3>{selectedLocation.name}</h3>
            <p>{selectedLocation.description}</p>
          </div>
        </Popup>
      )}
    </Map>
  )
}
```

---

## 6. User Location (Geolocate)

```tsx
import Map, { GeolocateControl, NavigationControl } from 'react-map-gl/mapbox'

function MapWithControls() {
  return (
    <Map
      mapboxAccessToken={MAPBOX_TOKEN}
      initialViewState={{ longitude: -122.4, latitude: 37.8, zoom: 10 }}
      style={{ width: '100%', height: 500 }}
      mapStyle="mapbox://styles/mapbox/streets-v12"
    >
      {/* Zoom in/out buttons */}
      <NavigationControl position="top-right" />

      {/* Locate user button */}
      <GeolocateControl
        position="top-right"
        trackUserLocation
        showUserHeading
      />
    </Map>
  )
}
```

---

## 7. Click to Add Marker

```tsx
import Map, { Marker, MapLayerMouseEvent } from 'react-map-gl/mapbox'
import { useState, useCallback } from 'react'

function ClickableMap() {
  const [markers, setMarkers] = useState<{ lng: number; lat: number }[]>([])

  const handleClick = useCallback((event: MapLayerMouseEvent) => {
    const { lng, lat } = event.lngLat
    setMarkers((prev) => [...prev, { lng, lat }])
  }, [])

  return (
    <Map
      mapboxAccessToken={MAPBOX_TOKEN}
      initialViewState={{ longitude: -122.4, latitude: 37.8, zoom: 10 }}
      style={{ width: '100%', height: 500 }}
      mapStyle="mapbox://styles/mapbox/streets-v12"
      onClick={handleClick}
    >
      {markers.map((marker, i) => (
        <Marker key={i} longitude={marker.lng} latitude={marker.lat}>
          <Pin />
        </Marker>
      ))}
    </Map>
  )
}
```

---

## 8. Fit Bounds to Markers

```tsx
import { useRef, useEffect } from 'react'
import Map, { MapRef } from 'react-map-gl/mapbox'
import mapboxgl from 'mapbox-gl'

function MapWithBounds({ locations }) {
  const mapRef = useRef<MapRef>(null)

  useEffect(() => {
    if (!mapRef.current || locations.length === 0) return

    const bounds = new mapboxgl.LngLatBounds()
    locations.forEach((loc) => {
      bounds.extend([loc.longitude, loc.latitude])
    })

    mapRef.current.fitBounds(bounds, { padding: 50, maxZoom: 15 })
  }, [locations])

  return (
    <Map
      ref={mapRef}
      mapboxAccessToken={MAPBOX_TOKEN}
      initialViewState={{ longitude: 0, latitude: 0, zoom: 1 }}
      style={{ width: '100%', height: 500 }}
      mapStyle="mapbox://styles/mapbox/streets-v12"
    >
      {/* markers... */}
    </Map>
  )
}
```

---

## 9. Map Styles

```tsx
// Light/Dark themes
mapStyle="mapbox://styles/mapbox/light-v11"
mapStyle="mapbox://styles/mapbox/dark-v11"

// Street maps
mapStyle="mapbox://styles/mapbox/streets-v12"
mapStyle="mapbox://styles/mapbox/outdoors-v12"

// Satellite
mapStyle="mapbox://styles/mapbox/satellite-v9"
mapStyle="mapbox://styles/mapbox/satellite-streets-v12"
```

---

## 10. Geocoding (Address Search)

Use Mapbox Geocoding API:

```tsx
async function searchAddress(query: string): Promise<Location[]> {
  const response = await fetch(
    `https://api.mapbox.com/geocoding/v5/mapbox.places/${encodeURIComponent(query)}.json?access_token=${MAPBOX_TOKEN}`
  )
  const data = await response.json()

  return data.features.map((feature: any) => ({
    id: feature.id,
    name: feature.place_name,
    longitude: feature.center[0],
    latitude: feature.center[1],
  }))
}
```

---

## 11. Tips

- Always import CSS: `import 'mapbox-gl/dist/mapbox-gl.css'`
- Use `react-map-gl/mapbox` (not just `react-map-gl`) for Mapbox v3
- Set explicit height on map container
- Use `useCallback` for event handlers to prevent re-renders
- Use `mapRef` for imperative actions (flyTo, fitBounds)

---

## 12. Troubleshooting

### Map not showing
- Check token is valid
- Ensure CSS is imported
- Verify container has explicit height

### Markers in wrong position
- Mapbox uses `[longitude, latitude]` order (not lat/lng)

### Performance issues
- Use `useMemo` for marker arrays
- Consider clustering for many markers (`supercluster`)
