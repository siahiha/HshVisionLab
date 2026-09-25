import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useEffect, useState } from 'react'
import { api, serviceUrl } from './api'
import type { EventDeletionRange } from './api'
import type { CameraSettings, ClientSubscription, DetectionEvent, InvocationDefinition, TriggerDefinition } from './types'
export const keys = { status: ['status'], cameras: ['cameras'], settings: ['settings'], capabilities: ['capabilities'], models: ['models'], people: ['people'], triggers: ['triggers'], invocations: ['invocations'], invocationLogs: ['invocation-logs'], events: ['events'] }
export const clientSubscriptionKey = 'hsh-client-subscription'
export const defaultClientSubscription = (): ClientSubscription => ({ mode: 'All', cameraIds: [], roiIds: [], faceRequired: false, plateRequired: false, includeFace: true, includePlate: true, includeUnknownFace: true, includeArtifacts: true, windowMs: 1500, cooldownSeconds: 0 })
export function readClientSubscription(): ClientSubscription {
  try {
    const value = JSON.parse(sessionStorage.getItem(clientSubscriptionKey) ?? 'null') as Partial<ClientSubscription> | null
    return { ...defaultClientSubscription(), ...(value ?? {}) }
  } catch { return defaultClientSubscription() }
}
export function saveClientSubscription(value: ClientSubscription) {
  sessionStorage.setItem(clientSubscriptionKey, JSON.stringify(value))
  window.dispatchEvent(new Event('hsh-client-subscription-changed'))
}
export function useClientSubscription() {
  const [value, setValue] = useState<ClientSubscription>(() => readClientSubscription())
  useEffect(() => {
    const update = () => setValue(readClientSubscription())
    window.addEventListener('hsh-client-subscription-changed', update)
    return () => window.removeEventListener('hsh-client-subscription-changed', update)
  }, [])
  return value
}
export function useServiceStatus() { return useQuery({ queryKey: keys.status, queryFn: api.status, refetchInterval: 5000 }) }
export function useCameras() { return useQuery({ queryKey: keys.cameras, queryFn: api.cameras, refetchInterval: 5000 }) }
export function useCamera(id?: string) { return useQuery({ queryKey: ['camera', id], queryFn: () => api.camera(id!), enabled: Boolean(id) }) }
export function useSettings() { return useQuery({ queryKey: keys.settings, queryFn: api.settings }) }
export function useCapabilities() { return useQuery({ queryKey: keys.capabilities, queryFn: api.capabilities, staleTime: 60_000 }) }
export function useModels() { return useQuery({ queryKey: keys.models, queryFn: api.models, staleTime: 60_000 }) }
export function useInvocations() { return useQuery({ queryKey: keys.invocations, queryFn: api.invocations }) }
export function useInvocationLogs(invocationId?: string, limit = 200) { return useQuery({ queryKey: [...keys.invocationLogs, invocationId, limit], queryFn: () => api.invocationLogs(invocationId, limit), refetchInterval: 5000 }) }
export function useInvocationMutation() { const client = useQueryClient(); return useMutation({ mutationFn: (input: { value: InvocationDefinition; create: boolean }) => input.create ? api.createInvocation(input.value) : api.updateInvocation(input.value), onSuccess: () => { void client.invalidateQueries({ queryKey: keys.invocations }); void client.invalidateQueries({ queryKey: keys.invocationLogs }) } }) }
export function usePeople() { return useQuery({ queryKey: keys.people, queryFn: api.people }) }
export function usePersonSamples(id?: string) { return useQuery({ queryKey: ['samples', id], queryFn: () => api.samples(id!), enabled: Boolean(id) }) }
export function useEvents(query = '', limit = 200) {
  const subscription = useClientSubscription()
  const subscriptionKey = JSON.stringify(subscription)
  const client = useQueryClient()
  const queryKey = [...keys.events, query, limit, subscriptionKey]
  return useQuery({ queryKey, queryFn: async () => {
    const batch = await api.events(query, subscription, limit)
    // REST polling may return an older page while SignalR has already added
    // a newer event to the same cache. Merge both sets so a live card is not
    // removed by the next five-second polling refresh.
    const merged = new Map<string, DetectionEvent>()
    for (const item of client.getQueryData<DetectionEvent[]>(queryKey) ?? []) merged.set(item.eventId, item)
    for (const item of batch) merged.set(item.eventId, item)
    return [...merged.values()].sort((a, b) => b.sequence - a.sequence).slice(0, limit)
  }, refetchInterval: 5000 })
}
export function useEvent(id?: string) { return useQuery({ queryKey: ['event', id], queryFn: () => api.event(id!), enabled: Boolean(id) }) }
export function useDeleteEvents() { const client = useQueryClient(); return useMutation({ mutationFn: (range: EventDeletionRange) => api.deleteEvents(range), onSuccess: () => { void client.invalidateQueries({ queryKey: keys.events }); void client.removeQueries({ queryKey: ['event'] }) } }) }
export function useTriggers() { return useQuery({ queryKey: keys.triggers, queryFn: api.triggers }) }
export function useCameraMutation() { const client = useQueryClient(); return useMutation({ mutationFn: (camera: CameraSettings) => api.saveCamera(camera), onSuccess: (_, camera) => { void client.invalidateQueries({ queryKey: keys.cameras }); void client.invalidateQueries({ queryKey: ['camera', camera.id] }); void client.invalidateQueries({ queryKey: keys.settings }) } }) }
export function useCameraAction() { const client = useQueryClient(); return useMutation({ mutationFn: ({ id, action }: { id: string; action: 'start' | 'stop' | 'restart' }) => api.cameraAction(id, action), onSuccess: () => { void client.invalidateQueries({ queryKey: keys.cameras }); void client.invalidateQueries({ queryKey: keys.status }) } }) }
export function useTriggerMutation() { const client = useQueryClient(); return useMutation({ mutationFn: ({ mode, trigger }: { mode: 'create' | 'update'; trigger: TriggerDefinition }) => mode === 'create' ? api.createTrigger(trigger) : api.updateTrigger(trigger), onSuccess: () => { void client.invalidateQueries({ queryKey: keys.triggers }); void client.invalidateQueries({ queryKey: keys.settings }) } }) }
export function useDetectionStream() {
  const client = useQueryClient()
  useEffectOnce(() => {
    let stopped = false
    const key = 'hsh-detection-last-sequence'
    let last = Number(sessionStorage.getItem(key) ?? '0') || 0
    let pending: DetectionEvent[] = []
    let flushTimer: number | undefined
    const connection = new HubConnectionBuilder().withUrl(serviceUrl('/hubs/detections')).withAutomaticReconnect([0, 2000, 5000, 15000]).configureLogging(LogLevel.Warning).build()
    const flush = () => {
      flushTimer = undefined
      if (stopped || pending.length === 0) return
      const batch = pending
      pending = []
      const subscriptionKey = JSON.stringify(readClientSubscription())
      client.setQueryData<DetectionEvent[]>([...keys.events, '', 200, subscriptionKey], old => {
        const merged = new Map<string, DetectionEvent>()
        for (const item of old ?? []) merged.set(item.eventId, item)
        for (const item of batch) merged.set(item.eventId, item)
        return [...merged.values()].sort((a, b) => b.sequence - a.sequence).slice(0, 200)
      })
    }
    const apply = (item: DetectionEvent) => {
      last = Math.max(last, item.sequence)
      sessionStorage.setItem(key, String(last))
      pending.push(item)
      if (flushTimer === undefined) flushTimer = window.setTimeout(flush, 50)
    }
    connection.on('detection', apply)
    connection.on('cursorExpired', () => { last = 0; sessionStorage.setItem(key, '0'); void client.invalidateQueries({ queryKey: keys.events }) })
    connection.on('replayStarted', () => undefined)
    connection.on('replayCompleted', (message: { lastSequence?: number }) => { if (message?.lastSequence) { last = Math.max(last, message.lastSequence); sessionStorage.setItem(key, String(last)) } })
    const subscribe = () => connection.invoke('Subscribe', last, readClientSubscription()).catch(() => undefined)
    const onSubscriptionChanged = () => { void subscribe(); void client.invalidateQueries({ queryKey: keys.events }) }
    window.addEventListener('hsh-client-subscription-changed', onSubscriptionChanged)
    const connect = async () => {
      try {
        await connection.start()
        if (stopped) return
        // The dashboard already loads a bounded history through REST. On the
        // first connection do not replay the entire event store through
        // SignalR; start the live cursor at the current watermark instead.
        if (last === 0) {
          try {
            const status = await api.status()
            last = status.eventSequence
            sessionStorage.setItem(key, String(last))
          } catch { /* subscribe from zero is the safe fallback */ }
        }
        await subscribe()
      } catch { /* polling remains the safe fallback */ }
    }
    connection.onreconnected(() => { void subscribe() })
    void connect()
    return () => { stopped = true; if (flushTimer !== undefined) window.clearTimeout(flushTimer); pending = []; window.removeEventListener('hsh-client-subscription-changed', onSubscriptionChanged); connection.off('detection', apply); void connection.stop() }
  })
}
function useEffectOnce(effect: () => void | (() => void)) { useEffect(effect, []) }
