import { createContext, useContext, useState, ReactNode } from 'react';
import { Alert, Snackbar } from '@mui/material';

interface NotificationState {
  open: boolean;
  message: string;
  severity: 'success' | 'error' | 'warning' | 'info';
}

interface NotificationContextType {
  showSuccess: (message: string) => void;
  showError: (message: string) => void;
  showWarning: (message: string) => void;
  showInfo: (message: string) => void;
  close: () => void;
}

const NotificationContext = createContext<NotificationContextType | undefined>(undefined);

export function NotificationProvider({ children }: { children: ReactNode }) {
  const [notification, setNotification] = useState<NotificationState>({
    open: false,
    message: '',
    severity: 'info',
  });

  const showNotification = (message: string, severity: NotificationState['severity']) => {
    setNotification({
      open: true,
      message,
      severity,
    });
  };

  const showSuccess = (message: string) => showNotification(message, 'success');
  const showError = (message: string) => showNotification(message, 'error');
  const showWarning = (message: string) => showNotification(message, 'warning');
  const showInfo = (message: string) => showNotification(message, 'info');

  const close = () => {
    setNotification(prev => ({ ...prev, open: false }));
  };

  return (
    <NotificationContext.Provider value={{
      showSuccess,
      showError,
      showWarning,
      showInfo,
      close,
    }}>
      {children}
      
      {/* Global notification component */}
      <Snackbar
        open={notification.open}
        autoHideDuration={6000}
        onClose={close}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
      >
        <Alert
          onClose={close}
          severity={notification.severity}
          variant="filled"
          sx={{
            width: '100%',
            color: '#fff',
            // MUI's default close icon on a filled Alert is translucent white
            // (~0.5 opacity) which gets washed out on success / warning / info
            // backgrounds. Force opaque white with a subtle hover lift so the
            // dismiss affordance is legible at-a-glance.
            '& .MuiAlert-action': {
              color: '#fff',
              pt: 0,
            },
            '& .MuiAlert-action .MuiIconButton-root': {
              color: '#fff',
              opacity: 0.92,
              '&:hover': {
                opacity: 1,
                backgroundColor: 'rgba(255, 255, 255, 0.18)',
              },
              '&:focus-visible': {
                outline: '2px solid rgba(255, 255, 255, 0.85)',
                outlineOffset: '2px',
              },
            },
            '& .MuiAlert-action .MuiSvgIcon-root': {
              color: '#fff',
              fontSize: '1.125rem',
            },
            '& .MuiAlert-icon': {
              color: '#fff',
              opacity: 0.95,
            },
          }}
        >
          {notification.message}
        </Alert>
      </Snackbar>
    </NotificationContext.Provider>
  );
}

export function useNotification() {
  const context = useContext(NotificationContext);
  if (!context) {
    throw new Error('useNotification must be used within a NotificationProvider');
  }
  return context;
}
