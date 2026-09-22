/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_HSH_API_KEY?: string
  readonly VITE_HSH_API_BASE_URL?: string
  readonly VITE_HSH_DEV_API_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
