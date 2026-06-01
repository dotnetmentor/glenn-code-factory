import axios, { AxiosError, AxiosRequestConfig } from 'axios'

export const apiClient = axios.create({
  baseURL: '',
  headers: {
    'Content-Type': 'application/json',
  },
  withCredentials: true,
})

// Add X-Tenant-Id header for tenant-scoped requests (not for super-admin)
apiClient.interceptors.request.use((config) => {
  const isSuperAdmin = window.location.pathname.startsWith('/super-admin')
  if (isSuperAdmin) {
    return config
  }
  const tenantId = localStorage.getItem('tenant-admin-selected-tenant-id')
  if (tenantId) {
    config.headers['X-Tenant-Id'] = tenantId
  }
  return config
})

const isInIframe = window.parent !== window

apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (
      isInIframe &&
      error.response &&
      error.response.status !== 401
    ) {
      window.parent.postMessage(
        {
          type: 'preview:networkError',
          payload: {
            url: error.config?.url || error.response.config?.url,
            method: error.config?.method?.toUpperCase() || 'UNKNOWN',
            status: error.response.status,
            responseBody: error.response.data,
          },
        },
        '*'
      )
    }
    return Promise.reject(error)
  }
)

export async function customClient<T>(config: AxiosRequestConfig): Promise<T> {
  const { data } = await apiClient(config)
  return data
}

export type ErrorType<E> = AxiosError<E>
