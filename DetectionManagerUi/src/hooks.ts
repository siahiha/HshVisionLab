import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useEffect } from 'react'
import { api } from './api'
import type { CameraSettings, DetectionEvent, TriggerDefinition } from './types'
export const keys = { status: ['status'], cameras: ['cameras'], settings: ['settings'], capabilities: ['capabilities'], models: ['models'], people: ['people'], triggers: ['triggers'], events: ['events'] }
export function useServiceStatus() { return useQuery({ queryKey: keys.status, queryFn: api.status, refetchInterval: 5000 }) }
export function useCameras() { return useQuery({ queryKey: keys.cameras, queryFn: api.cameras, refetchInterval: 5000 }) }
export function useCamera(id?: string) { return useQuery({ queryKey: ['camera', id], queryFn: () => api.camera(id!), enabled: Boolean(id) }) }
export function useSettings() { return useQuery({ queryKey: keys.settings, queryFn: api.settings }) }
export function useCapabilities() { return useQuery({ queryKey: keys.capabilities, queryFn: api.capabilities, staleTime: 60_000 }) }
export function useModels() { return useQuery({ queryKey: keys.models, queryFn: api.models, staleTime: 60_000 }) }
export function usePeople() { return useQuery({ queryKey: keys.people, queryFn: api.people }) }
export function usePersonSamples(id?: string) { return useQuery({ queryKey: ['samples', id], queryFn: () => api.samples(id!), enabled: Boolean(id) }) }
export function useEvents(query = '') { return useQuery({ queryKey: [...keys.events, query], queryFn: () => api.events(query), refetchInterval: 5000 }) }
export function useEvent(id?: string) { return useQuery({ queryKey: ['event', id], queryFn: () => api.event(id!), enabled: Boolean(id) }) }
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
    const connection = new HubConnectionBuilder().withUrl('/hubs/detections').withAutomaticReconnect([0, 2000, 5000, 15000]).configureLogging(LogLevel.Warning).build()
    const apply = (item: DetectionEvent) => { last = Math.max(last, item.sequence); sessionStorage.setItem(key, String(last)); client.setQueryData<DetectionEvent[]>([...keys.events, ''], old => [item, ...(old ?? []).filter(existing => existing.eventId !== item.eventId)].slice(0, 2000)) }
    connection.on('detection', apply)
    connection.on('cursorExpired', () => { last = 0; sessionStorage.setItem(key, '0'); void client.invalidateQueries({ queryKey: keys.events }) })
    connection.on('replayStarted', () => undefined)
    connection.on('replayCompleted', (message: { lastSequence?: number }) => { if (message?.lastSequence) { last = Math.max(last, message.lastSequence); sessionStorage.setItem(key, String(last)) } })
    const connect = async () => { try { await connection.start(); if (!stopped) await connection.invoke('Subscribe', last) } catch { /* polling remains the safe fallback */ } }
    connection.onreconnected(() => { void connection.invoke('Subscribe', last).catch(() => undefined) })
    void connect()
    return () => { stopped = true; connection.off('detection', apply); void connection.stop() }
  })
}
function useEffectOnce(effect: () => void | (() => void)) { useEffect(effect, []) }
