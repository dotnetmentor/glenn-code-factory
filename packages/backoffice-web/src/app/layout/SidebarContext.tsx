import { createContext, useContext, useState, PropsWithChildren } from 'react'

interface SidebarContextType {
  isCollapsed: boolean
  toggleCollapse: () => void
}

const SidebarContext = createContext<SidebarContextType | undefined>(undefined)

export function SidebarProvider({ children }: PropsWithChildren) {
  const [isCollapsed, setIsCollapsed] = useState(false)

  const toggleCollapse = () => {
    setIsCollapsed(prev => !prev)
  }

  return (
    <SidebarContext.Provider value={{ isCollapsed, toggleCollapse }}>
      {children}
    </SidebarContext.Provider>
  )
}

export function useSidebar() {
  const context = useContext(SidebarContext)
  if (context === undefined) {
    throw new Error('useSidebar must be used within SidebarProvider')
  }
  return context
}
