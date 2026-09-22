export type Id = string;
export interface CameraStatus {
  id: Id;
  name: string;
  running: boolean;
  fps: number;
  inferenceMs: number;
  droppedFrames: number;
  width: number;
  height: number;
  sourceState: string;
  configuredRoiCount: number;
  configuredTaskCount: number;
  activePipelineCount: number;
  processingState: string;
  captureBackend?: string;
}
export interface RoiPoint {
  x: number;
  y: number;
}
export interface ProcessingTask {
  id: Id;
  type: string;
  name: string;
  enabled: boolean;
  maxFps: number;
  threads: number;
  options: Record<string, unknown>;
}
export interface NamedRoi {
  id: Id;
  name: string;
  enabled: boolean;
  points: RoiPoint[];
  processing: ProcessingTask[];
}
export interface CameraSettings {
  id: Id;
  name: string;
  sourceUrl: string;
  modelFile: string;
  plateEnabled: boolean;
  faceEnabled: boolean;
  faceRecognitionEnabled: boolean;
  captureBackend: string;
  transport: string;
  bufferCount: number;
  reconnectDelaySec: number;
  drawBoxes: boolean;
  motionGateEnabled: boolean;
  motionFps: number;
  motionThreshold: number;
  motionChangedPercent: number;
  motionRoiScalePercent: number;
  motionHoldMs: number;
  activeDetectionFps: number;
  idleDetectionFps: number;
  maxFps: number;
  threads: number;
  inputSize: number;
  confidence: number;
  nmsIoU: number;
  platePreprocessing: string;
  faceModelFile: string;
  faceInputSize: number;
  faceConfidence: number;
  faceRecordConfidence: number;
  faceRecognitionModelFile: string;
  faceRecognitionThreshold: number;
  faceMatchIou: number;
  faceTrackMaxMisses: number;
  facePreprocessing: string;
  faceMaxFps: number;
  faceNmsThreshold: number;
  faceTopK: number;
  faceUnknownMatchThreshold: number;
  faceEventCooldownSeconds: number;
  trackMaxMisses: number;
  processingSchemaVersion: number;
  rois: NamedRoi[];
  [key: string]: unknown;
}
export interface ServiceSettings {
  schemaVersion: number;
  revision: number;
  serviceNodeId: string;
  http: { listenUrls: string[]; serveUi: boolean; corsOrigins: string[] };
  security: { apiKey: string; allowLoopbackWithoutApiKey: boolean };
  runtime: {
    autoStartCameras: boolean;
    previewFps: number;
    maxEventQueueLength: number;
  };
  association: {
    maxWindowMs: number;
    requireSameRoi: boolean;
  };
  retention: {
    eventDays: number;
    artifactDays: number;
    webhookRetryDays: number;
  };
  triggers: TriggerDefinition[];
}
export interface SettingsResponse {
  revision: number;
  detection: {
    cameras: CameraSettings[];
    selectedCameraId?: string;
    [key: string]: unknown;
  };
  service: ServiceSettings;
}
export interface ModelInfo {
  name: string;
  relativePath: string;
  module: string;
  capability?: string;
  inputSizes?: number[];
  packaged: boolean;
}
export interface TriggerAction {
  type: string;
  target?: string;
  enabled: boolean;
}
export interface TriggerDefinition {
  id: Id;
  name: string;
  enabled: boolean;
  cameraIds: string[];
  taskIds: string[];
  kinds: string[];
  labelEquals?: string;
  identityId?: string;
  plateTextEquals?: string;
  minimumConfidence?: number;
  cooldownSeconds: number;
  actions: TriggerAction[];
}
export interface ClientSubscription {
  mode: "All" | "Plate" | "KnownFace";
  cameraIds: string[];
  roiIds: string[];
  faceRequired: boolean;
  plateRequired: boolean;
  includeFace: boolean;
  includePlate: boolean;
  includeUnknownFace: boolean;
  includeArtifacts: boolean;
  windowMs: number;
  cooldownSeconds: number;
}
export interface Artifact {
  artifactId: Id;
  type: string;
  contentType: string;
  width: number;
  height: number;
  sourceFrameSequence: number;
  sha256: string;
  sizeBytes: number;
  retentionUntilUtc: string;
  relativePath: string;
  downloadUrl: string;
}
export interface DetectionEvent {
  eventId: Id;
  sequence: number;
  payloadVersion: number;
  eventType: string;
  scenario: string;
  occurredAtUtc: string;
  receivedAtUtc: string;
  source: Record<string, unknown>;
  trigger: Record<string, unknown>;
  components: Record<string, Record<string, unknown>>;
  artifacts: Artifact[];
}
export interface FaceIdentity {
  id: Id;
  name: string;
  isUnknown: boolean;
  personNumber: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  samples: FaceSample[];
}
export interface FaceSample {
  id: Id;
  personId: Id;
  personNumber: number;
  personName: string;
  sampleNumber: number;
  originalFileName: string;
  fileExtension: string;
  createdAtUtc: string;
  detectionConfidence: number;
  faceImage?: number[];
  embedding?: number[];
}
export interface FaceSimilarityPair {
  left: FaceSample;
  right: FaceSample;
  similarity: number;
}
export interface FaceDatabaseHealth {
  databasePath: string;
  people: number;
  samples: number;
  sizeBytes: number;
}
export interface Capability {
  type: string;
  displayName: string;
  kind: string;
  optionsType?: string;
  editorKey: string;
  available?: boolean;
  availabilityMessage?: string;
}
export interface ServiceStatus {
  ready: boolean;
  revision: number;
  serviceNodeId: string;
  license: { isValid: boolean; message: string; features?: string };
  cameras: CameraStatus[];
  eventSequence: number;
}
export interface WebRtcAnswer {
  sessionId: string;
  type: string;
  sdp: string;
}
export interface LiveOverlayPoint { x: number; y: number; }
export interface LiveOverlayRect { x: number; y: number; width: number; height: number; }
export interface LiveOverlayRoi { id: string; name: string; enabled: boolean; points: LiveOverlayPoint[]; }
export interface LiveOverlayDetection {
  kind: string;
  label: string;
  text?: string | null;
  confidence: number;
  bounds: LiveOverlayRect;
  trackId?: number | null;
  accepted: boolean;
}
export interface LiveOverlayPrimitive {
  kind: string;
  points: LiveOverlayPoint[];
  bounds: LiveOverlayRect;
  radius: number;
  red: number;
  green: number;
  blue: number;
  thickness: number;
  filled: boolean;
}
export interface LiveOverlaySnapshot {
  width: number;
  height: number;
  rois: LiveOverlayRoi[];
  motionRois: LiveOverlayRoi[];
  detections: LiveOverlayDetection[];
  processingOverlays: LiveOverlayPrimitive[];
  updatedUtc: string;
}
