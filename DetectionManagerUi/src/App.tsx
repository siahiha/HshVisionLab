import React, { useEffect, useMemo, useRef, useState } from "react";
import {
  BrowserRouter,
  Link,
  NavLink,
  Route,
  Routes,
  useSearchParams,
  useLocation,
  useNavigate,
  useParams,
} from "react-router-dom";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  Activity,
  AlertTriangle,
  Archive,
  BellRing,
  Camera,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  CircleGauge,
  Database,
  Download,
  Eye,
  FileJson,
  Gauge,
  LayoutDashboard,
  Menu,
  Maximize2,
  Minimize2,
  Move,
  Pause,
  Pencil,
  Play,
  Plus,
  Radio,
  RefreshCw,
  Save,
  Search,
  Server,
  Settings,
  ShieldCheck,
  SlidersHorizontal,
  Trash2,
  UserRound,
  UsersRound,
  Video,
  Wifi,
  X,
  Zap,
} from "lucide-react";
import { api, serviceUrl } from "./api";
import {
  keys,
  useCamera,
  useCameraAction,
  useCameraMutation,
  useCameras,
  useCapabilities,
  useDeleteEvents,
  useDetectionStream,
  useEvent,
  useEvents,
  useModels,
  usePeople,
  usePersonSamples,
  useServiceStatus,
  useSettings,
  useTriggerMutation,
  useTriggers,
  readClientSubscription,
  saveClientSubscription,
} from "./hooks";
import type {
  CameraSettings,
  CameraStatus,
  ClientSubscription,
  DetectionEvent,
  FaceIdentity,
  FaceSample,
  FaceSimilarityPair,
  LiveOverlayDetection,
  LiveOverlaySnapshot,
  ModelInfo,
  NamedRoi,
  ProcessingTask,
  RoiPoint,
  ServiceSettings,
  SettingsResponse,
  TriggerAction,
  TriggerDefinition,
} from "./types";

const navItems = [
  { to: "/", label: "نمای کلی", icon: LayoutDashboard, end: true },
  { to: "/cameras", label: "مدیریت دوربین‌ها", icon: Camera },
  { to: "/faces", label: "پایگاه چهره", icon: UsersRound },
  { to: "/events", label: "تاریخچه تشخیص", icon: Archive },
  { to: "/triggers", label: "تریگرها و کلاینت‌ها", icon: BellRing },
  { to: "/settings", label: "تنظیمات سرویس", icon: Settings },
];
const n = (v: unknown, fallback = 0) =>
  typeof v === "number" && Number.isFinite(v) ? v : fallback;
const s = (v: unknown, fallback = "") => (typeof v === "string" ? v : fallback);
const newId = () =>
  typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
    ? crypto.randomUUID().replaceAll("-", "")
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}${Math.random().toString(36).slice(2)}`;
const clone = <T,>(value: T): T => structuredClone(value);
const option = (task: ProcessingTask, key: string, fallback: unknown) =>
  task.options?.[key] ?? fallback;
const setOption = (
  task: ProcessingTask,
  key: string,
  value: unknown,
): ProcessingTask => ({ ...task, options: { ...task.options, [key]: value } });
const processingType = (value: unknown) =>
  s(value).toLocaleLowerCase() === "face"
    ? "Face"
    : s(value).toLocaleLowerCase() === "plate"
      ? "Plate"
      : s(value);
function normalizeCameraSettings(value: CameraSettings): CameraSettings {
  const raw = value as CameraSettings & { rois?: unknown[] };
  const rois = Array.isArray(raw.rois) ? raw.rois : [];
  return {
    ...raw,
    processingSchemaVersion: 3,
    rois: rois.map((item, index) => {
      const roi = item as Partial<NamedRoi>;
      const points = Array.isArray(roi.points) ? roi.points : [];
      const tasks = Array.isArray(roi.processing) ? roi.processing : [];
      return {
        id: s(roi.id).trim() || newId(),
        name: s(roi.name).trim() || `ROI ${index + 1}`,
        enabled: roi.enabled !== false,
        points: points
          .filter(
            (point) =>
              point &&
              typeof (point as RoiPoint).x === "number" &&
              typeof (point as RoiPoint).y === "number",
          )
          .map((point) => ({
            x: Math.max(0, Math.min(1, (point as RoiPoint).x)),
            y: Math.max(0, Math.min(1, (point as RoiPoint).y)),
          })),
        processing: tasks.map((taskValue) => {
          const task = taskValue as Partial<ProcessingTask>;
          return {
            id: s(task.id).trim() || newId(),
            type: processingType(task.type),
            name:
              s(task.name).trim() || `${processingType(task.type)} detection`,
            enabled: task.enabled !== false,
            maxFps: n(task.maxFps, 8),
            threads: n(task.threads, 1),
            options:
              task.options && typeof task.options === "object"
                ? (task.options as Record<string, unknown>)
                : {},
          };
        }),
      };
    }),
  };
}
function prepareCameraForSave(value: CameraSettings): CameraSettings {
  const camera = normalizeCameraSettings(clone(value));
  camera.name = camera.name.trim();
  camera.sourceUrl = camera.sourceUrl.trim();
  camera.rois = camera.rois.map((roi) => ({
    ...roi,
    name: roi.name.trim(),
    processing: roi.processing.map((task) => ({
      ...task,
      type: processingType(task.type),
      name: task.name.trim(),
    })),
  }));
  const payload = camera as CameraSettings & { processing?: unknown };
  delete payload.processing;
  return camera;
}
function validateCameraDraft(camera: CameraSettings): string[] {
  const errors: string[] = [];
  if (!camera.name.trim()) errors.push("نام دوربین الزامی است.");
  if (!camera.sourceUrl.trim()) errors.push("Source URL دوربین الزامی است.");
  if (!camera.rois.length) errors.push("حداقل یک ROI تعریف کنید.");
  const names = new Set<string>();
  let enabledTasks = 0;
  for (const roi of camera.rois) {
    const roiName = roi.name.trim();
    if (!roiName) errors.push("نام ROI نمی‌تواند خالی باشد.");
    const nameKey = roiName.toLocaleLowerCase();
    if (names.has(nameKey)) errors.push(`نام ROI «${roiName}» تکراری است.`);
    names.add(nameKey);
    if (roi.enabled && roi.points.length < 3)
      errors.push(`ROI «${roiName || "بدون نام"}» حداقل به سه نقطه نیاز دارد.`);
    for (const task of roi.processing) {
      const type = processingType(task.type);
      if (type !== "Plate" && type !== "Face")
        errors.push(
          `نوع task «${task.type}» در ROI «${roiName}» پشتیبانی نمی‌شود.`,
        );
      if (task.enabled) enabledTasks++;
    }
  }
  if (camera.rois.some((roi) => roi.enabled) && enabledTasks === 0)
    errors.push("برای ROI فعال حداقل یک task فعال کنید.");
  return [...new Set(errors)];
}
function fmtDate(value?: string) {
  if (!value) return "—";
  return new Intl.DateTimeFormat("fa-IR", {
    dateStyle: "short",
    timeStyle: "short",
  }).format(new Date(value));
}
function fmt(v: unknown, digits = 1) {
  return typeof v === "number" ? v.toFixed(digits) : "—";
}
function eventTitle(event: DetectionEvent) {
  return s(
    event.components.face?.label,
    s(event.components.plate?.plateText, event.eventType),
  );
}
function eventConfidence(event: DetectionEvent) {
  const component = Object.values(event.components).find(
    (value) => typeof value.confidence === "number",
  );
  const confidence = component?.confidence;
  if (typeof confidence !== "number" || !Number.isFinite(confidence)) return "—";
  return `${Math.round(confidence <= 1 ? confidence * 100 : confidence)}%`;
}

function App() {
  const [mobile, setMobile] = useState(false);
  const status = useServiceStatus();
  return (
    <BrowserRouter>
      <div className="app-shell">
        <div className="app-body">
        <aside className={`sidebar ${mobile ? "open" : ""}`}>
          <div className="brand">
            <div className="brand-mark">
              <Zap size={20} />
            </div>
            <div>
              <b>HSH VISION</b>
              <span>DETECTION MANAGER</span>
            </div>
            <button
              className="icon-button mobile-close"
              onClick={() => setMobile(false)}
            >
              <X size={18} />
            </button>
          </div>
          <div className="workspace-label">محیط مدیریت سرویس</div>
          <nav>
            {navItems.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.end}
                onClick={() => setMobile(false)}
                className={({ isActive }) =>
                  `nav-item ${isActive ? "active" : ""}`
                }
              >
                <item.icon size={17} />
                <span>{item.label}</span>
                {item.to === "/" && status.data?.ready && (
                  <i className="nav-pulse" />
                )}
              </NavLink>
            ))}
          </nav>
          <div className="sidebar-footer">
            <div className="connection-line">
              <i
                className={`status-dot ${status.data?.ready ? "online" : "warning"}`}
              />
              <span>
                {status.data?.ready
                  ? "سرویس آماده و متصل"
                  : "سرویس نیازمند بررسی"}
              </span>
            </div>
            <small>HshDetectionService · .NET 8</small>
          </div>
        </aside>
        {mobile && (
          <div className="sidebar-scrim" onClick={() => setMobile(false)} />
        )}
        <main className="main-area">
          <header className="topbar">
            <div className="breadcrumb">
              <button
                className="icon-button menu-button"
                onClick={() => setMobile(true)}
              >
                <Menu size={19} />
              </button>
              <Server size={16} />
              <span>HSH Vision /</span>
              <PageTitle />
            </div>
            <div className="topbar-actions">
              <span className="service-chip">
                <i
                  className={`status-dot ${status.data?.ready ? "online" : "warning"}`}
                />
                {status.data?.serviceNodeId
                  ? `Node ${status.data.serviceNodeId.slice(0, 8)}`
                  : "در حال اتصال"}
              </span>
              <div className="avatar">H</div>
            </div>
          </header>
          <div className="page-content">
            <Routes>
              <Route path="/" element={<Dashboard />} />
              <Route path="/cameras" element={<Cameras />} />
              <Route path="/faces" element={<Faces />} />
              <Route path="/events" element={<Events />} />
              <Route path="/events/:eventId" element={<EventDetail />} />
              <Route path="/triggers" element={<Triggers />} />
              <Route path="/settings" element={<SettingsPage />} />
            </Routes>
          </div>
        </main>
        </div>
      </div>
    </BrowserRouter>
  );
}
function PageTitle() {
  const location = useLocation();
  useDetectionStream();
  return (
    <b>
      {navItems.find(
        (item) =>
          item.to === location.pathname ||
          (item.to !== "/" && location.pathname.startsWith(item.to)),
      )?.label ?? "جزئیات"}
    </b>
  );
}
function PageHead({
  title,
  description,
  action,
  className,
}: {
  title: string;
  description?: string;
  action?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={`page-head ${className ?? ""}`}>
      <div>
        <h1>{title}</h1>
        {description && <p>{description}</p>}
      </div>
      {action}
    </div>
  );
}
function Button({
  children,
  variant = "primary",
  icon: Icon,
  ...props
}: React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "soft" | "danger" | "ghost";
  icon?: typeof Save;
}) {
  return (
    <button className={`button ${variant}`} {...props}>
      {Icon && <Icon size={16} />}
      {children}
    </button>
  );
}
function Badge({
  children,
  tone = "neutral",
}: {
  children: React.ReactNode;
  tone?: "green" | "amber" | "red" | "blue" | "neutral";
}) {
  return (
    <span className={`badge ${tone}`}>
      <i />
      {children}
    </span>
  );
}
function Loading({ label = "در حال دریافت اطلاعات..." }: { label?: string }) {
  return (
    <div className="loading">
      <RefreshCw size={18} className="spin" />
      {label}
    </div>
  );
}
function ErrorBox({
  message = "ارتباط با سرویس برقرار نشد.",
}: {
  message?: string;
}) {
  return (
    <div className="error-box">
      <AlertTriangle size={19} />
      <div>
        <b>خطا در دریافت اطلاعات</b>
        <span>{message}</span>
      </div>
    </div>
  );
}
function Empty({
  icon: Icon = Database,
  title,
  text,
}: {
  icon?: typeof Database;
  title: string;
  text: string;
}) {
  return (
    <div className="empty">
      <Icon size={30} />
      <b>{title}</b>
      <span>{text}</span>
    </div>
  );
}
function Field({
  label,
  children,
  wide = false,
  hint,
}: {
  label: string;
  children: React.ReactNode;
  wide?: boolean;
  hint?: string;
}) {
  return (
    <label className={`field ${wide ? "wide" : ""}`}>
      <span>
        {label}
        {hint && <small> · {hint}</small>}
      </span>
      {children}
    </label>
  );
}
function Toggle({
  checked,
  onChange,
}: {
  checked: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <label className="switch">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      <i />
    </label>
  );
}
function Stat({
  icon: Icon,
  label,
  value,
  detail,
  tone = "blue",
}: {
  icon: typeof Camera;
  label: string;
  value: string | number;
  detail?: string;
  tone?: string;
}) {
  return (
    <div className="stat-card">
      <div className={`stat-icon ${tone}`}>
        <Icon size={20} />
      </div>
      <div>
        <span>{label}</span>
        <strong>{value}</strong>
        {detail && <small>{detail}</small>}
      </div>
    </div>
  );
}

function Dashboard() {
  const status = useServiceStatus();
  const cameras = useCameras();
  const events = useEvents();
  const people = usePeople();
  const navigate = useNavigate();
  const [focusedCameraId, setFocusedCameraId] = useState<string>();
  const [cameraPage, setCameraPage] = useState(0);
  const [commandBusy, setCommandBusy] = useState(false);
  const list = cameras.data ?? [];
  const cameraPageSize = 6;
  const cameraPageCount = Math.max(1, Math.ceil(list.length / cameraPageSize));
  const activeCameraPage = Math.min(cameraPage, cameraPageCount - 1);
  const visibleCameras = list.slice(
    activeCameraPage * cameraPageSize,
    (activeCameraPage + 1) * cameraPageSize,
  );
  const cameraPageStart = activeCameraPage * cameraPageSize;
  const cameraPageEnd = Math.min(cameraPageStart + cameraPageSize, list.length);
  useEffect(() => {
    setCameraPage((current) => Math.min(current, cameraPageCount - 1));
  }, [cameraPageCount]);
  const recent = (events.data ?? [])
    .slice()
    .sort((a, b) => b.sequence - a.sequence)
    .slice(0, 8);
  const running = list.filter((item) => item.running).length;
  const runForAll = async (command: "start" | "stop") => {
    if (!list.length || commandBusy) return;
    setCommandBusy(true);
    try {
      await Promise.all(
        list
          .filter((camera) => (command === "start" ? !camera.running : camera.running))
          .map((camera) => api.cameraAction(camera.id, command)),
      );
      await cameras.refetch();
    } finally {
      setCommandBusy(false);
    }
  };
  return (
    <>
      <section className="panel dashboard-commandbar">
        <div className="commandbar-title">
          <div className="commandbar-icon"><LayoutDashboard size={17} /></div>
          <div>
            <strong>مرکز کنترل دوربین‌ها</strong>
            <span>نمای شبکه و کنترل سریع سرویس</span>
          </div>
        </div>
        <div className="commandbar-actions">
          <Button variant="soft" icon={LayoutDashboard}>نمای شبکه</Button>
          <Button icon={Play} disabled={commandBusy || !list.length} onClick={() => void runForAll("start")}>شروع همه</Button>
          <Button variant="danger" icon={Pause} disabled={commandBusy || !list.length} onClick={() => void runForAll("stop")}>توقف همه</Button>
          <Button variant="ghost" icon={RefreshCw} onClick={() => void cameras.refetch()}>تازه‌سازی</Button>
          <Button variant="ghost" icon={Settings} onClick={() => navigate("/settings")}>تنظیمات</Button>
        </div>
      </section>
      <section className="hero-card">
        <div className="hero-copy">
          <Badge tone={status.data?.ready ? "green" : "amber"}>
            {status.data?.ready ? "سرویس آنلاین" : "در انتظار سرویس"}
          </Badge>
          <h2>مرکز مدیریت تشخیص</h2>
          <p>
            معادل وب برنامه HshVisionLab برای کنترل دوربین‌ها، ROI، پردازش و
            کلاینت‌ها
          </p>
          <div className="hero-meta">
            <span>
              <Camera size={14} />
              {list.length} دوربین
            </span>
            <span>
              <Activity size={14} />
              Sequence #{status.data?.eventSequence ?? 0}
            </span>
            <span>
              <UsersRound size={14} />
              {people.data?.length ?? 0} شخص
            </span>
          </div>
        </div>
        <div className="hero-orbit">
          <div className="orbit-ring ring-one" />
          <div className="orbit-ring ring-two" />
          <div className="orbit-core">
            <Eye size={26} />
          </div>
        </div>
      </section>
      <div className="stats-grid">
        <Stat
          icon={Camera}
          label="دوربین‌های فعال"
          value={`${running}/${list.length}`}
          detail="دریافت فریم"
          tone="blue"
        />
        <Stat
          icon={Activity}
          label="رخدادهای پایدار"
          value={status.data?.eventSequence ?? 0}
          detail="Event Store"
          tone="purple"
        />
        <Stat
          icon={UsersRound}
          label="افراد پایگاه چهره"
          value={people.data?.length ?? 0}
          detail="شامل Unknownها"
          tone="green"
        />
        <Stat
          icon={Gauge}
          label="میانگین inference"
          value={
            list.length
              ? `${fmt(list.reduce((sum, item) => sum + item.inferenceMs, 0) / list.length)} ms`
              : "—"
          }
          detail="لحظه‌ای"
          tone="amber"
        />
      </div>
      <div className="dashboard-live-layout">
      <section className="panel dashboard-workspace">
        <div className="panel-head">
          <div>
            <h3>{focusedCameraId ? "پیش‌نمایش متمرکز دوربین" : "نمای زندهٔ همهٔ دوربین‌ها"}</h3>
            <span>
              {focusedCameraId
                ? "در این حالت فقط تصویر همین دوربین نمایش داده می‌شود و ROIها قابل ویرایش هستند."
                : "همان الگوی چنددوربینهٔ HshVisionLab؛ برای بزرگ‌نمایی روی تصویر کلیک کنید."}
            </span>
          </div>
          <Button
            variant="soft"
            icon={Settings}
            onClick={() => navigate("/cameras")}
          >
            مدیریت دوربین‌ها
          </Button>
        </div>
        {focusedCameraId ? (
          <CameraFocusWorkspace
            cameraId={focusedCameraId}
            onBack={() => setFocusedCameraId(undefined)}
          />
        ) : (
          <div
            className={`camera-wall count-${Math.min(6, Math.max(1, visibleCameras.length))}`}
          >
            {visibleCameras.map((camera) => (
              <CameraTile
                key={camera.id}
                camera={camera}
                onOpen={() => setFocusedCameraId(camera.id)}
                onEdit={() => navigate(`/cameras?camera=${encodeURIComponent(camera.id)}`)}
                onFullscreen={() => setFocusedCameraId(camera.id)}
              />
            ))}
            {!list.length && (
              <Empty
                icon={Camera}
                title="دوربینی تعریف نشده"
                text="از مدیریت دوربین‌ها اولین منبع تصویر را اضافه کنید."
              />
            )}
          </div>
        )}
        {!focusedCameraId && list.length > cameraPageSize && (
          <div className="camera-pagination" aria-label="صفحه‌بندی دوربین‌ها">
            <Button
              variant="ghost"
              icon={ChevronRight}
              disabled={activeCameraPage === 0}
              onClick={() => setCameraPage((current) => Math.max(0, current - 1))}
            >
              قبلی
            </Button>
            <span>
              صفحهٔ {activeCameraPage + 1} از {cameraPageCount} · نمایش {cameraPageStart + 1} تا {cameraPageEnd} از {list.length}
            </span>
            <Button
              variant="ghost"
              icon={ChevronLeft}
              disabled={activeCameraPage >= cameraPageCount - 1}
              onClick={() => setCameraPage((current) => Math.min(cameraPageCount - 1, current + 1))}
            >
              بعدی
            </Button>
          </div>
        )}
      </section>
      <DetectionHistoryPanel
        events={recent}
        onOpenHistory={() => navigate("/events")}
        onOpenEvent={(eventId) => navigate(`/events/${eventId}`)}
      />
      </div>
      <section className="dashboard-grid dashboard-runtime-grid">
        <section className="panel">
          <div className="panel-head">
            <div>
              <h3>وضعیت runtime</h3>
              <span>منبع، فریم، inference و فریم‌های حذف‌شده</span>
            </div>
            <CircleGauge size={18} />
          </div>
          <div className="runtime-list">
            {list.slice(0, 5).map((camera) => (
              <div className={`runtime-row ${camera.running ? "is-running" : "is-stopped"}`} key={camera.id}>
                <div>
                  <i
                    className={`status-dot ${camera.running ? "online" : "muted"}`}
                  />
                  <span>{camera.name}</span>
                </div>
                <b>
                  {camera.running
                    ? `${fmt(camera.fps)} FPS · ${fmt(camera.inferenceMs)} ms`
                    : "متوقف"}
                </b>
              </div>
            ))}
            {!list.length && (
              <Empty
                icon={Server}
                title="runtime خالی است"
                text="دوربین فعال ندارید."
              />
            )}
          </div>
        </section>
      </section>
    </>
  );
}
function DetectionHistoryPanel({
  events,
  onOpenHistory,
  onOpenEvent,
}: {
  events: DetectionEvent[];
  onOpenHistory: () => void;
  onOpenEvent: (eventId: string) => void;
}) {
  return (
    <section className="panel detection-history-panel">
      <div className="panel-head">
        <div>
          <h3>پنل تشخیص</h3>
          <span>Detected events · All cameras</span>
        </div>
        <div className="head-actions">
          <Badge tone={events.length ? "green" : "neutral"}>{events.length} رخداد</Badge>
          <Button variant="ghost" onClick={onOpenHistory}>
            همه <ChevronLeft size={15} />
          </Button>
        </div>
      </div>
      {!events.length ? (
        <Empty
          icon={Radio}
          title="هنوز تشخیصی ثبت نشده"
          text="پس از دریافت اولین رخداد، crop و مشخصات متنی تشخیص اینجا نمایش داده می‌شود."
        />
      ) : (
        <div className="detected-events-list">
          {events.map((event) => {
            const crop = event.artifacts.find((artifact) => {
              const type = artifact.type.toLocaleLowerCase();
              return type.includes("platecrop") || type.includes("detectioncrop") || type.includes("facealignedcrop") || type.includes("roiraw") || type.includes("roiannotated");
            });
            const camera = s(event.source.cameraName, s(event.source.cameraId, "دوربین نامشخص"));
            return (
              <button className="detected-event-card" key={event.eventId} onClick={() => onOpenEvent(event.eventId)}>
                <div className="detected-event-image">
                  {crop ? <img src={serviceUrl(crop.downloadUrl)} alt={crop.type} /> : <Database size={28} />}
                </div>
                <div className="detected-event-copy">
                  <span className="detected-event-camera">{camera}</span>
                  <strong>{eventTitle(event)}</strong>
                  <span>Confidence: {eventConfidence(event)}</span>
                  <small>{fmtDate(event.occurredAtUtc)}</small>
                  <em>{s(event.source.roiName, event.scenario)}</em>
                </div>
                <ChevronLeft className="detected-event-arrow" size={15} />
              </button>
            );
          })}
        </div>
      )}
    </section>
  );
}
function CameraTile({
  camera,
  onOpen,
  onEdit,
  onFullscreen,
}: {
  camera: CameraStatus;
  onOpen: () => void;
  onEdit: () => void;
  onFullscreen: () => void;
}) {
  const action = useCameraAction();
  const running = camera.running;
  return (
    <article className={`camera-tile ${running ? "camera-running" : "camera-stopped"}`}>
      <button className="camera-tile-open" onClick={onOpen} aria-label={`باز کردن ${camera.name}`}>
        {camera.captureBackend?.toLocaleLowerCase() === "mediamtx" ? (
          <RawMediaMtxStream cameraId={camera.id} enabled={running} overlayIntervalMs={400} />
        ) : (
          <SnapshotImage cameraId={camera.id} alt={camera.name} enabled={running} refreshDelayMs={400} />
        )}
      </button>
      <div className="camera-tile-toolbar">
        <span className="camera-tile-status">
          <i className={`status-dot ${running ? "online" : "warning"}`} />
          {running ? "در حال دریافت" : "متوقف"}
        </span>
        <div>
          <button
            className="camera-control-button"
            title={running ? "توقف دوربین" : "شروع دوربین"}
            disabled={action.isPending}
            onClick={() => action.mutate({ id: camera.id, action: running ? "stop" : "start" })}
          >
            {running ? <Pause size={14} /> : <Play size={14} />}
          </button>
          <button className="camera-control-button" title="ویرایش دوربین" onClick={onEdit}>
            <Pencil size={14} />
          </button>
          <button className="camera-control-button" title="نمای تمام‌صفحه و ROI" onClick={onFullscreen}>
            <Maximize2 size={14} />
          </button>
        </div>
      </div>
      <div className="camera-tile-shade">
        <div>
          <b>{camera.name}</b>
          <span>{running ? `${fmt(camera.fps)} FPS · ${camera.width || "—"}×${camera.height || "—"}` : "منبع متوقف است"}</span>
        </div>
        <Badge tone={running ? "green" : "amber"}>{running ? "فعال" : "متوقف"}</Badge>
      </div>
    </article>
  );
}
function CameraFocusWorkspace({
  cameraId,
  onBack,
}: {
  cameraId: string;
  onBack: () => void;
}) {
  const detail = useCamera(cameraId);
  const statuses = useCameras();
  const action = useCameraAction();
  const mutation = useCameraMutation();
  const [draft, setDraft] = useState<CameraSettings>();
  const [activeRoi, setActiveRoi] = useState<string>();
  const [roiMode, setRoiMode] = useState<"view" | "edit" | "new">("view");
  const [roiDraft, setRoiDraft] = useState<NamedRoi>();
  const [saveError, setSaveError] = useState("");
  useEffect(() => {
    if (!detail.data) return;
    const next = normalizeCameraSettings(detail.data);
    setDraft(next);
    setActiveRoi(next.rois[0]?.id);
    setRoiMode("view");
    setRoiDraft(undefined);
    setSaveError("");
  }, [detail.data]);
  if (detail.isLoading || !draft) return <Loading label="در حال آماده‌سازی تصویر دوربین..." />;
  const status = statuses.data?.find((item) => item.id === cameraId);
  const roi = draft.rois.find((item) => item.id === activeRoi);
  const displayedRoi = roiMode === "view" ? roi : roiDraft;
  const beginEdit = () => {
    if (!roi) return;
    setRoiDraft(clone(roi));
    setSaveError("");
    setRoiMode("edit");
  };
  const beginNew = () => {
    const next: NamedRoi = {
      id: newId(),
      name: `ROI ${draft.rois.length + 1}`,
      enabled: true,
      points: [],
      processing: [],
    };
    setRoiDraft(next);
    setSaveError("");
    setRoiMode("new");
  };
  const saveRoi = () => {
    if (!roiDraft) return;
    if (roiDraft.points.length < 3) {
      setSaveError("برای ذخیرهٔ ROI حداقل سه نقطه روی تصویر مشخص کنید.");
      return;
    }
    const rois = roiMode === "new"
      ? [...draft.rois, roiDraft]
      : draft.rois.map((item) => (item.id === roiDraft.id ? roiDraft : item));
    const nextDraft = { ...draft, rois };
    const payload = prepareCameraForSave(nextDraft);
    const errors = validateCameraDraft(payload);
    if (errors.length) {
      setSaveError(errors.join(" "));
      return;
    }
    setDraft(nextDraft);
    setActiveRoi(roiDraft.id);
    setRoiDraft(undefined);
    setRoiMode("view");
    setSaveError("");
    mutation.mutate(payload);
  };
  const cancelRoi = () => {
    setRoiDraft(undefined);
    setRoiMode("view");
    setSaveError("");
  };
  const removeRoi = () => {
    if (!roi || !window.confirm(`ROI «${roi.name}» حذف شود؟`)) return;
    const rois = draft.rois.filter((item) => item.id !== roi.id);
    setDraft({ ...draft, rois });
    setActiveRoi(rois[0]?.id);
    setSaveError("");
  };
  const updateRoiDraft = (points: RoiPoint[]) => {
    if (roiDraft) setRoiDraft({ ...roiDraft, points });
  };
  const save = () => {
    const payload = prepareCameraForSave(draft);
    const errors = validateCameraDraft(payload);
    if (errors.length) {
      setSaveError(errors.join(" "));
      return;
    }
    setSaveError("");
    mutation.mutate(payload);
  };
  return (
    <section className="camera-focus-view">
      <div className="camera-focus-head">
        <button className="button soft focus-back-button" onClick={onBack} title="بازگشت به همهٔ دوربین‌ها">
          <ChevronLeft size={16} /> بازگشت به همهٔ دوربین‌ها
        </button>
        <div className="camera-focus-title">
          <strong>{draft.name}</strong>
          <span><i className={`status-dot ${status?.running ? "online" : "warning"}`} />{status?.running ? "در حال دریافت" : "متوقف"}</span>
        </div>
        <div className="camera-focus-actions">
          {status?.running ? (
            <Button variant="soft" icon={Pause} onClick={() => action.mutate({ id: cameraId, action: "stop" })}>توقف</Button>
          ) : (
            <Button variant="soft" icon={Play} onClick={() => action.mutate({ id: cameraId, action: "start" })}>شروع</Button>
          )}
          <Button icon={Save} disabled={mutation.isPending || roiMode !== "view"} onClick={save}>{mutation.isPending ? "در حال ذخیره..." : "ذخیره تنظیمات"}</Button>
        </div>
      </div>
      {saveError && <div className="fullscreen-error"><AlertTriangle size={15} />{saveError}</div>}
      <div className="camera-focus-canvas">
        <RoiCanvas
          cameraId={cameraId}
          roi={displayedRoi}
          className="camera-focus-roi"
          live={draft.captureBackend === "MediaMTX"}
          editable={roiMode !== "view"}
          onChange={updateRoiDraft}
        />
      </div>
      <div className="camera-focus-footer">
        <div className="focus-roi-tabs">
          {draft.rois.map((item) => (
            <button key={item.id} disabled={roiMode !== "view"} className={item.id === roi?.id ? "active" : ""} onClick={() => setActiveRoi(item.id)}>
              <i className={`status-dot ${item.enabled ? "online" : "muted"}`} />
              <span>{item.name}</span>
              <small>{item.points.length} نقطه</small>
            </button>
          ))}
          {!draft.rois.length && <span className="focus-no-roi">ROI تعریف نشده است.</span>}
        </div>
        <div className="camera-focus-footer-actions">
          {roiMode === "view" ? (
            <>
              <Button variant="soft" icon={Pencil} disabled={!roi} onClick={beginEdit}>ویرایش ROI</Button>
              <Button variant="soft" icon={Plus} onClick={beginNew}>ROI جدید</Button>
              <Button variant="danger" icon={Trash2} disabled={!roi} onClick={removeRoi}>حذف ROI</Button>
              <span>برای شروع یکی از ابزارهای ROI را انتخاب کنید.</span>
            </>
          ) : (
            <>
              <Button icon={Save} onClick={saveRoi}>ذخیره ROI</Button>
              <Button variant="ghost" onClick={cancelRoi}>لغو</Button>
              <span>{roiMode === "new" ? "حالت ساخت ROI: روی تصویر کلیک کنید و حداقل سه نقطه بگذارید." : "حالت ویرایش ROI: نقاط جدید را روی تصویر اضافه کنید."}</span>
            </>
          )}
        </div>
      </div>
    </section>
  );
}
function CameraFullscreen({
  cameraId,
  onClose,
}: {
  cameraId: string;
  onClose: () => void;
}) {
  const detail = useCamera(cameraId);
  const statuses = useCameras();
  const action = useCameraAction();
  const mutation = useCameraMutation();
  const [draft, setDraft] = useState<CameraSettings>();
  const [activeRoi, setActiveRoi] = useState<string>();
  const [saveError, setSaveError] = useState("");
  useEffect(() => {
    if (!detail.data) return;
    const next = normalizeCameraSettings(detail.data);
    setDraft(next);
    setActiveRoi(next.rois[0]?.id);
    setSaveError("");
  }, [detail.data]);
  if (detail.isLoading || !draft) {
    return (
      <div className="modal-backdrop camera-fullscreen-backdrop">
        <section className="camera-fullscreen loading-shell">
          <Loading label="در حال آماده‌سازی نمای کامل دوربین..." />
        </section>
      </div>
    );
  }
  const status = statuses.data?.find((item) => item.id === cameraId);
  const roi = draft.rois.find((item) => item.id === activeRoi);
  const addRoi = () => {
    const next: NamedRoi = {
      id: newId(),
      name: `ROI ${draft.rois.length + 1}`,
      enabled: true,
      points: [
        { x: 0.05, y: 0.05 },
        { x: 0.95, y: 0.05 },
        { x: 0.95, y: 0.95 },
        { x: 0.05, y: 0.95 },
      ],
      processing: [],
    };
    setDraft({ ...draft, rois: [...draft.rois, next] });
    setActiveRoi(next.id);
  };
  const removeRoi = () => {
    if (!roi) return;
    const rois = draft.rois.filter((item) => item.id !== roi.id);
    setDraft({ ...draft, rois });
    setActiveRoi(rois[0]?.id);
  };
  const updateRoi = (next: NamedRoi) =>
    setDraft({
      ...draft,
      rois: draft.rois.map((item) => (item.id === next.id ? next : item)),
    });
  const save = () => {
    const payload = prepareCameraForSave(draft);
    const errors = validateCameraDraft(payload);
    if (errors.length) {
      setSaveError(errors.join(" "));
      return;
    }
    setSaveError("");
    mutation.mutate(payload, { onSuccess: onClose });
  };
  return (
    <div className="modal-backdrop camera-fullscreen-backdrop">
      <section className="camera-fullscreen" aria-label="نمای تمام‌صفحه دوربین">
        <header className="camera-fullscreen-head">
          <div className="camera-fullscreen-title">
            <div className="camera-title-icon"><Camera size={22} /></div>
            <div>
              <div className="title-row">
                <h2>{draft.name}</h2>
                <Badge tone={status?.running ? "green" : "amber"}>
                  {status?.running ? "در حال دریافت" : "متوقف"}
                </Badge>
              </div>
              <span>{draft.sourceUrl || "منبع تنظیم نشده"}</span>
            </div>
          </div>
          <div className="title-actions">
            {status?.running ? (
              <Button variant="soft" icon={Pause} onClick={() => action.mutate({ id: cameraId, action: "stop" })}>
                توقف
              </Button>
            ) : (
              <Button variant="soft" icon={Play} onClick={() => action.mutate({ id: cameraId, action: "start" })}>
                شروع
              </Button>
            )}
            <Button icon={Save} disabled={mutation.isPending} onClick={save}>
              {mutation.isPending ? "در حال ذخیره..." : "ذخیره ROI"}
            </Button>
            <button className="icon-button fullscreen-close" onClick={onClose} title="بستن نمای کامل">
              <Minimize2 size={18} />
            </button>
          </div>
        </header>
        {saveError && <div className="fullscreen-error"><AlertTriangle size={15} />{saveError}</div>}
        <div className="camera-fullscreen-body">
          <div className="fullscreen-camera-stage">
            <RoiCanvas
              cameraId={cameraId}
              roi={roi}
              className="fullscreen-roi"
              live={draft.captureBackend === "MediaMTX"}
              onChange={(points) => roi && updateRoi({ ...roi, points })}
            />
          </div>
          <aside className="fullscreen-inspector">
            <div className="inspector-heading">
              <div>
                <small>کنترل دوربین</small>
                <h3>وضعیت و ROI</h3>
              </div>
              <Badge tone={status?.running ? "green" : "amber"}>{status?.processingState ?? "—"}</Badge>
            </div>
            <div className="fullscreen-runtime-grid">
              <div><small>FPS</small><b>{fmt(status?.fps)}</b></div>
              <div><small>Inference</small><b>{fmt(status?.inferenceMs)} ms</b></div>
              <div><small>فریم ورودی</small><b>{status?.width || "—"}×{status?.height || "—"}</b></div>
              <div><small>ROI فعال</small><b>{draft.rois.filter((item) => item.enabled).length}</b></div>
            </div>
            <div className="fullscreen-roi-toolbar">
              <div>
                <h4>ناحیه‌های تشخیص</h4>
                <span>برای ویرایش، ROI را انتخاب کنید و روی تصویر نقطه اضافه کنید.</span>
              </div>
              <Button variant="soft" icon={Plus} onClick={addRoi}>ROI جدید</Button>
            </div>
            <div className="fullscreen-roi-list">
              {draft.rois.map((item) => (
                <button
                  key={item.id}
                  className={item.id === roi?.id ? "active" : ""}
                  onClick={() => setActiveRoi(item.id)}
                >
                  <i className={`status-dot ${item.enabled ? "online" : "muted"}`} />
                  <span><b>{item.name}</b><small>{item.points.length} نقطه · {item.processing.length} task</small></span>
                  <ChevronLeft size={14} />
                </button>
              ))}
              {!draft.rois.length && <Empty icon={Database} title="ROI وجود ندارد" text="برای شروع ROI جدید بسازید." />}
            </div>
            <div className="fullscreen-actions">
              <Button variant="danger" icon={Trash2} disabled={!roi} onClick={removeRoi}>حذف ROI انتخاب‌شده</Button>
              <span><b>راهنما:</b> کلیک روی تصویر نقطه اضافه می‌کند؛ دکمهٔ برگشت برای حذف آخرین نقطه است.</span>
            </div>
          </aside>
        </div>
      </section>
    </div>
  );
}
function SnapshotImage({
  cameraId,
  alt,
  enabled = true,
  className,
  refreshKey,
  refreshDelayMs = 400,
}: {
  cameraId: string;
  alt: string;
  enabled?: boolean;
  className?: string;
  refreshKey?: number;
  refreshDelayMs?: number;
}) {
  const [src, setSrc] = useState(() => (enabled ? api.snapshotUrl(cameraId) : ""));
  const timer = useRef<number>();
  const pageVisible = usePageVisible();
  const active = enabled && pageVisible;
  useEffect(() => {
    if (timer.current) window.clearTimeout(timer.current);
    if (!active) {
      if (!enabled) setSrc("");
      return () => undefined;
    }
    setSrc(api.snapshotUrl(cameraId));
    return () => {
      if (timer.current) window.clearTimeout(timer.current);
    };
  }, [active, cameraId, refreshKey]);
  const schedule = () => {
    if (!active) return;
    if (timer.current) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(
      () => setSrc(api.snapshotUrl(cameraId)),
      refreshDelayMs,
    );
  };
  return (
    <img
      className={className}
      src={src || undefined}
      alt={alt}
      onLoad={schedule}
      onError={() => {
        if (!active) return;
        if (timer.current) window.clearTimeout(timer.current);
        timer.current = window.setTimeout(
          () => setSrc(api.snapshotUrl(cameraId)),
          500,
        );
      }}
    />
  );
}

function usePageVisible() {
  const [visible, setVisible] = useState(
    () => typeof document === "undefined" || document.visibilityState === "visible",
  );
  useEffect(() => {
    const update = () => setVisible(document.visibilityState === "visible");
    document.addEventListener("visibilitychange", update);
    return () => document.removeEventListener("visibilitychange", update);
  }, []);
  return visible;
}

function Cameras() {
  const list = useCameras();
  const [searchParams] = useSearchParams();
  const [selectedId, setSelectedId] = useState<string>(() => searchParams.get("camera") ?? "");
  const [createOpen, setCreateOpen] = useState(false);
  const [filter, setFilter] = useState("");
  useEffect(() => {
    const fromUrl = searchParams.get("camera");
    if (fromUrl) setSelectedId(fromUrl);
  }, [searchParams]);
  const create = useMutation({
    mutationFn: (body: Partial<CameraSettings>) => api.createCamera(body),
    onSuccess: (camera) => {
      setSelectedId(camera.id);
      setCreateOpen(false);
      void list.refetch();
    },
  });
  const filtered =
    list.data?.filter((item) =>
      item.name.toLocaleLowerCase().includes(filter.toLocaleLowerCase()),
    ) ?? [];
  return (
    <>
      <PageHead
        title="دوربین‌ها و تشخیص"
        description="مدیریت کامل چنددوربینه، ROIهای چندضلعی، Motion Gate و زنجیرهٔ پردازش هر ROI."
        action={
          <Button icon={Plus} onClick={() => setCreateOpen(true)}>
            افزودن دوربین
          </Button>
        }
      />
      {createOpen && (
        <CreateCamera
          onCancel={() => setCreateOpen(false)}
          onCreate={(body) => create.mutate(body)}
          pending={create.isPending}
        />
      )}
      {createOpen ? null : (
        <div className="camera-layout">
          <aside className="camera-list panel">
            <div className="panel-head compact">
              <div>
                <h3>دوربین‌ها</h3>
                <span>{filtered.length} منبع تصویر</span>
              </div>
              <Search size={16} />
            </div>
            <div className="list-filter">
              <Search size={14} />
              <input
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="جست‌وجوی دوربین"
              />
            </div>
            {filtered.map((camera) => (
              <button
                className={`camera-list-item ${selectedId === camera.id ? "selected" : ""} ${camera.running ? "is-running" : "is-stopped"}`}
                key={camera.id}
                onClick={() => setSelectedId(camera.id)}
              >
                <div className="camera-list-icon">
                  <Camera size={18} />
                </div>
                <div>
                  <b>{camera.name}</b>
                  <span>
                    {camera.running
                      ? `${fmt(camera.fps)} FPS · ${fmt(camera.inferenceMs)} ms`
                      : "متوقف"}
                  </span>
                </div>
                <span
                  className={`status-dot ${camera.running ? "online" : "muted"}`}
                />
              </button>
            ))}
            {!filtered.length && (
              <Empty
                icon={Camera}
                title="بدون دوربین"
                text="برای شروع یک منبع تصویر اضافه کنید."
              />
            )}
          </aside>
          <section className="camera-editor">
            {selectedId ? (
              <CameraEditor id={selectedId} />
            ) : (
              <div className="panel editor-placeholder">
                <Camera size={35} />
                <b>یک دوربین را انتخاب کنید</b>
                <span>
                  مانند فرم CameraSettingsForm، تنظیمات General و Processing در
                  اینجا قابل ویرایش است.
                </span>
              </div>
            )}
          </section>
        </div>
      )}
    </>
  );
}
function CreateCamera({
  onCancel,
  onCreate,
  pending,
}: {
  onCancel: () => void;
  onCreate: (body: Partial<CameraSettings>) => void;
  pending: boolean;
}) {
  const [name, setName] = useState("Camera 1");
  const [sourceUrl, setSourceUrl] = useState("");
  return (
    <section className="panel create-card">
      <div className="panel-head">
        <div>
          <h3>افزودن دوربین</h3>
          <span>منبع RTSP، وب‌کم (مثلاً 0) یا فایل ویدئویی</span>
        </div>
        <button className="icon-button" onClick={onCancel}>
          <X size={17} />
        </button>
      </div>
      <div className="form-grid">
        <Field label="نام">
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Source URL" wide>
          <input
            dir="ltr"
            value={sourceUrl}
            onChange={(e) => setSourceUrl(e.target.value)}
            placeholder="rtsp://... یا 0"
          />
        </Field>
      </div>
      <div className="form-actions">
        <Button variant="ghost" onClick={onCancel}>
          انصراف
        </Button>
        <Button
          icon={Plus}
          disabled={pending || !name.trim() || !sourceUrl.trim()}
          onClick={() => onCreate({ name, sourceUrl })}
        >
          ساخت دوربین
        </Button>
      </div>
    </section>
  );
}

function CameraEditor({ id }: { id: string }) {
  const detail = useCamera(id);
  const statuses = useCameras();
  const serviceStatus = useServiceStatus();
  const mutation = useCameraMutation();
  const action = useCameraAction();
  const models = useModels();
  const [draft, setDraft] = useState<CameraSettings>();
  const [tab, setTab] = useState<"preview" | "general" | "processing">(
    "preview",
  );
  const [activeRoi, setActiveRoi] = useState<string>();
  const [saveError, setSaveError] = useState("");
  useEffect(() => {
    if (detail.data) {
      const next = normalizeCameraSettings(detail.data);
      setDraft(next);
      setActiveRoi(next.rois?.[0]?.id);
      setSaveError("");
    }
  }, [detail.data]);
  if (detail.isLoading || !draft) return <Loading />;
  const status = statuses.data?.find((item) => item.id === id);
  const roi = draft.rois.find((item) => item.id === activeRoi);
  const update = (key: string, value: unknown) =>
    setDraft({ ...draft, [key]: value });
  const updateRoi = (next: NamedRoi) =>
    setDraft({
      ...draft,
      rois: draft.rois.map((item) => (item.id === next.id ? next : item)),
    });
  const addRoi = () => {
    const next: NamedRoi = {
      id: newId(),
      name: `ROI ${draft.rois.length + 1}`,
      enabled: true,
      points: [
        { x: 0.05, y: 0.05 },
        { x: 0.95, y: 0.05 },
        { x: 0.95, y: 0.95 },
        { x: 0.05, y: 0.95 },
      ],
      processing: [],
    };
    setDraft({ ...draft, rois: [...draft.rois, next] });
    setActiveRoi(next.id);
  };
  const removeRoi = () => {
    if (!roi) return;
    const rois = draft.rois.filter((item) => item.id !== roi.id);
    setDraft({ ...draft, rois });
    setActiveRoi(rois[0]?.id);
  };
  const addTask = (type = "Plate") => {
    if (!roi) return;
    const face = type.toLocaleLowerCase() === "face";
    const task: ProcessingTask = {
      id: newId(),
      type: face ? "Face" : "Plate",
      name: face ? "Face detection" : "Plate detection",
      enabled: true,
      maxFps: face ? draft.faceMaxFps : draft.maxFps,
      threads: draft.threads,
      options: face
        ? {
            modelFile: draft.faceModelFile,
            inputSize: draft.faceInputSize,
            preprocessing: draft.facePreprocessing,
            confidence: draft.faceConfidence,
            recordConfidence: draft.faceRecordConfidence,
            recognitionEnabled: draft.faceRecognitionEnabled,
            recognitionModelFile: draft.faceRecognitionModelFile,
            recognitionThreshold: draft.faceRecognitionThreshold,
            matchIou: draft.faceMatchIou,
            trackMaxMisses: draft.faceTrackMaxMisses,
            nmsThreshold: draft.faceNmsThreshold,
            topK: draft.faceTopK,
            unknownMatchThreshold: draft.faceUnknownMatchThreshold,
            eventCooldownSeconds: draft.faceEventCooldownSeconds,
          }
        : {
            modelFile: draft.modelFile,
            inputSize: draft.inputSize,
            preprocessing: draft.platePreprocessing,
            confidence: draft.confidence,
            nmsIoU: draft.nmsIoU,
            trackMaxMisses: draft.trackMaxMisses,
          },
    };
    updateRoi({ ...roi, processing: [...roi.processing, task] });
  };
  const profile = (name: "weak" | "balanced" | "high") => {
    const p =
      name === "weak"
        ? {
            fps: 5,
            threads: 1,
            buffer: 0,
            active: 5,
            idle: 2,
            faceSize: 320,
            topK: 1000,
          }
        : name === "high"
          ? {
              fps: 15,
              threads: 4,
              buffer: 2,
              active: 15,
              idle: 4,
              faceSize: 480,
              topK: 5000,
            }
          : {
              fps: 8,
              threads: 2,
              buffer: 0,
              active: 8,
              idle: 0,
              faceSize: 416,
              topK: 3000,
            };
    const rois = draft.rois.map((current) => ({
      ...current,
      processing: current.processing.map((task) => ({
        ...task,
        maxFps: p.fps,
        threads: p.threads,
        options: {
          ...task.options,
          ...(task.type.toLocaleLowerCase() === "face"
            ? { inputSize: p.faceSize, topK: p.topK }
            : { inputSize: p.faceSize }),
        },
      })),
    }));
    setDraft({
      ...draft,
      maxFps: p.fps,
      faceMaxFps: p.fps,
      threads: p.threads,
      bufferCount: p.buffer,
      activeDetectionFps: p.active,
      idleDetectionFps: p.idle,
      rois,
    });
  };
  const save = () => {
    const payload = prepareCameraForSave(draft);
    const errors = validateCameraDraft(payload);
    if (errors.length) {
      setSaveError(errors.join(" "));
      return;
    }
    setSaveError("");
    mutation.mutate(payload);
  };
  return (
    <div className="editor-stack">
      <section className="panel editor-title">
        <div className="editor-title-main">
          <div className="camera-title-icon">
            <Camera size={24} />
          </div>
          <div>
            <div className="title-row">
              <h2>{draft.name}</h2>
              <Badge tone={status?.running ? "green" : "neutral"}>
                {status?.running ? "در حال کار" : "متوقف"}
              </Badge>
            </div>
            <span>{draft.sourceUrl || "منبع تنظیم نشده"}</span>
          </div>
        </div>
        <div className="title-actions">
          {status?.running ? (
            <Button
              variant="soft"
              icon={Pause}
              onClick={() => action.mutate({ id, action: "stop" })}
            >
              توقف
            </Button>
          ) : (
            <Button
              variant="soft"
              icon={Play}
              onClick={() => action.mutate({ id, action: "start" })}
            >
              شروع
            </Button>
          )}
          <Button icon={Save} disabled={mutation.isPending} onClick={save}>
            {mutation.isPending ? "در حال ذخیره..." : "ذخیره تغییرات"}
          </Button>
        </div>
      </section>
      {serviceStatus.data && !serviceStatus.data.license.isValid && (
        <div className="error-box">
          <AlertTriangle size={19} />
          <div>
            <b>پردازش runtime فعال نیست</b>
            <span>
              {serviceStatus.data.license.message}؛ ROI و task ذخیره می‌شوند اما
              تا رفع این وضعیت inference اجرا نمی‌شود.
            </span>
          </div>
        </div>
      )}
      {(saveError || mutation.error) && (
        <div className="error-box">
          <AlertTriangle size={19} />
          <div>
            <b>تنظیمات ذخیره نشد</b>
            <span>
              {saveError ||
                (mutation.error instanceof Error
                  ? mutation.error.message
                  : "خطای نامشخص")}
            </span>
          </div>
        </div>
      )}
      <div className="tab-bar">
        <button
          className={tab === "preview" ? "active" : ""}
          onClick={() => setTab("preview")}
        >
          <Video size={16} />
          پیش‌نمایش و Drawing
        </button>
        <button
          className={tab === "general" ? "active" : ""}
          onClick={() => setTab("general")}
        >
          <SlidersHorizontal size={16} />
          General / Motion
        </button>
        <button
          className={tab === "processing" ? "active" : ""}
          onClick={() => setTab("processing")}
        >
          <Settings size={16} />
          Processing / ROI
        </button>
      </div>
      {tab === "preview" && (
        <div className="preview-grid">
          <section className="panel live-panel">
            <div className="panel-head compact">
              <div>
                <h3>
                  {draft.captureBackend === "MediaMTX"
                    ? "پخش کم‌تاخیر MediaMTX"
                    : "آخرین فریم کامپوزیت‌شده"}
                </h3>
                <span>
                  {draft.captureBackend === "MediaMTX"
                    ? "WebRTC · استریم پردازش‌شده با ROI، کادرها و نتایج تشخیص"
                    : "ROI و Drawing روی آخرین تصویر سرویس رسم می‌شوند."}
                </span>
              </div>
              <Badge tone={status?.running ? "green" : "amber"}>
                {status?.sourceState ?? "—"}
              </Badge>
            </div>
            <RoiCanvas
              cameraId={id}
              roi={roi}
              live={draft.captureBackend === "MediaMTX"}
              onChange={(next) => roi && updateRoi({ ...roi, points: next })}
            />
            <div className="preview-toolbar">
              <Button variant="soft" icon={Plus} onClick={addRoi}>
                ROI جدید
              </Button>
              <Button
                variant="danger"
                icon={Trash2}
                disabled={!roi}
                onClick={removeRoi}
              >
                حذف ROI
              </Button>
              <span>
                {roi
                  ? `ROI فعال: ${roi.name} · ${roi.points.length} نقطه`
                  : "ROI انتخاب نشده"}
              </span>
            </div>
          </section>
          <RuntimePanel status={status} />
        </div>
      )}
      {tab === "general" && (
        <GeneralSettings
          draft={draft}
          update={update}
          profile={profile}
          models={models.data ?? []}
        />
      )}
      {tab === "processing" && (
        <ProcessingSettings
          draft={draft}
          roi={roi}
          setActiveRoi={setActiveRoi}
          updateRoi={updateRoi}
          update={update}
          models={models.data ?? []}
          addTask={addTask}
        />
      )}
    </div>
  );
}
function RuntimePanel({ status }: { status?: CameraStatus }) {
  return (
    <section className="panel quick-panel">
      <div className="panel-head compact">
        <div>
          <h3>وضعیت لحظه‌ای</h3>
          <span>دادهٔ runtime همان دوربین</span>
        </div>
        <Activity size={17} />
      </div>
      <div className="runtime-list">
        <RuntimeRow
          label="نرخ دریافت"
          value={`${fmt(status?.fps)} FPS`}
          icon={Activity}
        />
        <RuntimeRow
          label="زمان inference"
          value={`${fmt(status?.inferenceMs)} ms`}
          icon={Gauge}
        />
        <RuntimeRow
          label="رزولوشن"
          value={status?.width ? `${status.width}×${status.height}` : "—"}
          icon={Video}
        />
        <RuntimeRow
          label="ROI / task"
          value={`${status?.configuredRoiCount ?? 0} / ${status?.configuredTaskCount ?? 0}`}
          icon={SlidersHorizontal}
        />
        <RuntimeRow
          label="pipeline فعال"
          value={`${status?.activePipelineCount ?? 0} · ${status?.processingState ?? "—"}`}
          icon={CheckCircle2}
        />
        <RuntimeRow
          label="Dropped frames"
          value={status?.droppedFrames ?? 0}
          icon={AlertTriangle}
        />
      </div>
      <div className="preview-note">
        <ShieldCheck size={17} />
        <span>
          فریم preview از سرویس می‌آید و UI هرگز مستقیماً به دوربین یا دیتابیس
          دسترسی ندارد.
        </span>
      </div>
    </section>
  );
}
function RuntimeRow({
  icon: Icon,
  label,
  value,
}: {
  icon: typeof Activity;
  label: string;
  value: React.ReactNode;
}) {
  return (
    <div className="runtime-row">
      <div>
        <Icon size={15} />
        <span>{label}</span>
      </div>
      <b>{value}</b>
    </div>
  );
}

function useLiveOverlay(cameraId: string, enabled: boolean, pollDelayMs = 180) {
  const [overlay, setOverlay] = useState<LiveOverlaySnapshot>();
  useEffect(() => {
    let stopped = false;
    let timer: number | undefined;
    if (!enabled || !cameraId) {
      setOverlay(undefined);
      return () => undefined;
    }

    const poll = async () => {
      try {
        const next = await api.overlay(cameraId);
        if (!stopped) setOverlay(next);
      } catch {
        // The raw video must remain available even when the overlay endpoint
        // is temporarily unavailable.
      } finally {
        if (!stopped) timer = window.setTimeout(() => void poll(), pollDelayMs);
      }
    };
    void poll();
    return () => {
      stopped = true;
      if (timer) window.clearTimeout(timer);
    };
  }, [cameraId, enabled, pollDelayMs]);
  return overlay;
}

function overlayColor(item: LiveOverlayDetection) {
  return item.accepted ? "#43e37b" : "#ff5264";
}

function LiveOverlaySvg({ overlay }: { overlay?: LiveOverlaySnapshot }) {
  if (!overlay || overlay.width <= 0 || overlay.height <= 0) return null;
  const pointString = (points: { x: number; y: number }[]) =>
    points.map((point) => `${point.x},${point.y}`).join(" ");
  const fontSize = Math.max(12, Math.min(24, overlay.width / 90));

  return (
    <svg
      className="live-overlay-svg"
      viewBox={`0 0 ${overlay.width} ${overlay.height}`}
      preserveAspectRatio="none"
      aria-hidden="true"
    >
      {overlay.rois.map((roi) => (
        <g key={`roi-${roi.id}`}>
          <polygon
            points={pointString(
              roi.points.map((point) => ({
                x: point.x * overlay.width,
                y: point.y * overlay.height,
              })),
            )}
            fill="none"
            stroke="#ffb400"
            strokeWidth={Math.max(2, overlay.width / 900)}
          />
          {roi.points[0] && (
            <text
              x={roi.points[0].x * overlay.width}
              y={roi.points[0].y * overlay.height}
              fill="#ffb400"
              fontSize={fontSize * 0.78}
              paintOrder="stroke"
              stroke="#111"
              strokeWidth="4"
            >
              {roi.name}
            </text>
          )}
        </g>
      ))}
      {(overlay.motionRois ?? []).map((roi) => (
        <polygon
          key={`motion-roi-${roi.id}`}
          points={pointString(
            roi.points.map((point) => ({
              x: point.x * overlay.width,
              y: point.y * overlay.height,
            })),
          )}
          fill="none"
          stroke="#ffbe00"
          strokeWidth={Math.max(1.5, overlay.width / 900)}
          strokeDasharray="10 8"
          strokeLinecap="round"
        />
      ))}
      {overlay.processingOverlays.map((item, index) => {
        const color = `rgb(${item.red} ${item.green} ${item.blue})`;
        const common = {
          stroke: color,
          strokeWidth: item.thickness,
          fill: item.filled ? color : "none",
        };
        if (item.kind === "Rectangle") {
          return (
            <rect
              key={`primitive-${index}`}
              x={item.bounds.x}
              y={item.bounds.y}
              width={item.bounds.width}
              height={item.bounds.height}
              {...common}
            />
          );
        }
        if (item.kind === "Circle" && item.points[0]) {
          return (
            <circle
              key={`primitive-${index}`}
              cx={item.points[0].x}
              cy={item.points[0].y}
              r={item.radius}
              {...common}
            />
          );
        }
        if (item.kind === "Points") {
          return item.points.map((point, pointIndex) => (
            <circle
              key={`primitive-${index}-${pointIndex}`}
              cx={point.x}
              cy={point.y}
              r={item.radius}
              fill={color}
            />
          ));
        }
        return (
          <polyline
            key={`primitive-${index}`}
            points={pointString(item.points)}
            {...common}
            fill={item.kind === "Polygon" && item.filled ? color : "none"}
          />
        );
      })}
      {overlay.detections.map((item, index) => {
        const color = overlayColor(item);
        const label = `${item.text || item.label}${item.trackId != null ? ` #${item.trackId}` : ""} ${Math.round(item.confidence * 100)}%`;
        const textY = item.bounds.y + item.bounds.height + fontSize + 3 > overlay.height
          ? Math.max(fontSize, item.bounds.y - 4)
          : item.bounds.y + item.bounds.height + fontSize;
        return (
          <g key={`detection-${index}`}>
            <rect
              x={item.bounds.x}
              y={item.bounds.y}
              width={Math.max(1, item.bounds.width)}
              height={Math.max(1, item.bounds.height)}
              fill="none"
              stroke={color}
              strokeWidth={Math.max(2, overlay.width / 700)}
            />
            <text
              x={item.bounds.x}
              y={textY}
              fill={color}
              fontSize={fontSize}
              fontWeight="700"
              paintOrder="stroke"
              stroke="#050c15"
              strokeWidth="5"
            >
              {label}
            </text>
          </g>
        );
      })}
    </svg>
  );
}

function RawMediaMtxStream({
  cameraId,
  enabled,
  className,
  overlayIntervalMs = 180,
  onLoadedMetadata,
}: {
  cameraId: string;
  enabled: boolean;
  className?: string;
  overlayIntervalMs?: number;
  onLoadedMetadata?: React.ReactEventHandler<HTMLVideoElement>;
}) {
  const createViewerId = () => {
    const uuid = globalThis.crypto?.randomUUID?.();
    if (uuid) return `web-${uuid.replaceAll("-", "")}`;

    // LAN-hosted HTTP pages are not secure contexts, so randomUUID() may be
    // unavailable even though WebRTC itself is supported by the browser.
    const random = Math.random().toString(36).slice(2);
    return `web-${Date.now().toString(36)}${random}`;
  };
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState("");
  const pageVisible = usePageVisible();
  const active = enabled && pageVisible;
  const overlay = useLiveOverlay(cameraId, active, overlayIntervalMs);

  useEffect(() => {
    let disposed = false;
    let connecting = false;
    let reconnecting = false;
    let reconnectTimer: number | undefined;
    let healthTimer: number | undefined;
    let reconnectAttempt = 0;
    const reconnectDelays = [2000, 5000, 10000, 30000];
    type ActiveSession = {
      pc: RTCPeerConnection;
      viewerId: string;
      stream?: MediaStream;
      lastProgressAt: number;
      lastFramesDecoded?: number;
      lastVideoTime?: number;
      highJitterSince?: number;
      closed?: boolean;
    };
    let activeSession: ActiveSession | undefined;

    const waitForIce = async (current: RTCPeerConnection) => {
      if (current.iceGatheringState === "complete") return;
      await new Promise<void>((resolve) => {
        const done = () => {
          if (current.iceGatheringState === "complete") {
            current.removeEventListener("icegatheringstatechange", done);
            resolve();
          }
        };
        current.addEventListener("icegatheringstatechange", done);
        window.setTimeout(() => {
          current.removeEventListener("icegatheringstatechange", done);
          resolve();
        }, 1500);
      });
    };

    const closeSession = (session: ActiveSession | undefined) => {
      if (!session || session.closed) return;
      session.closed = true;
      if (activeSession === session) activeSession = undefined;
      if (pcRef.current === session.pc) pcRef.current = null;
      const video = videoRef.current;
      if (video && video.srcObject === session.stream) video.srcObject = null;
      session.pc.ontrack = null;
      session.pc.onconnectionstatechange = null;
      session.pc.oniceconnectionstatechange = null;
      session.pc.close();
      void api.whep(cameraId, session.viewerId, "DELETE").catch(() => undefined);
    };

    const scheduleReconnect = () => {
      if (disposed || !active || connecting || reconnecting || reconnectTimer !== undefined) return;
      const delay = reconnectDelays[Math.min(reconnectAttempt, reconnectDelays.length - 1)];
      reconnectAttempt = Math.min(reconnectAttempt + 1, reconnectDelays.length - 1);
      setError(`پخش زنده در حال بازیابی است؛ تلاش بعدی تا ${delay / 1000} ثانیه دیگر.`);
      reconnectTimer = window.setTimeout(() => {
        reconnectTimer = undefined;
        void restart();
      }, delay);
    };

    const restart = async () => {
      if (disposed || !active || reconnecting) return;
      reconnecting = true;
      setRunning(false);
      closeSession(activeSession);
      try {
        await connect();
      } finally {
        reconnecting = false;
        // If the camera is temporarily unavailable, keep retrying this tile
        // without requiring a full page refresh.
        if (!disposed && !activeSession) scheduleReconnect();
      }
    };

    const checkHealth = async () => {
      const session = activeSession;
      const video = videoRef.current;
      if (disposed || !session || session.closed || !video) return;

      const now = Date.now();
      let framesDecoded: number | undefined;
      let averageJitterBufferDelay: number | undefined;
      try {
        const stats = await session.pc.getStats();
        stats.forEach((report) => {
          if (report.type !== "inbound-rtp") return;
          const inbound = report as RTCInboundRtpStreamStats & { mediaType?: string };
          if (inbound.kind !== "video" && inbound.mediaType !== "video") return;
          if (typeof inbound.framesDecoded === "number") framesDecoded = inbound.framesDecoded;
          if (
            typeof inbound.jitterBufferDelay === "number" &&
            typeof inbound.jitterBufferEmittedCount === "number" &&
            inbound.jitterBufferEmittedCount > 0
          ) {
            averageJitterBufferDelay =
              inbound.jitterBufferDelay / inbound.jitterBufferEmittedCount;
          }
        });
      } catch {
        // A closed peer connection is handled by the state listeners below.
      }

      const videoAdvanced =
        typeof session.lastVideoTime === "number" &&
        video.currentTime > session.lastVideoTime + 0.01;
      const framesAdvanced =
        typeof framesDecoded === "number" &&
        (typeof session.lastFramesDecoded !== "number" || framesDecoded > session.lastFramesDecoded);
      session.lastVideoTime = video.currentTime;
      session.lastFramesDecoded = framesDecoded;
      if (videoAdvanced || framesAdvanced) session.lastProgressAt = now;

      if (typeof averageJitterBufferDelay === "number" && averageJitterBufferDelay > 1.5) {
        session.highJitterSince ??= now;
      } else {
        session.highJitterSince = undefined;
      }

      const peerIsBroken =
        session.pc.connectionState === "failed" ||
        session.pc.connectionState === "closed" ||
        session.pc.iceConnectionState === "failed" ||
        session.pc.iceConnectionState === "closed";
      const videoIsStalled = now - session.lastProgressAt > 7000;
      const jitterIsGrowing =
        typeof session.highJitterSince === "number" && now - session.highJitterSince > 4000;
      if (peerIsBroken || videoIsStalled || jitterIsGrowing) scheduleReconnect();
    };

    async function connect() {
      if (disposed || !active || !cameraId || connecting) return;
      connecting = true;
      const session: ActiveSession = {
        pc: new RTCPeerConnection(),
        viewerId: createViewerId(),
        lastProgressAt: Date.now(),
      };
      const connection = session.pc;
      activeSession = session;
      pcRef.current = connection;
      setError("");
      connection.addTransceiver("video", { direction: "recvonly" });
      connection.ontrack = (event) => {
        const video = videoRef.current;
        if (disposed || activeSession !== session || !video || event.track.kind !== "video") return;
        // MediaMTX can deliver a valid video track without populating
        // RTCTrackEvent.streams. Build the stream from the track in that
        // case; otherwise the peer is connected but the video stays black.
        session.stream = event.streams[0] ?? new MediaStream([event.track]);
        video.srcObject = session.stream;
        video.defaultPlaybackRate = 1;
        video.playbackRate = 1;
        setRunning(true);
        void video.play().catch(() => undefined);
      };
      connection.onconnectionstatechange = () => {
        if (connection.connectionState === "failed" || connection.connectionState === "closed")
          scheduleReconnect();
        else if (connection.connectionState === "connected") {
          reconnectAttempt = 0;
          setError("");
        }
      };
      connection.oniceconnectionstatechange = () => {
        if (connection.iceConnectionState === "failed" || connection.iceConnectionState === "closed")
          scheduleReconnect();
      };
      try {
        const offer = await connection.createOffer();
        await connection.setLocalDescription(offer);
        await waitForIce(connection);
        if (disposed || activeSession !== session) return;
        const response = await api.whep(
          cameraId,
          session.viewerId,
          "POST",
          connection.localDescription?.sdp ?? "",
        );
        if (!response.ok)
          throw new Error((await response.text()) || `${response.status} ${response.statusText}`);
        if (disposed || activeSession !== session) return;
        await connection.setRemoteDescription({
          type: "answer",
          sdp: await response.text(),
        });
      } catch (cause) {
        if (!disposed && activeSession === session) {
          setError(cause instanceof Error ? cause.message : "اتصال خام MediaMTX برقرار نشد.");
          setRunning(false);
          closeSession(session);
          scheduleReconnect();
        }
      } finally {
        connecting = false;
        if (!disposed && !activeSession) scheduleReconnect();
      }
    }

    if (active && cameraId) {
      void connect();
      healthTimer = window.setInterval(() => void checkHealth(), 2000);
    }
    return () => {
      disposed = true;
      if (reconnectTimer !== undefined) window.clearTimeout(reconnectTimer);
      if (healthTimer !== undefined) window.clearInterval(healthTimer);
      setRunning(false);
      closeSession(activeSession);
    };
  }, [active, cameraId]);

  return (
    <span className={`media-mtx-stream ${className ?? ""}`}>
      <video
        ref={videoRef}
        autoPlay
        muted
        playsInline
        onLoadedMetadata={onLoadedMetadata}
      />
      <LiveOverlaySvg overlay={overlay} />
      {!running && error && <span className="stream-inline-error">{error}</span>}
    </span>
  );
}

function MediaMtxLivePreview({ camera }: { camera?: CameraStatus }) {
  return (
    <section className="panel live-panel">
      <div className="panel-head compact">
        <div>
          <h3>پخش کم‌تاخیر MediaMTX</h3>
          <span>WHEP خام MediaMTX + Overlay سبک سمت کلاینت</span>
        </div>
      </div>
      <div className="video-frame">
        {camera?.id ? (
          <RawMediaMtxStream cameraId={camera.id} enabled={Boolean(camera.running)} />
        ) : (
          <div className="video-empty"><Video size={34} /><span>دوربینی انتخاب نشده</span></div>
        )}
        {!camera?.running && camera?.id && (
          <div className="video-overlay"><Pause size={17} /> دوربین متوقف است</div>
        )}
      </div>
    </section>
  );
}

function CompositeWebRtcStream({
  cameraId,
  enabled,
  className,
  onLoadedMetadata,
}: {
  cameraId: string;
  enabled: boolean;
  className?: string;
  onLoadedMetadata?: React.ReactEventHandler<HTMLVideoElement>;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const connectionRef = useRef<RTCPeerConnection>();
  const sessionRef = useRef<string>();
  const [error, setError] = useState("");

  useEffect(() => {
    let disposed = false;
    const start = async () => {
      if (!enabled || !cameraId) return;
      setError("");
      const connection = new RTCPeerConnection();
      connectionRef.current = connection;
      connection.addTransceiver("video", { direction: "recvonly" });
      connection.ontrack = (event) => {
        if (!disposed && videoRef.current && event.track.kind === "video")
          videoRef.current.srcObject = event.streams[0] ?? new MediaStream([event.track]);
      };
      try {
        const offer = await connection.createOffer();
        await connection.setLocalDescription(offer);
        const answer = await api.webRtcOffer(cameraId, {
          type: "offer",
          sdp: offer.sdp ?? "",
        });
        if (disposed) return;
        await connection.setRemoteDescription({
          type: answer.type as RTCSdpType,
          sdp: answer.sdp,
        });
        sessionRef.current = answer.sessionId;
      } catch (cause) {
        if (!disposed) {
          setError(cause instanceof Error ? cause.message : "پخش زنده برقرار نشد.");
          connection.close();
          connectionRef.current = undefined;
        }
      }
    };
    void start();
    return () => {
      disposed = true;
      connectionRef.current?.close();
      connectionRef.current = undefined;
      const sessionId = sessionRef.current;
      sessionRef.current = undefined;
      if (sessionId) void api.closeWebRtc(sessionId);
    };
  }, [cameraId, enabled]);

  return (
    <>
      <video
        ref={videoRef}
        className={className}
        autoPlay
        muted
        playsInline
        onLoadedMetadata={onLoadedMetadata}
      />
      {error && <span className="stream-inline-error">{error}</span>}
    </>
  );
}

function RoiCanvas({
  cameraId,
  roi,
  className,
  live = false,
  editable = true,
  onChange,
}: {
  cameraId: string;
  roi?: NamedRoi;
  className?: string;
  live?: boolean;
  editable?: boolean;
  onChange: (points: { x: number; y: number }[]) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const refreshTimer = useRef<number>();
  const [stamp, setStamp] = useState(Date.now());
  const [imageSize, setImageSize] = useState({ width: 16, height: 9 });
  const [imageSource, setImageSource] = useState(() =>
    api.snapshotUrl(cameraId),
  );
  useEffect(() => {
    if (refreshTimer.current) window.clearTimeout(refreshTimer.current);
    setImageSource(api.snapshotUrl(cameraId));
    return () => {
      if (refreshTimer.current) window.clearTimeout(refreshTimer.current);
    };
  }, [cameraId, stamp]);
  const scheduleRefresh = () => {
    if (refreshTimer.current) window.clearTimeout(refreshTimer.current);
    refreshTimer.current = window.setTimeout(() => setStamp(Date.now()), 250);
  };
  const edit = (event: React.MouseEvent) => {
    if (!editable || !roi || !stageRef.current) return;
    const rect = stageRef.current.getBoundingClientRect();
    const x = Math.max(
      0,
      Math.min(1, (event.clientX - rect.left) / rect.width),
    );
    const y = Math.max(
      0,
      Math.min(1, (event.clientY - rect.top) / rect.height),
    );
    onChange([...roi.points, { x, y }]);
  };
  const undo = () => roi && onChange(roi.points.slice(0, -1));
  return (
    <div className={`roi-canvas-wrap ${className ?? ""}`}>
      <div className={`roi-canvas ${editable ? "is-editable" : "is-readonly"}`} ref={ref} onClick={edit}>
        <div
          ref={stageRef}
          className="roi-image-stage"
          style={{ aspectRatio: `${imageSize.width} / ${imageSize.height}` }}
        >
          {live ? (
            <RawMediaMtxStream
              cameraId={cameraId}
              enabled
              onLoadedMetadata={(event) =>
                setImageSize({
                  width: event.currentTarget.videoWidth || 16,
                  height: event.currentTarget.videoHeight || 9,
                })
              }
            />
          ) : (
            <img
              src={imageSource}
              alt="آخرین تصویر دوربین"
              onLoad={(event) => {
                setImageSize({
                  width: event.currentTarget.naturalWidth || 16,
                  height: event.currentTarget.naturalHeight || 9,
                });
                scheduleRefresh();
              }}
              onError={scheduleRefresh}
            />
          )}
          <svg viewBox="0 0 1 1" preserveAspectRatio="none">
            <polygon
              points={(roi?.points ?? []).map((p) => `${p.x},${p.y}`).join(" ")}
              fill="rgba(61,143,247,.15)"
              stroke="#62b0ff"
              strokeWidth=".006"
            />
            {(roi?.points ?? []).map((p, index) => (
              <circle
                key={index}
                cx={p.x}
                cy={p.y}
                r=".012"
                fill="#fff"
                stroke="#318af0"
                strokeWidth=".004"
              />
            ))}
          </svg>
        </div>
        {!roi && (
          <div className="video-empty">
            <Camera size={32} />
            <span>یک ROI انتخاب یا ایجاد کنید</span>
          </div>
        )}
      </div>
      <div className="canvas-actions">
        <button
          className="icon-button"
          onClick={(e) => {
            e.stopPropagation();
            setStamp(Date.now());
          }}
          title="تازه‌سازی"
        >
          <RefreshCw size={15} />
        </button>
        <button
          className="icon-button"
          onClick={(e) => {
            e.stopPropagation();
            undo();
          }}
          disabled={!editable || !roi?.points.length}
          title="حذف آخرین نقطه"
        >
          <Move size={15} />
        </button>
        <span>{editable ? "کلیک روی تصویر: افزودن نقطه · آخرین نقطه را با Undo حذف کنید" : "برای ویرایش، ابزار ویرایش ROI یا ROI جدید را انتخاب کنید."}</span>
      </div>
    </div>
  );
}
function GeneralSettings({
  draft,
  update,
  profile,
  models,
}: {
  draft: CameraSettings;
  update: (key: string, value: unknown) => void;
  profile: (name: "weak" | "balanced" | "high") => void;
  models: ModelInfo[];
}) {
  return (
    <div className="settings-grid">
      <section className="panel">
        <div className="panel-head">
          <div>
            <h3>General و Capture</h3>
            <span>معادل تب General فرم تنظیمات دوربین</span>
          </div>
        </div>
        <div className="form-grid">
          <Field label="نام دوربین">
            <input
              value={draft.name}
              onChange={(e) => update("name", e.target.value)}
            />
          </Field>
          <Field label="Source URL" wide>
            <input
              dir="ltr"
              value={draft.sourceUrl}
              onChange={(e) => update("sourceUrl", e.target.value)}
            />
          </Field>
          <Field label="RTSP receiver">
            <select
              value={draft.captureBackend}
              onChange={(e) => update("captureBackend", e.target.value)}
            >
              <option>FFmpeg</option>
              <option>LibVLC</option>
              <option>MediaMTX</option>
            </select>
          </Field>
          <Field label="Transport">
            <select
              value={draft.transport}
              onChange={(e) => update("transport", e.target.value)}
            >
              <option>TCP</option>
              <option>UDP</option>
            </select>
          </Field>
          <Field label="Reconnect delay (sec)">
            <input
              type="number"
              min="0"
              value={draft.reconnectDelaySec}
              onChange={(e) =>
                update("reconnectDelaySec", Number(e.target.value))
              }
            />
          </Field>
          <Field label="Buffer count" hint="0 یعنی فقط آخرین فریم">
            <input
              type="number"
              min="0"
              value={draft.bufferCount}
              onChange={(e) => update("bufferCount", Number(e.target.value))}
            />
          </Field>
          <label className="check-field">
            <input
              type="checkbox"
              checked={draft.drawBoxes}
              onChange={(e) => update("drawBoxes", e.target.checked)}
            />
            <span>نمایش Drawing، ROI و کادر تشخیص</span>
          </label>
          <Field label="مدت نمایش کادر تشخیص (ms)" hint="برای Face و Plate مستقل نگه‌داری می‌شود">
            <input
              type="number"
              min="0"
              max="60000"
              step="100"
              value={draft.detectionOverlayHoldMs}
              onChange={(e) => update("detectionOverlayHoldMs", Number(e.target.value))}
            />
          </Field>
        </div>
      </section>
      <section className="panel">
        <div className="panel-head">
          <div>
            <h3>Motion Gate و نرخ‌ها</h3>
            <span>برای حفظ latency، inference در حالت idle کنترل می‌شود.</span>
          </div>
          <Toggle
            checked={draft.motionGateEnabled}
            onChange={(value) => update("motionGateEnabled", value)}
          />
        </div>
        <div className="form-grid">
          <Field label="Motion FPS">
            <input
              type="number"
              min="1"
              value={draft.motionFps}
              onChange={(e) => update("motionFps", Number(e.target.value))}
            />
          </Field>
          <Field label="Motion threshold">
            <input
              type="number"
              value={draft.motionThreshold}
              onChange={(e) =>
                update("motionThreshold", Number(e.target.value))
              }
            />
          </Field>
          <Field label="Changed percent">
            <input
              type="number"
              step=".01"
              value={draft.motionChangedPercent}
              onChange={(e) =>
                update("motionChangedPercent", Number(e.target.value))
              }
            />
          </Field>
          <Field label="ROI scale %">
            <input
              type="number"
              min="25"
              max="300"
              value={draft.motionRoiScalePercent}
              onChange={(e) =>
                update("motionRoiScalePercent", Number(e.target.value))
              }
            />
          </Field>
          <Field label="Motion hold (ms)">
            <input
              type="number"
              min="0"
              value={draft.motionHoldMs}
              onChange={(e) => update("motionHoldMs", Number(e.target.value))}
            />
          </Field>
          <Field label="Active detection FPS">
            <input
              type="number"
              min="0"
              value={draft.activeDetectionFps}
              onChange={(e) =>
                update("activeDetectionFps", Number(e.target.value))
              }
            />
          </Field>
          <Field label="Idle detection FPS" hint="0 یعنی توقف inference">
            <input
              type="number"
              min="0"
              value={draft.idleDetectionFps}
              onChange={(e) =>
                update("idleDetectionFps", Number(e.target.value))
              }
            />
          </Field>
        </div>
      </section>
      <section className="panel profile-panel">
        <div className="panel-head">
          <div>
            <h3>Performance profiles</h3>
            <span>
              thresholdها را تغییر نمی‌دهد؛ فقط سرعت و مصرف را تنظیم می‌کند.
            </span>
          </div>
          <Gauge size={18} />
        </div>
        <div className="profile-grid">
          <button onClick={() => profile("weak")}>
            <b>Weak / virtual 6-core</b>
            <span>INT8 · ۵ FPS · یک thread</span>
          </button>
          <button className="recommended" onClick={() => profile("balanced")}>
            <b>Balanced / normal system</b>
            <span>پیشنهادی · ۸ FPS · دو thread</span>
          </button>
          <button onClick={() => profile("high")}>
            <b>High / realtime</b>
            <span>۱۵ FPS · چهار thread</span>
          </button>
        </div>
        <div className="model-list">
          <b>مدل‌های قابل انتخاب سرویس</b>
          {models.slice(0, 8).map((model) => (
            <span key={model.relativePath}>
              {model.module} · {model.name}
            </span>
          ))}
          {!models.length && (
            <small>
              فهرست مدل در حال حاضر خالی است یا endpoint در دسترس نیست.
            </small>
          )}
        </div>
      </section>
    </div>
  );
}
function ProcessingSettings({
  draft,
  roi,
  setActiveRoi,
  updateRoi,
  update,
  models,
  addTask,
}: {
  draft: CameraSettings;
  roi?: NamedRoi;
  setActiveRoi: (id: string) => void;
  updateRoi: (roi: NamedRoi) => void;
  update: (key: string, value: unknown) => void;
  models: ModelInfo[];
  addTask: (type?: string) => void;
}) {
  return (
    <section className="panel processing-panel">
      <div className="panel-head">
        <div>
          <h3>Processing tree</h3>
          <span>
            ترتیب ROIها و taskها همان ترتیب اجرای runtime است؛ تنظیمات هر task
            مستقل است.
          </span>
        </div>
        <div className="head-actions">
          <Button
            variant="soft"
            icon={Plus}
            onClick={() => addTask("Plate")}
            disabled={!roi}
          >
            Plate
          </Button>
          <Button
            variant="soft"
            icon={Plus}
            onClick={() => addTask("Face")}
            disabled={!roi}
          >
            Face
          </Button>
        </div>
      </div>
      <div className="processing-body">
        <aside className="roi-tree">
          {draft.rois.map((item) => (
            <button
              key={item.id}
              className={roi?.id === item.id ? "active" : ""}
              onClick={() => setActiveRoi(item.id)}
            >
              <div>
                <b>{item.name}</b>
                <span>
                  {item.processing.length} task ·{" "}
                  {item.enabled ? "فعال" : "غیرفعال"}
                </span>
              </div>
              <i
                className={`status-dot ${item.enabled ? "online" : "muted"}`}
              />
            </button>
          ))}
          {!draft.rois.length && (
            <Empty
              icon={Radio}
              title="ROI ندارید"
              text="در تب preview یک ROI بسازید."
            />
          )}
        </aside>
        <div className="task-editor">
          {roi ? (
            <>
              <div className="roi-editor-head">
                <Field label="نام ROI">
                  <input
                    value={roi.name}
                    onChange={(e) =>
                      updateRoi({ ...roi, name: e.target.value })
                    }
                  />
                </Field>
                <label className="check-field">
                  <input
                    type="checkbox"
                    checked={roi.enabled}
                    onChange={(e) =>
                      updateRoi({ ...roi, enabled: e.target.checked })
                    }
                  />
                  <span>ROI فعال</span>
                </label>
              </div>
              {roi.processing.map((task) => (
                <TaskEditor
                  key={task.id}
                  task={task}
                  onChange={(next) =>
                    updateRoi({
                      ...roi,
                      processing: roi.processing.map((item) =>
                        item.id === task.id ? next : item,
                      ),
                    })
                  }
                  onDelete={() =>
                    updateRoi({
                      ...roi,
                      processing: roi.processing.filter(
                        (item) => item.id !== task.id,
                      ),
                    })
                  }
                  bufferCount={draft.bufferCount}
                  onBufferChange={(value) => update("bufferCount", value)}
                  models={models}
                />
              ))}
              {!roi.processing.length && (
                <Empty
                  icon={SlidersHorizontal}
                  title="پردازشی برای این ROI نیست"
                  text="Plate یا Face را اضافه کنید."
                />
              )}
            </>
          ) : (
            <Empty
              icon={Radio}
              title="ROI را انتخاب کنید"
              text="درخت ROI سمت راست همان ترتیب اجرای تشخیص را نشان می‌دهد."
            />
          )}
        </div>
      </div>
    </section>
  );
}

function ModelSelect({
  label,
  value,
  onChange,
  models,
  capability,
  wide,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  models: ModelInfo[];
  capability: "plate" | "faceDetection" | "faceRecognition";
  wide?: boolean;
}) {
  const modelValue = (model: ModelInfo) => model.name || model.relativePath;
  const modelText = (model: ModelInfo) => model.name || model.relativePath;
  const familyModels = models.filter((model) => {
    const advertised = model.capability?.toLowerCase();
    if (advertised) return advertised === capability.toLowerCase();
    const text = `${model.module} ${model.name} ${model.relativePath}`.toLowerCase();
    if (capability === "plate") return text.includes("plate");
    if (capability === "faceRecognition") return text.includes("face") && text.includes("sface");
    return text.includes("face") && text.includes("yunet");
  });
  const available = familyModels.length ? familyModels : models;
  const currentModel = available.find(
    (model) => model.name === value || model.relativePath === value,
  );
  const options = available.some(
    (model) => model.name === value || model.relativePath === value,
  )
    ? available
    : value
      ? [
          {
            name: value,
            relativePath: value,
            module: "Current",
            packaged: false,
          },
          ...available,
        ]
      : available;
  const selectedValue = currentModel ? modelValue(currentModel) : value;
  return (
    <Field label={label} wide={wide}>
      <select
        dir="ltr"
        value={selectedValue}
        onChange={(e) => onChange(e.target.value)}
      >
        {!options.length && (
          <option value={value}>{value || "مدلی از سرویس گزارش نشده"}</option>
        )}
        {options.map((model) => (
          <option key={`${model.module}:${model.relativePath}:${model.name}`} value={modelValue(model)}>
            {modelText(model)}
          </option>
        ))}
      </select>
    </Field>
  );
}

function modelFamilyModels(
  models: ModelInfo[],
  capability: "plate" | "faceDetection" | "faceRecognition",
) {
  const familyModels = models.filter((model) => {
    const advertised = model.capability?.toLowerCase();
    if (advertised) return advertised === capability.toLowerCase();
    const text = `${model.module} ${model.name} ${model.relativePath}`.toLowerCase();
    if (capability === "plate") return text.includes("plate");
    if (capability === "faceRecognition") return text.includes("face") && text.includes("sface");
    return text.includes("face") && text.includes("yunet");
  });
  return familyModels.length ? familyModels : models;
}

function InputSizeSelect({
  task,
  models,
  capability,
  fallback,
  onChange,
}: {
  task: ProcessingTask;
  models: ModelInfo[];
  capability: "plate" | "faceDetection";
  fallback: number;
  onChange: (value: number) => void;
}) {
  const value = n(option(task, "inputSize", fallback), fallback);
  const modelValue = s(option(task, "modelFile", ""));
  const selected = modelFamilyModels(models, capability).find(
    (model) => model.name === modelValue || model.relativePath === modelValue,
  );
  const declaredSizes = (selected?.inputSizes ?? []).filter(
    (size): size is number => Number.isInteger(size) && size > 0,
  );
  const sizes = declaredSizes.length ? declaredSizes : [value];
  const options = sizes.includes(value) ? sizes : [value, ...sizes];

  return (
    <Field label="Input size">
      <select
        dir="ltr"
        value={String(value)}
        onChange={(e) => onChange(Number(e.target.value))}
      >
        {options.sort((a, b) => a - b).map((size) => (
          <option key={size} value={size}>
            {size}
          </option>
        ))}
      </select>
    </Field>
  );
}

function TaskEditor({
  task,
  onChange,
  onDelete,
  bufferCount,
  onBufferChange,
  models,
}: {
  task: ProcessingTask;
  onChange: (next: ProcessingTask) => void;
  onDelete: () => void;
  bufferCount: number;
  onBufferChange: (value: number) => void;
  models: ModelInfo[];
}) {
  const face = task.type.toLocaleLowerCase() === "face";
  const set = (key: string, value: unknown) =>
    onChange(setOption(task, key, value));
  const setModel = (value: string, capability: "plate" | "faceDetection") => {
    let next = setOption(task, "modelFile", value);
    const selected = modelFamilyModels(models, capability).find(
      (model) => model.name === value || model.relativePath === value,
    );
    const declaredSize = selected?.inputSizes?.find(
      (size) => Number.isInteger(size) && size > 0,
    );
    if (declaredSize) next = setOption(next, "inputSize", declaredSize);
    onChange(next);
  };
  return (
    <div className="task-card detailed">
      <div className="task-card-top">
        <div className="task-symbol">
          {face ? <UserRound size={17} /> : <Radio size={17} />}
        </div>
        <div>
          <b>{task.name}</b>
          <span>{task.type} · مستقل برای همین ROI</span>
        </div>
        <Toggle
          checked={task.enabled}
          onChange={(value) => onChange({ ...task, enabled: value })}
        />
        <button className="icon-button danger-icon" onClick={onDelete}>
          <Trash2 size={16} />
        </button>
      </div>
      <div className="task-name-field">
        <Field label="نام task">
          <input
            value={task.name}
            onChange={(e) => onChange({ ...task, name: e.target.value })}
          />
        </Field>
      </div>
      {!face && (
        <div className="processing-option-section plate-section">
          <div className="processing-section-head">
            <div>
              <b>۱. تشخیص پلاک</b>
              <span>Plate detection · تنظیمات مدل، دقت و tracking</span>
            </div>
            <Toggle
              checked={task.enabled}
              onChange={(value) => onChange({ ...task, enabled: value })}
            />
          </div>
          <div className="task-fields">
            <ModelSelect
              label="Model"
              value={s(option(task, "modelFile", "best.hshmodel"))}
              onChange={(value) => setModel(value, "plate")}
              models={models}
              capability="plate"
              wide
            />
            <InputSizeSelect
              task={task}
              models={models}
              capability="plate"
              fallback={416}
              onChange={(value) => set("inputSize", value)}
            />
            <Field label="Preprocessing">
              <select
                value={s(option(task, "preprocessing", "Standard"))}
                onChange={(e) => set("preprocessing", e.target.value)}
              >
                <option>None</option>
                <option>Standard</option>
                <option>Advanced</option>
              </select>
            </Field>
            <Field label="Confidence">
              <input
                type="number"
                min="0"
                max="1"
                step=".01"
                value={n(option(task, "confidence", 0.35))}
                onChange={(e) => set("confidence", Number(e.target.value))}
              />
            </Field>
            <Field label="NMS IoU">
              <input
                type="number"
                min="0"
                max="1"
                step=".01"
                value={n(option(task, "nmsIoU", 0.45))}
                onChange={(e) => set("nmsIoU", Number(e.target.value))}
              />
            </Field>
            <Field label="Max processing FPS">
              <input
                type="number"
                min="1"
                max="60"
                value={task.maxFps}
                onChange={(e) =>
                  onChange({ ...task, maxFps: Number(e.target.value) })
                }
              />
            </Field>
            <Field label="Threads">
              <input
                type="number"
                min="1"
                max="16"
                value={task.threads}
                onChange={(e) =>
                  onChange({ ...task, threads: Number(e.target.value) })
                }
              />
            </Field>
            <Field label="Buffer count" hint="۰ یعنی فقط جدیدترین فریم">
              <input
                type="number"
                min="0"
                max="10"
                value={bufferCount}
                onChange={(e) => onBufferChange(Number(e.target.value))}
              />
            </Field>
            <Field label="Track max misses">
              <input
                type="number"
                min="1"
                max="60"
                value={n(option(task, "trackMaxMisses", 6))}
                onChange={(e) => set("trackMaxMisses", Number(e.target.value))}
              />
            </Field>
          </div>
        </div>
      )}
      {face && (
        <>
          <div className="processing-option-section face-section">
            <div className="processing-section-head">
              <div>
                <b>۲. تشخیص چهره</b>
                <span>Face detection · YuNet چهره‌ها را پیدا می‌کند</span>
              </div>
              <Toggle
                checked={task.enabled}
                onChange={(value) => onChange({ ...task, enabled: value })}
              />
            </div>
            <div className="task-fields">
              <ModelSelect
                label="Detection model"
                value={s(
                  option(task, "modelFile", "face_yunet_2023mar.hshmodel"),
                )}
                onChange={(value) => setModel(value, "faceDetection")}
                models={models}
                capability="faceDetection"
                wide
              />
              <InputSizeSelect
                task={task}
                models={models}
                capability="faceDetection"
                fallback={640}
                onChange={(value) => set("inputSize", value)}
              />
              <Field label="Preprocessing">
                <select
                  value={s(option(task, "preprocessing", "None"))}
                  onChange={(e) => set("preprocessing", e.target.value)}
                >
                  <option>None</option>
                  <option>Standard</option>
                  <option>Advanced</option>
                </select>
              </Field>
              <Field label="Detection confidence">
                <input
                  type="number"
                  min="0"
                  max="1"
                  step=".01"
                  value={n(option(task, "confidence", 0.8))}
                  onChange={(e) => set("confidence", Number(e.target.value))}
                />
              </Field>
              <Field label="NMS IoU">
                <input
                  type="number"
                  min="0"
                  max="1"
                  step=".01"
                  value={n(option(task, "nmsThreshold", 0.3))}
                  onChange={(e) => set("nmsThreshold", Number(e.target.value))}
                />
              </Field>
              <Field label="Max candidate faces">
                <input
                  type="number"
                  min="1"
                  max="10000"
                  value={n(option(task, "topK", 5000))}
                  onChange={(e) => set("topK", Number(e.target.value))}
                />
              </Field>
            </div>
          </div>
          <div className="processing-option-section identification-section">
            <div className="processing-section-head">
              <div>
                <b>۳. شناسایی چهره</b>
                <span>Face identification · مقایسه با Face DB توسط SFace</span>
              </div>
              <Toggle
                checked={Boolean(option(task, "recognitionEnabled", true))}
                onChange={(value) => set("recognitionEnabled", value)}
              />
            </div>
            <div className="task-fields">
              <ModelSelect
                label="Recognition model (SFace)"
                value={s(
                  option(
                    task,
                    "recognitionModelFile",
                    "face_recognition_sface.hshmodel",
                  ),
                )}
                onChange={(value) => set("recognitionModelFile", value)}
                models={models}
                capability="faceRecognition"
                wide
              />
              <Field label="Known-person threshold">
                <input
                  type="number"
                  min="0"
                  max="1"
                  step=".01"
                  value={n(option(task, "recognitionThreshold", 0.4))}
                  onChange={(e) =>
                    set("recognitionThreshold", Number(e.target.value))
                  }
                />
              </Field>
              <Field label="Unknown-person match threshold">
                <input
                  type="number"
                  min="0"
                  max="1"
                  step=".01"
                  value={n(option(task, "unknownMatchThreshold", 0.35))}
                  onChange={(e) =>
                    set("unknownMatchThreshold", Number(e.target.value))
                  }
                />
              </Field>
            </div>
          </div>
          <div className="processing-option-section tracking-section">
            <div className="processing-section-head">
              <div>
                <b>ردیابی و ثبت سابقه</b>
                <span>Tracking and recording · کنترل نرخ و ثبت رویداد</span>
              </div>
            </div>
            <div className="task-fields">
              <Field label="Max processing FPS">
                <input
                  type="number"
                  min="1"
                  max="60"
                  value={task.maxFps}
                  onChange={(e) =>
                    onChange({ ...task, maxFps: Number(e.target.value) })
                  }
                />
              </Field>
              <Field label="Threads">
                <input
                  type="number"
                  min="1"
                  max="16"
                  value={task.threads}
                  onChange={(e) =>
                    onChange({ ...task, threads: Number(e.target.value) })
                  }
                />
              </Field>
              <Field label="Buffer count" hint="۰ یعنی فقط جدیدترین فریم">
                <input
                  type="number"
                  min="0"
                  max="10"
                  value={bufferCount}
                  onChange={(e) => onBufferChange(Number(e.target.value))}
                />
              </Field>
              <Field label="History record confidence">
                <input
                  type="number"
                  min="0"
                  max="1"
                  step=".01"
                  value={n(option(task, "recordConfidence", 0.8))}
                  onChange={(e) =>
                    set("recordConfidence", Number(e.target.value))
                  }
                />
              </Field>
              <Field
                label="Face sample record cooldown (sec)"
                hint="محدودیت داخلی ثبت نمونهٔ چهره است و به cooldown تریگرها مربوط نیست"
              >
                <input
                  type="number"
                  min="0"
                  value={n(option(task, "eventCooldownSeconds", 60))}
                  onChange={(e) =>
                    set("eventCooldownSeconds", Number(e.target.value))
                  }
                />
              </Field>
              <Field label="Tracking IoU">
                <input
                  type="number"
                  min="0"
                  max="1"
                  step=".01"
                  value={n(option(task, "matchIou", 0.25))}
                  onChange={(e) => set("matchIou", Number(e.target.value))}
                />
              </Field>
              <Field label="Track max misses">
                <input
                  type="number"
                  min="1"
                  max="60"
                  value={n(option(task, "trackMaxMisses", 10))}
                  onChange={(e) => set("trackMaxMisses", Number(e.target.value))}
                />
              </Field>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function LivePreview({ camera }: { camera?: CameraStatus }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [mode, setMode] = useState<"snapshot" | "webrtc">("snapshot");
  const [error, setError] = useState("");
  const session = useRef<string>();
  const pc = useRef<RTCPeerConnection>();
  const [stamp, setStamp] = useState(Date.now());
  useEffect(
    () => () => {
      pc.current?.close();
      if (session.current) void api.closeWebRtc(session.current);
    },
    [],
  );
  const start = async () => {
    if (!camera?.id || !videoRef.current) return;
    setError("");
    try {
      const connection = new RTCPeerConnection();
      pc.current = connection;
      connection.addTransceiver("video", { direction: "recvonly" });
      connection.ontrack = (e) => {
        if (videoRef.current && e.track.kind === "video")
          videoRef.current.srcObject = e.streams[0] ?? new MediaStream([e.track]);
      };
      const offer = await connection.createOffer();
      await connection.setLocalDescription(offer);
      const answer = await api.webRtcOffer(camera.id, {
        type: "offer",
        sdp: offer.sdp ?? "",
      });
      await connection.setRemoteDescription({
        type: answer.type as RTCSdpType,
        sdp: answer.sdp,
      });
      session.current = answer.sessionId;
      setMode("webrtc");
    } catch (e) {
      setError(e instanceof Error ? e.message : "WebRTC برقرار نشد.");
      setMode("snapshot");
    }
  };
  return (
    <section className="panel live-panel">
      <div className="panel-head compact">
        <div>
          <h3>پیش‌نمایش</h3>
          <span>
            {mode === "webrtc"
              ? "WebRTC · stream کامپوزیت‌شده"
              : "Snapshot · آخرین فریم"}
          </span>
        </div>
        <div className="preview-controls">
          <button
            className={`preview-mode ${mode === "webrtc" ? "active" : ""}`}
            onClick={start}
          >
            <Video size={15} /> WebRTC
          </button>
          <button className="icon-button" onClick={() => setStamp(Date.now())}>
            <RefreshCw size={16} />
          </button>
        </div>
      </div>
      <div className="video-frame">
        {mode === "webrtc" ? (
          <video ref={videoRef} autoPlay muted playsInline />
        ) : camera?.id ? (
          <img
            src={api.snapshotUrl(camera.id) + `&t=${stamp}`}
            alt="camera snapshot"
          />
        ) : (
          <div className="video-empty">
            <Camera size={34} />
            <span>دوربینی انتخاب نشده</span>
          </div>
        )}
        {!camera?.running && (
          <div className="video-overlay">
            <Pause size={17} /> دوربین متوقف است
          </div>
        )}
      </div>
      {error && (
        <div className="preview-error">
          <AlertTriangle size={15} />
          {error}
        </div>
      )}
    </section>
  );
}

function Faces() {
  const people = usePeople();
  const health = useQueryClient();
  const [selected, setSelected] = useState<string>();
  const [name, setName] = useState("");
  const [search, setSearch] = useState("");
  const [group, setGroup] = useState(true);
  const [similarOpen, setSimilarOpen] = useState(false);
  const create = useMutation({
    mutationFn: api.createPerson,
    onSuccess: async (person) => {
      await health.invalidateQueries({ queryKey: keys.people, refetchType: "active" });
      setSelected(person.id);
      setName("");
    },
  });
  const rename = useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      api.renamePerson(id, name),
    onSuccess: () => health.invalidateQueries({ queryKey: keys.people, refetchType: "active" }),
  });
  const remove = useMutation({
    mutationFn: api.deletePerson,
    onSuccess: async () => {
      setSelected(undefined);
      await health.invalidateQueries({ queryKey: keys.people, refetchType: "active" });
    },
  });
  const filtered =
    people.data?.filter((person) =>
      person.name.toLocaleLowerCase().includes(search.toLocaleLowerCase()),
    ) ?? [];
  return (
    <>
      <PageHead
        title="پایگاه دادهٔ چهره"
        description="معادل FaceDatabaseForm: افراد، نمونه‌ها، Unknownها، انتقال، ادغام و Similarity."
        action={
          <div className="page-actions">
            <div className="search-box">
              <Search size={17} />
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="جست‌وجوی نام یا شماره"
              />
            </div>
            <Button
              variant="soft"
              icon={UsersRound}
              onClick={() => setSimilarOpen(true)}
            >
              Similar samples
            </Button>
          </div>
        }
      />
      <div className="faces-layout">
        <section className="panel people-panel">
          <div className="panel-head compact">
            <div>
              <h3>People / Samples</h3>
              <span>
                {filtered.length} شخص ·{" "}
                {filtered.reduce((sum, item) => sum + item.samples.length, 0)}{" "}
                نمونه
              </span>
            </div>
            <label className="check-inline">
              <input
                type="checkbox"
                checked={group}
                onChange={(e) => setGroup(e.target.checked)}
              />{" "}
              Group by person
            </label>
          </div>
          <div className={`person-list ${group ? "grouped" : ""}`}>
            {filtered.map((person) => (
              <button
                key={person.id}
                className={`person-row ${selected === person.id ? "selected" : ""}`}
                onClick={() => setSelected(person.id)}
              >
                <div
                  className={`person-avatar ${person.isUnknown ? "unknown" : ""}`}
                >
                  {person.isUnknown ? "?" : person.name.slice(0, 1)}
                </div>
                <div>
                  <b>{person.name}</b>
                  <span>
                    #{String(person.personNumber).padStart(4, "0")} ·{" "}
                    {person.samples.length}/10 sample
                  </span>
                </div>
                <ChevronLeft size={15} />
              </button>
            ))}
            {!filtered.length && (
              <Empty
                icon={UserRound}
                title="فردی پیدا نشد"
                text="از فرم پایین یک شخص بسازید."
              />
            )}
          </div>
          <div className="create-person">
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) =>
                e.key === "Enter" && name.trim() && create.mutate(name.trim())
              }
              placeholder="نام شخص جدید"
            />
            <Button
              icon={Plus}
              onClick={() => name.trim() && create.mutate(name.trim())}
              disabled={create.isPending}
            >
              افزودن
            </Button>
          </div>
          {create.error instanceof Error && (
            <div className="preview-error">
              <AlertTriangle size={15} />
              {create.error.message}
            </div>
          )}
        </section>
        <section className="person-detail">
          {selected ? (
            <PersonDetail
              id={selected}
              people={people.data ?? []}
              onRename={(next) => rename.mutateAsync({ id: selected, name: next })}
              onDelete={() => remove.mutate(selected)}
              renamePending={rename.isPending}
              renameError={rename.error instanceof Error ? rename.error.message : undefined}
              deleteError={remove.error instanceof Error ? remove.error.message : undefined}
            />
          ) : (
            <div className="panel editor-placeholder">
              <UsersRound size={35} />
              <b>یک شخص را انتخاب کنید</b>
              <span>
                در این بخش تمام نمونه‌ها، وضعیت Image missing و عملیات انتقال در
                دسترس است.
              </span>
            </div>
          )}
        </section>
      </div>
      {similarOpen && (
        <SimilarityDialog
          people={people.data ?? []}
          onClose={() => setSimilarOpen(false)}
        />
      )}
    </>
  );
}
function PersonDetail({
  id,
  people,
  onRename,
  onDelete,
  renamePending = false,
  renameError,
  deleteError,
}: {
  id: string;
  people: FaceIdentity[];
  onRename: (name: string) => Promise<void>;
  onDelete: () => void;
  renamePending?: boolean;
  renameError?: string;
  deleteError?: string;
}) {
  const samples = usePersonSamples(id);
  const client = useQueryClient();
  const person = people.find((item) => item.id === id);
  const [edit, setEdit] = useState(false);
  const [nextName, setNextName] = useState(person?.name ?? "");
  const [moveSample, setMoveSample] = useState<string>();
  const upload = useMutation({
    mutationFn: (file: File) => api.uploadSample(id, file),
    onSuccess: () => {
      void samples.refetch();
      void client.invalidateQueries({ queryKey: keys.people });
    },
  });
  const del = useMutation({
    mutationFn: api.deleteSample,
    onSuccess: () => {
      void samples.refetch();
      void client.invalidateQueries({ queryKey: keys.people });
    },
  });
  if (!person) return <ErrorBox message="شخص انتخاب‌شده دیگر وجود ندارد." />;
  const uploadFiles = (files: FileList | null) => {
    if (!files) return;
    Array.from(files).forEach((file) => upload.mutate(file));
  };
  return (
    <div className="editor-stack">
      <section className="panel person-title">
        <div className="person-title-main">
          <div
            className={`person-avatar large ${person.isUnknown ? "unknown" : ""}`}
          >
            {person.isUnknown ? "?" : person.name.slice(0, 1)}
          </div>
          <div>
            {edit ? (
              <div className="edit-name">
                <input
                  value={nextName}
                  onChange={(e) => setNextName(e.target.value)}
                />
                <Button
                  variant="soft"
                  icon={Save}
                  disabled={renamePending}
                  onClick={() => {
                    const value = nextName.trim();
                    if (!value) return;
                    void onRename(value)
                      .then(() => setEdit(false))
                      .catch(() => undefined);
                  }}
                >
                  ثبت
                </Button>
              </div>
            ) : (
              <h2>{person.name}</h2>
            )}
            <span>
              Person #{String(person.personNumber).padStart(4, "0")} ·{" "}
              {person.isUnknown ? "Unknown auto-enrollment" : "Named identity"}{" "}
              · {fmtDate(person.createdAtUtc)}
            </span>
          </div>
        </div>
        <div className="title-actions">
          <Button
            variant="soft"
            icon={Pencil}
            onClick={() => {
              setNextName(person.name);
              setEdit(true);
            }}
          >
            Rename
          </Button>
          <Button variant="danger" icon={Trash2} onClick={onDelete}>
            حذف شخص
          </Button>
        </div>
      </section>
      {(renameError || deleteError) && (
        <div className="preview-error">
          <AlertTriangle size={15} />
          {renameError ?? deleteError}
        </div>
      )}
      <section className="panel">
        <div className="panel-head">
          <div>
            <h3>نمونه‌های این شخص</h3>
            <span>حداکثر ۱۰ نمونه؛ فایل تصویر در SQLite نگهداری می‌شود.</span>
          </div>
          <label className="button primary upload-button">
            <Plus size={16} />
            افزودن تصویر / folder
            <input
              type="file"
              accept="image/*"
              multiple
              onChange={(e) => uploadFiles(e.target.files)}
            />
          </label>
        </div>
        {samples.isLoading ? (
          <Loading />
        ) : (
          <div className="sample-grid detailed-samples">
            {(samples.data ?? []).map((sample) => (
              <SampleCard
                key={sample.id}
                sample={sample}
                people={people}
                moveSample={moveSample === sample.id ? sample.id : undefined}
                onMove={(target) => {
                  void api.moveSample(sample.id, target).then(() => {
                    setMoveSample(undefined);
                    void samples.refetch();
                    void client.invalidateQueries({ queryKey: keys.people });
                  });
                }}
                onStartMove={() => setMoveSample(sample.id)}
                onDelete={() => del.mutate(sample.id)}
              />
            ))}
            {!samples.data?.length && (
              <Empty
                icon={UserRound}
                title="نمونه‌ای ثبت نشده"
                text="برای enrollment یک یا چند تصویر چهره انتخاب کنید."
              />
            )}
          </div>
        )}
      </section>
    </div>
  );
}
function SampleCard({
  sample,
  people,
  moveSample,
  onMove,
  onStartMove,
  onDelete,
}: {
  sample: FaceSample;
  people: FaceIdentity[];
  moveSample?: string;
  onMove: (target: string) => void;
  onStartMove: () => void;
  onDelete: () => void;
}) {
  return (
    <div className="sample-card">
      <img
        src={api.sampleImageUrl(sample.id)}
        alt={sample.originalFileName}
        onError={(e) => {
          e.currentTarget.style.display = "none";
        }}
      />
      <div>
        <b>
          Sample {sample.sampleNumber} · #{sample.personNumber}
        </b>
        <span title={sample.originalFileName}>
          {sample.originalFileName || "legacy-image-missing.jpg"}
        </span>
        <small>
          confidence {fmt(sample.detectionConfidence, 2)} ·{" "}
          {fmtDate(sample.createdAtUtc)}
        </small>
        <div className="sample-actions">
          <button
            className="icon-button"
            title="انتقال به شخص دیگر"
            onClick={onStartMove}
          >
            <Move size={14} />
          </button>
          <button
            className="icon-button danger-icon"
            title="حذف نمونه"
            onClick={onDelete}
          >
            <Trash2 size={14} />
          </button>
          {moveSample && (
            <select
              defaultValue=""
              onChange={(e) => e.target.value && onMove(e.target.value)}
            >
              <option value="">انتخاب مقصد</option>
              {people
                .filter(
                  (item) => item.id !== sample.personId && !item.isUnknown,
                )
                .map((item) => (
                  <option key={item.id} value={item.id}>
                    #{item.personNumber} {item.name}
                  </option>
                ))}
            </select>
          )}
        </div>
      </div>
    </div>
  );
}
function SimilarityDialog({
  people,
  onClose,
}: {
  people: FaceIdentity[];
  onClose: () => void;
}) {
  const [threshold, setThreshold] = useState(0.4);
  const [different, setDifferent] = useState(false);
  const [pairs, setPairs] = useState<FaceSimilarityPair[]>();
  const [busy, setBusy] = useState(false);
  const check = async () => {
    setBusy(true);
    try {
      setPairs(await api.similar(threshold, different));
    } finally {
      setBusy(false);
    }
  };
  const merge = async (pair: FaceSimilarityPair) => {
    if (!confirm(`ادغام ${pair.right.personName} در ${pair.left.personName}؟`))
      return;
    await api.mergePeople(pair.left.personId, pair.right.personId);
    await check();
  };
  return (
    <div className="modal-backdrop">
      <section className="modal-panel similarity-dialog">
        <div className="panel-head">
          <div>
            <h3>Similar face samples</h3>
            <span>threshold پیش‌فرض 0.40 مطابق recognition SFace</span>
          </div>
          <button className="icon-button" onClick={onClose}>
            <X size={18} />
          </button>
        </div>
        <div className="similar-toolbar">
          <Field label="Minimum similarity">
            <input
              type="number"
              min=".3"
              max=".99"
              step=".01"
              value={threshold}
              onChange={(e) => setThreshold(Number(e.target.value))}
            />
          </Field>
          <label className="check-field">
            <input
              type="checkbox"
              checked={different}
              onChange={(e) => setDifferent(e.target.checked)}
            />
            <span>Only different people</span>
          </label>
          <Button icon={Search} onClick={() => void check()}>
            {busy ? "در حال بررسی..." : "Check similarity"}
          </Button>
        </div>
        <div className="similar-results">
          {pairs?.map((pair, index) => (
            <div
              className="similar-pair"
              key={`${pair.left.id}-${pair.right.id}`}
            >
              <img src={api.sampleImageUrl(pair.left.id)} alt="left" />
              <div>
                <b>
                  {pair.left.personName} / sample {pair.left.sampleNumber}
                </b>
                <span>#{pair.left.personNumber}</span>
              </div>
              <strong>{pair.similarity.toFixed(3)}</strong>
              <img src={api.sampleImageUrl(pair.right.id)} alt="right" />
              <div>
                <b>
                  {pair.right.personName} / sample {pair.right.sampleNumber}
                </b>
                <span>#{pair.right.personNumber}</span>
              </div>
              <Button variant="soft" onClick={() => void merge(pair)}>
                ادغام در اولی
              </Button>
            </div>
          ))}
          {pairs && !pairs.length && (
            <Empty
              icon={CheckCircle2}
              title="جفت مشابهی پیدا نشد"
              text="threshold را کمتر کنید یا فیلتر افراد متفاوت را بردارید."
            />
          )}
          {!pairs && (
            <Empty
              icon={UsersRound}
              title="Similarity اجرا نشده"
              text="آستانه را انتخاب و Check را اجرا کنید."
            />
          )}
        </div>
        <div className="modal-foot">
          <span>{people.length} شخص در مقایسه</span>
          <Button variant="ghost" onClick={onClose}>
            بستن
          </Button>
        </div>
      </section>
    </div>
  );
}

function Events() {
  type DeleteMode = "all" | "today" | "7days" | "30days" | "custom";
  type DeleteSelection = { range: { fromUtc?: string; toUtc?: string }; label: string };
  const [filter, setFilter] = useState("");
  const [scenario, setScenario] = useState("");
  const [deleteMode, setDeleteMode] = useState<DeleteMode>("all");
  const [deleteFrom, setDeleteFrom] = useState("");
  const [deleteTo, setDeleteTo] = useState("");
  const [deleteNotice, setDeleteNotice] = useState("");
  const [deleteError, setDeleteError] = useState("");
  const deleteEvents = useDeleteEvents();
  const events = useEvents(
    scenario ? `&scenario=${encodeURIComponent(scenario)}` : "",
    2000,
  );
  const getDeleteSelection = (): DeleteSelection | null => {
    const now = new Date();
    if (deleteMode === "all") return { range: {}, label: "همهٔ تاریخچه" };
    if (deleteMode === "today") {
      const from = new Date(now);
      from.setHours(0, 0, 0, 0);
      return { range: { fromUtc: from.toISOString(), toUtc: now.toISOString() }, label: "رخدادهای امروز" };
    }
    if (deleteMode === "7days" || deleteMode === "30days") {
      const days = deleteMode === "7days" ? 7 : 30;
      const from = new Date(now.getTime() - days * 24 * 60 * 60 * 1000);
      return { range: { fromUtc: from.toISOString(), toUtc: now.toISOString() }, label: `رخدادهای ${days} روز اخیر` };
    }
    if (!deleteFrom || !deleteTo) return null;
    const from = new Date(`${deleteFrom}T00:00:00`);
    const to = new Date(`${deleteTo}T23:59:59.999`);
    if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()) || from > to) return null;
    return { range: { fromUtc: from.toISOString(), toUtc: to.toISOString() }, label: `رخدادهای ${deleteFrom} تا ${deleteTo}` };
  };

  const [selected, setSelected] = useState<string>();
  const list = (events.data ?? [])
    .filter(
      (event) =>
        !filter ||
        JSON.stringify(event)
          .toLocaleLowerCase()
          .includes(filter.toLocaleLowerCase()),
    )
    .slice()
    .sort((a, b) => b.sequence - a.sequence);
  const deletePreview = getDeleteSelection();
  const previewList = deleteMode === "all"
    ? list
    : deletePreview?.range.fromUtc && deletePreview.range.toUtc
      ? list.filter((event) => {
          const occurred = new Date(event.occurredAtUtc).getTime();
          return occurred >= new Date(deletePreview.range.fromUtc!).getTime() && occurred <= new Date(deletePreview.range.toUtc!).getTime();
        })
      : [];

  const runDelete = () => {
    setDeleteError("");
    setDeleteNotice("");
    const selection = getDeleteSelection();
    if (!selection) {
      setDeleteError(deleteMode === "custom" ? "برای حذف بازهٔ سفارشی، تاریخ شروع و پایان معتبر انتخاب کنید." : "بازهٔ انتخابی معتبر نیست.");
      return;
    }
    if (!window.confirm(`آیا ${selection.label} حذف شود؟ این عملیات قابل بازگشت نیست.`)) return;
    deleteEvents.mutate(selection.range, {
      onSuccess: (result) => {
        setSelected(undefined);
        setDeleteNotice(`${result.deletedCount.toLocaleString("fa-IR")} رخداد حذف شد.`);
      },
      onError: (error) => setDeleteError(error instanceof Error ? error.message : "حذف تاریخچه ناموفق بود."),
    });
  };

  const deleteLabel: Record<DeleteMode, string> = {
    all: "حذف همه",
    today: "حذف امروز",
    "7days": "حذف ۷ روز",
    "30days": "حذف ۳۰ روز",
    custom: "حذف بازه",
  };
  return (
    <>
      <PageHead
        className="events-page-head"
        title="تاریخچه تشخیص و evidence"
        description="رخدادها پایدار ذخیره می‌شوند؛ با قطع UI eventها از دست نمی‌روند و پس از اتصال مجدد replay می‌شوند."
        action={
          <div className="page-actions">
            <div className="search-box">
              <Search size={17} />
              <input
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="پلاک، نام، دوربین..."
              />
            </div>
            <select
              className="toolbar-select"
              value={scenario}
              onChange={(e) => setScenario(e.target.value)}
            >
              <option value="">همهٔ سناریوها</option>
              <option value="PlateOnly">پلاک</option>
              <option value="FaceRecognition">چهره</option>
              <option value="PlateFaceAssociation">پلاک + چهره</option>
            </select>
            <div className="event-delete-tools">
              <select
                className="toolbar-select"
                value={deleteMode}
                onChange={(e) => {
                  setDeleteMode(e.target.value as DeleteMode);
                  setDeleteError("");
                  setDeleteNotice("");
                }}
                aria-label="بازه حذف تاریخچه"
              >
                <option value="all">همهٔ تاریخچه</option>
                <option value="today">امروز</option>
                <option value="7days">۷ روز اخیر</option>
                <option value="30days">۳۰ روز اخیر</option>
                <option value="custom">بازهٔ سفارشی</option>
              </select>
              {deleteMode === "custom" && (
                <>
                  <input className="toolbar-select event-date-input" type="date" value={deleteFrom} onChange={(e) => setDeleteFrom(e.target.value)} aria-label="از تاریخ" />
                  <input className="toolbar-select event-date-input" type="date" value={deleteTo} onChange={(e) => setDeleteTo(e.target.value)} aria-label="تا تاریخ" />
                </>
              )}
              <Button variant="danger" icon={Trash2} disabled={deleteEvents.isPending} onClick={runDelete}>
                {deleteEvents.isPending ? "در حال حذف..." : deleteLabel[deleteMode]}
              </Button>
              {(deleteError || deleteNotice) && <small className={deleteError ? "event-delete-error" : "event-delete-notice"}>{deleteError || deleteNotice}</small>}
            </div>
          </div>
        }
      />
      <div className={`events-layout ${selected ? "has-selection" : "empty-selection"}`}>
        <section className="event-side event-preview-top">
          {selected ? (
            <EventPreview id={selected} />
          ) : (
            <div className="panel editor-placeholder">
              <Activity size={35} />
              <b>یک رخداد را انتخاب کنید</b>
              <span>
                فریم کامل، cropهای ROI/Plate/Face و payload جزئی آن نمایش داده
                می‌شود.
              </span>
            </div>
          )}
        </section>
        <section className="panel events-panel">
          <div className="panel-head compact">
            <div>
              <h3>Event Store</h3>
              <span>{previewList.length} رخداد در محدودهٔ حذف · {list.length} رخداد بارگذاری‌شده</span>
            </div>
            <Button
              variant="soft"
              icon={RefreshCw}
              onClick={() => void events.refetch()}
            >
              تازه‌سازی
            </Button>
          </div>
          <div className="event-table">
            <div className="table-row table-head">
              <span>رخداد</span>
              <span>دوربین / ROI</span>
              <span>Trigger</span>
              <span>زمان</span>
              <span />
            </div>
            {previewList.map((event) => (
              <button
                className={`table-row ${selected === event.eventId ? "selected" : ""}`}
                key={event.eventId}
                onClick={() => setSelected(event.eventId)}
              >
                <span className="event-cell">
                  <i
                    className={`event-kind-dot ${event.scenario.toLowerCase().includes("face") ? "face" : "plate"}`}
                  />
                  <div>
                    <b>{eventTitle(event)}</b>
                    <small>
                      #{event.sequence} · {event.scenario}
                    </small>
                  </div>
                </span>
                <span>
                  {s(event.source.cameraName, s(event.source.cameraId, "—"))}
                  <small className="block-muted">
                    {s(event.source.roiName, "")}
                  </small>
                </span>
                <span>
                  {Boolean(event.trigger.matched) ? (
                    <Badge tone="green">matched</Badge>
                  ) : (
                    <Badge>stored</Badge>
                  )}
                </span>
                <span>{fmtDate(event.occurredAtUtc)}</span>
                <ChevronLeft size={15} />
              </button>
            ))}
            {!previewList.length && (
              <Empty
                icon={Activity}
                title={deleteMode === "all" ? "رخدادی وجود ندارد" : "در این بازه رخدادی وجود ندارد"}
                text={deleteMode === "all" ? "پس از فعال‌شدن دوربین و taskها اینجا پر می‌شود." : "با این انتخاب، موردی برای حذف در گرید دیده نمی‌شود."}
              />
            )}
          </div>
        </section>
      </div>
    </>
  );
}
function EventPreview({ id }: { id: string }) {
  const event = useEvent(id);
  const [payloadOpen, setPayloadOpen] = useState(false);
  if (event.isLoading)
    return (
      <div className="panel">
        <Loading />
      </div>
    );
  if (!event.data) return <ErrorBox />;
  const item = event.data;
  return (
    <div className="panel event-preview">
      <div className="panel-head compact">
        <div>
          <h3>{eventTitle(item)}</h3>
          <span>
            Sequence #{item.sequence} · {fmtDate(item.occurredAtUtc)}
          </span>
        </div>
        <Badge tone={Boolean(item.trigger.matched) ? "green" : "blue"}>
          {item.scenario}
        </Badge>
      </div>
      <div className="event-record-meta">
        <span><b>رخداد</b>{item.eventType}</span>
        <span><b>دوربین</b>{s(item.source.cameraName, s(item.source.cameraId, "—"))}</span>
        <span><b>ROI</b>{s(item.source.roiName, "—")}</span>
        <span><b>Sequence</b>#{item.sequence}</span>
        <span><b>زمان</b>{fmtDate(item.occurredAtUtc)}</span>
        <span><b>Trigger</b>{Boolean(item.trigger.matched) ? "matched" : "stored"}</span>
        {Object.entries(item.components).map(([key, value]) => (
          <span key={key}>
            <b>{key}</b>
            {s(value.label, s(value.plateText, "جزئیات در payload"))}
            <small>confidence {fmt(value.confidence, 3)}</small>
          </span>
        ))}
      </div>
      <div className="artifact-grid">
        {item.artifacts.map((artifact) => (
          <a
            className="artifact"
            key={artifact.artifactId}
            href={serviceUrl(artifact.downloadUrl)}
            target="_blank"
          >
            <img src={serviceUrl(artifact.downloadUrl)} alt={artifact.type} />
            <span>
              {artifact.type} · {Math.round(artifact.sizeBytes / 1024)} KB
            </span>
          </a>
        ))}
      </div>
      <div className="event-payload-actions">
        <Button
          variant="soft"
          icon={FileJson}
          aria-expanded={payloadOpen}
          onClick={() => setPayloadOpen((open) => !open)}
        >
          {payloadOpen ? "بستن payload" : "نمایش payload"}
        </Button>
      </div>
      {payloadOpen && (
        <div className="json-block">
          <div className="json-title">
            <FileJson size={15} /> payload کامل تریگر و components
          </div>
          <pre>{JSON.stringify(item, null, 2)}</pre>
        </div>
      )}
    </div>
  );
}
function EventDetail() {
  const { eventId } = useParams();
  return (
    <>
      <PageHead
        title="جزئیات رخداد"
        description="فریم، کراپ‌ها، مشخصات جزئی تشخیص و payload کامل"
      />
      <EventPreview id={eventId ?? ""} />
    </>
  );
}

function Triggers() {
  const query = useTriggers();
  const cameras = useCameras();
  const people = usePeople();
  const mutation = useTriggerMutation();
  const client = useQueryClient();
  const [selected, setSelected] = useState<TriggerDefinition>();
  const blank = (): TriggerDefinition => ({
    id: newId(),
    name: "تریگر جدید",
    enabled: true,
    cameraIds: [],
    taskIds: [],
    kinds: ["PlateRecognition"],
    cooldownSeconds: 0,
    actions: [{ type: "LiveEvent", enabled: true }],
  });
  const remove = useMutation({
    mutationFn: api.deleteTrigger,
    onSuccess: () => {
      setSelected(undefined);
      void client.invalidateQueries({ queryKey: keys.triggers });
    },
  });
  return (
    <>
      <PageHead
        title="تریگرها و کلاینت‌ها"
        description="شرط‌ها داخل سرویس ارزیابی می‌شوند؛ قطع UI باعث از دست رفتن رخداد بین دو اتصال نمی‌شود."
        action={
          <Button icon={Plus} onClick={() => setSelected(blank())}>
            تریگر جدید
          </Button>
        }
      />
      <ClientSubscriptionTester cameras={cameras.data ?? []} />
      <div className="triggers-layout">
        <section className="panel trigger-list">
          <div className="panel-head compact">
            <div>
              <h3>تعریف‌های سرویس</h3>
              <span>{query.data?.items.length ?? 0} تریگر</span>
            </div>
          </div>
          {query.data?.items.map((trigger) => (
            <button
              className={`trigger-row ${selected?.id === trigger.id ? "selected" : ""}`}
              key={trigger.id}
              onClick={() => setSelected(clone(trigger))}
            >
              <div className={`trigger-icon ${trigger.enabled ? "on" : ""}`}>
                <BellRing size={17} />
              </div>
              <div>
                <b>{trigger.name}</b>
                <span>
                  {trigger.kinds.join(" / ")} · cooldown{" "}
                  {trigger.cooldownSeconds}s
                </span>
              </div>
              <span
                className={`status-dot ${trigger.enabled ? "online" : "muted"}`}
              />
            </button>
          ))}
          {!query.data?.items.length && (
            <Empty
              icon={BellRing}
              title="تریگری تعریف نشده"
              text="برای ارسال LiveEvent یا webhook یک تریگر بسازید."
            />
          )}
        </section>
        <section className="trigger-editor">
          {selected ? (
            <TriggerEditor
              trigger={selected}
              cameras={cameras.data ?? []}
              people={people.data ?? []}
              exists={
                query.data?.items.some((item) => item.id === selected.id) ??
                false
              }
              onSave={(trigger) =>
                mutation.mutate({
                  mode: query.data?.items.some((item) => item.id === trigger.id)
                    ? "update"
                    : "create",
                  trigger,
                })
              }
              onDelete={() => remove.mutate(selected.id)}
            />
          ) : (
            <div className="panel editor-placeholder">
              <BellRing size={35} />
              <b>یک تریگر را انتخاب کنید</b>
              <span>
                LiveEvent، Webhook و کلاینت‌های مجاز از اینجا تنظیم می‌شوند.
              </span>
            </div>
          )}
        </section>
      </div>
    </>
  );
}

function ClientSubscriptionTester({ cameras }: { cameras: CameraStatus[] }) {
  const [draft, setDraft] = useState<ClientSubscription>(() => readClientSubscription());
  const update = <K extends keyof ClientSubscription>(key: K, value: ClientSubscription[K]) =>
    setDraft((current) => ({ ...current, [key]: value }));
  const toggleCamera = (id: string) =>
    update(
      "cameraIds",
      draft.cameraIds.includes(id)
        ? draft.cameraIds.filter((item) => item !== id)
        : [...draft.cameraIds, id],
    );
  const apply = () => saveClientSubscription(draft);
  return (
    <section className="panel client-subscription-panel">
      <div className="panel-head compact">
        <div>
          <h3>آزمایش subscription کلاینت</h3>
          <span>
            این تنظیم فقط eventهای همین اتصال UI را فیلتر می‌کند و تنظیمات دوربین را تغییر نمی‌دهد.
          </span>
        </div>
        <Button icon={Radio} onClick={apply}>اعمال برای اتصال جاری</Button>
      </div>
      <div className="form-grid">
        <Field label="رخداد پایه">
          <select value={draft.mode} onChange={(e) => update("mode", e.target.value as ClientSubscription["mode"])}>
            <option value="All">همهٔ رخدادها</option>
            <option value="Plate">پلاک‌محور</option>
            <option value="KnownFace">چهرهٔ شناخته‌شده</option>
          </select>
        </Field>
        <Field label="پنجرهٔ association (ms)">
          <input type="number" min="0" max="10000" step="100" value={draft.windowMs} onChange={(e) => update("windowMs", Number(e.target.value))} />
        </Field>
        <Field label="چهره الزامی باشد">
          <Toggle checked={draft.faceRequired} onChange={(value) => update("faceRequired", value)} />
        </Field>
        <Field label="پلاک الزامی باشد">
          <Toggle checked={draft.plateRequired} onChange={(value) => update("plateRequired", value)} />
        </Field>
        <Field label="چهرهٔ ناشناس هم ارسال شود">
          <Toggle checked={draft.includeUnknownFace} onChange={(value) => update("includeUnknownFace", value)} />
        </Field>
      </div>
      <div className="trigger-scope">
        <b>محدودکردن subscription به دوربین‌ها</b>
        <div>
          {cameras.map((camera) => (
            <label key={camera.id} className="scope-chip">
              <input type="checkbox" checked={draft.cameraIds.includes(camera.id)} onChange={() => toggleCamera(camera.id)} />
              <span>{camera.name}</span>
            </label>
          ))}
        </div>
        <small>خالی‌بودن یعنی همهٔ دوربین‌ها. تغییرات بعد از اعمال، روی SignalR همین صفحه فعال می‌شود.</small>
      </div>
    </section>
  );
}
function TriggerEditor({
  trigger,
  cameras,
  people,
  exists,
  onSave,
  onDelete,
}: {
  trigger: TriggerDefinition;
  cameras: CameraStatus[];
  people: FaceIdentity[];
  exists: boolean;
  onSave: (trigger: TriggerDefinition) => void;
  onDelete: () => void;
}) {
  const [draft, setDraft] = useState(trigger);
  useEffect(() => setDraft(trigger), [trigger]);
  const update = (key: keyof TriggerDefinition, value: unknown) =>
    setDraft({ ...draft, [key]: value });
  const toggleCamera = (id: string) =>
    update(
      "cameraIds",
      draft.cameraIds.includes(id)
        ? draft.cameraIds.filter((item) => item !== id)
        : [...draft.cameraIds, id],
    );
  const addAction = () =>
    update("actions", [
      ...draft.actions,
      { type: "Webhook", target: "", enabled: true },
    ]);
  return (
    <section className="panel trigger-form">
      <div className="panel-head">
        <div>
          <h3>تنظیم تریگر</h3>
          <span>
            {exists ? "ویرایش تعریف موجود" : "تعریف جدید"} · همهٔ فیلترها در
            runtime سرویس اعمال می‌شوند.
          </span>
        </div>
        <Toggle
          checked={draft.enabled}
          onChange={(value) => update("enabled", value)}
        />
      </div>
      <div className="form-grid">
        <Field label="نام تریگر" wide>
          <input
            value={draft.name}
            onChange={(e) => update("name", e.target.value)}
          />
        </Field>
        <Field label="سناریو">
          <select
            value={draft.kinds[0] ?? "PlateRecognition"}
            onChange={(e) => update("kinds", [e.target.value])}
          >
            <option>PlateRecognition</option>
            <option>FaceRecognition</option>
            <option>PlateFaceMatch</option>
          </select>
        </Field>
        <Field
          label="History event cooldown (sec)"
          hint="0 یعنی بدون محدودیت تاریخچه؛ کلید بر اساس سناریوی همین تریگر ساخته می‌شود"
        >
          <input
            type="number"
            min="0"
            value={draft.cooldownSeconds}
            onChange={(e) => update("cooldownSeconds", Number(e.target.value))}
          />
        </Field>
        <Field label="Label equals">
          <input
            value={draft.labelEquals ?? ""}
            onChange={(e) => update("labelEquals", e.target.value || undefined)}
            placeholder="اختیاری"
          />
        </Field>
        <Field label="Plate text equals">
          <input
            value={draft.plateTextEquals ?? ""}
            onChange={(e) =>
              update("plateTextEquals", e.target.value || undefined)
            }
            placeholder="اختیاری"
          />
        </Field>
        <Field label="Minimum confidence">
          <input
            type="number"
            min="0"
            max="1"
            step=".01"
            value={draft.minimumConfidence ?? ""}
            onChange={(e) =>
              update(
                "minimumConfidence",
                e.target.value ? Number(e.target.value) : undefined,
              )
            }
          />
        </Field>
        <Field label="Face identity">
          <select
            value={draft.identityId ?? ""}
            onChange={(e) => update("identityId", e.target.value || undefined)}
          >
            <option value="">همهٔ افراد</option>
            {people
              .filter((item) => !item.isUnknown)
              .map((person) => (
                <option key={person.id} value={person.id}>
                  #{person.personNumber} {person.name}
                </option>
              ))}
          </select>
        </Field>
      </div>
      <div className="trigger-scope">
        <b>محدودکردن به دوربین‌ها</b>
        <div>
          {cameras.map((camera) => (
            <label key={camera.id} className="scope-chip">
              <input
                type="checkbox"
                checked={draft.cameraIds.includes(camera.id)}
                onChange={() => toggleCamera(camera.id)}
              />
              <span>{camera.name}</span>
            </label>
          ))}
        </div>
        <small>خالی‌بودن یعنی همهٔ دوربین‌ها</small>
      </div>
      <div className="action-box actions-editor">
        <div>
          <b>کانال‌های اعلان</b>
          <span>
            کلاینت‌ها با SignalR replay می‌شوند؛ Webhook برای سیستم‌های بیرونی
            است.
          </span>
        </div>
        {draft.actions.map((action, index) => (
          <div className="action-row" key={index}>
            <select
              value={action.type}
              onChange={(e) => {
                const actions = [...draft.actions];
                actions[index] = { ...action, type: e.target.value };
                update("actions", actions);
              }}
            >
              <option>LiveEvent</option>
              <option>Webhook</option>
              <option>WindowsEvent</option>
            </select>
            <input
              dir="ltr"
              placeholder="target / URL"
              value={action.target ?? ""}
              onChange={(e) => {
                const actions = [...draft.actions];
                actions[index] = { ...action, target: e.target.value };
                update("actions", actions);
              }}
            />
            <Toggle
              checked={action.enabled}
              onChange={(value) => {
                const actions = [...draft.actions];
                actions[index] = { ...action, enabled: value };
                update("actions", actions);
              }}
            />
            <button
              className="icon-button danger-icon"
              onClick={() =>
                update(
                  "actions",
                  draft.actions.filter((_, i) => i !== index),
                )
              }
            >
              <Trash2 size={14} />
            </button>
          </div>
        ))}
        <Button variant="soft" icon={Plus} onClick={addAction}>
          کانال جدید
        </Button>
      </div>
      <div className="form-actions">
        <Button variant="danger" icon={Trash2} onClick={onDelete}>
          حذف تریگر
        </Button>
        <Button icon={Save} onClick={() => onSave(draft)}>
          ذخیره تریگر
        </Button>
      </div>
    </section>
  );
}

function SettingsPage() {
  const query = useSettings();
  const caps = useCapabilities();
  const models = useModels();
  const mutation = useMutation({
    mutationFn: (body: {
      revision: number;
      detection?: SettingsResponse["detection"];
      service?: ServiceSettings;
    }) => api.saveSettings(body),
    onSuccess: () => void query.refetch(),
  });
  const reload = useMutation({ mutationFn: api.reload });
  const [draft, setDraft] = useState<{
    revision: number;
    detection: SettingsResponse["detection"];
    service: ServiceSettings;
  }>();
  const [tab, setTab] = useState<"runtime" | "security" | "capabilities">(
    "runtime",
  );
  useEffect(() => {
    if (query.data)
      setDraft({
        revision: query.data.revision,
        detection: clone(query.data.detection),
        service: clone(query.data.service),
      });
  }, [query.data]);
  if (query.isLoading || !draft) return <Loading />;
  const service = draft.service;
  const setService = (next: ServiceSettings) =>
    setDraft({ ...draft, service: next });
  return (
    <>
      <PageHead
        title="تنظیمات سرویس"
        description="تنظیمات runtime، نگهداری، امنیت، مدل‌ها و reload مدیریت سرویس."
        action={
          <div className="title-actions">
            <Button
              variant="soft"
              icon={RefreshCw}
              onClick={() => reload.mutate()}
            >
              Reload runtime
            </Button>
            <Button
              icon={Save}
              disabled={mutation.isPending}
              onClick={() =>
                mutation.mutate({
                  revision: draft.revision,
                  detection: draft.detection,
                  service,
                })
              }
            >
              ذخیره همهٔ تنظیمات
            </Button>
          </div>
        }
      />
      <div className="tab-bar settings-tabs">
        <button
          className={tab === "runtime" ? "active" : ""}
          onClick={() => setTab("runtime")}
        >
          <Gauge size={16} />
          Runtime و retention
        </button>
        <button
          className={tab === "security" ? "active" : ""}
          onClick={() => setTab("security")}
        >
          <ShieldCheck size={16} />
          HTTP و امنیت
        </button>
        <button
          className={tab === "capabilities" ? "active" : ""}
          onClick={() => setTab("capabilities")}
        >
          <CircleGauge size={16} />
          قابلیت و مدل
        </button>
      </div>
      {tab === "runtime" && (
        <div className="settings-page-grid">
          <section className="panel">
            <div className="panel-head">
              <div>
                <h3>Runtime</h3>
                <span>Revision فعلی: {draft.revision}</span>
              </div>
              <Badge tone="blue">API managed</Badge>
            </div>
            <div className="settings-sections">
              <SettingLine
                label="شروع خودکار دوربین‌ها"
                text="پس از شروع سرویس دوربین‌های تنظیم‌شده اجرا شوند."
                control={
                  <Toggle
                    checked={service.runtime.autoStartCameras}
                    onChange={(value) =>
                      setService({
                        ...service,
                        runtime: {
                          ...service.runtime,
                          autoStartCameras: value,
                        },
                      })
                    }
                  />
                }
              />
              <SettingLine
                label="Preview FPS"
                text="سقف rendering مستقل از inference."
                control={
                  <input
                    className="small-input"
                    type="number"
                    value={service.runtime.previewFps}
                    onChange={(e) =>
                      setService({
                        ...service,
                        runtime: {
                          ...service.runtime,
                          previewFps: Number(e.target.value),
                        },
                      })
                    }
                  />
                }
              />
              <SettingLine
                label="Max event queue"
                text="ظرفیت صف رخداد پایدار قبل از اعمال backpressure."
                control={
                  <input
                    className="small-input"
                    type="number"
                    value={service.runtime.maxEventQueueLength}
                    onChange={(e) =>
                      setService({
                        ...service,
                        runtime: {
                          ...service.runtime,
                          maxEventQueueLength: Number(e.target.value),
                        },
                      })
                    }
                  />
                }
              />
              <SettingLine
                label="Max association window (ms)"
                text="حداکثر بازهٔ اتصال پلاک و چهره در event مشترک."
                control={
                  <input
                    className="small-input"
                    type="number"
                    min="0"
                    max="10000"
                    value={service.association.maxWindowMs}
                    onChange={(e) =>
                      setService({
                        ...service,
                        association: {
                          ...service.association,
                          maxWindowMs: Number(e.target.value),
                        },
                      })
                    }
                  />
                }
              />
              <SettingLine
                label="Association فقط داخل همان ROI"
                text="از اتصال پلاک و چهرهٔ دو ROI متفاوت جلوگیری شود."
                control={
                  <Toggle
                    checked={service.association.requireSameRoi}
                    onChange={(value) =>
                      setService({
                        ...service,
                        association: {
                          ...service.association,
                          requireSameRoi: value,
                        },
                      })
                    }
                  />
                }
              />
            </div>
          </section>
          <section className="panel">
            <div className="panel-head">
              <div>
                <h3>Retention</h3>
                <span>پاک‌سازی دوره‌ای metadata و evidence</span>
              </div>
              <Archive size={18} />
            </div>
            <div className="settings-sections">
              <SettingLine
                label="Event retention"
                text="مدت نگهداری رخدادها"
                control={
                  <input
                    className="small-input"
                    type="number"
                    value={service.retention.eventDays}
                    onChange={(e) =>
                      setService({
                        ...service,
                        retention: {
                          ...service.retention,
                          eventDays: Number(e.target.value),
                        },
                      })
                    }
                  />
                }
              />
              <SettingLine
                label="Artifact retention"
                text="مدت نگهداری frame/cropها"
                control={
                  <input
                    className="small-input"
                    type="number"
                    value={service.retention.artifactDays}
                    onChange={(e) =>
                      setService({
                        ...service,
                        retention: {
                          ...service.retention,
                          artifactDays: Number(e.target.value),
                        },
                      })
                    }
                  />
                }
              />
              <SettingLine
                label="Webhook retry"
                text="مدت retry کانال‌های بیرونی"
                control={
                  <input
                    className="small-input"
                    type="number"
                    value={service.retention.webhookRetryDays}
                    onChange={(e) =>
                      setService({
                        ...service,
                        retention: {
                          ...service.retention,
                          webhookRetryDays: Number(e.target.value),
                        },
                      })
                    }
                  />
                }
              />
            </div>
          </section>
        </div>
      )}
      {tab === "security" && (
        <div className="settings-page-grid">
          <section className="panel">
            <div className="panel-head">
              <div>
                <h3>HTTP listener</h3>
                <span>آدرس‌هایی که سرویس روی آن‌ها listen می‌کند</span>
              </div>
              <Wifi size={18} />
            </div>
            <div className="settings-sections">
              <div className="setting-line">
                <div>
                  <b>Listen URLs</b>
                  <span>هر آدرس در یک خط</span>
                </div>
                <textarea
                  className="url-box"
                  value={service.http.listenUrls.join("\n")}
                  onChange={(e) =>
                    setService({
                      ...service,
                      http: {
                        ...service.http,
                        listenUrls: e.target.value
                          .split(/\r?\n/)
                          .filter(Boolean),
                      },
                    })
                  }
                />
              </div>
              <div className="setting-line">
                <div>
                  <b>CORS origins</b>
                  <span>هر origin UI جداگانه در یک خط؛ برای SignalR هم استفاده می‌شود. تغییر پس از restart سرویس اعمال می‌شود.</span>
                </div>
                <textarea
                  className="url-box"
                  value={service.http.corsOrigins.join("\n")}
                  onChange={(e) =>
                    setService({
                      ...service,
                      http: {
                        ...service.http,
                        corsOrigins: e.target.value.split(/\r?\n/).filter(Boolean),
                      },
                    })
                  }
                />
              </div>
            </div>
          </section>
          <section className="panel">
            <div className="panel-head">
              <div>
                <h3>API security</h3>
                <span>کلید برای کلاینت‌های remote</span>
              </div>
              <ShieldCheck size={18} />
            </div>
            <div className="settings-sections">
              <div className="setting-line">
                <div>
                  <b>Allow loopback without API key</b>
                  <span>برای توسعه روی 127.0.0.1</span>
                </div>
                <Toggle
                  checked={service.security.allowLoopbackWithoutApiKey}
                  onChange={(value) =>
                    setService({
                      ...service,
                      security: {
                        ...service.security,
                        allowLoopbackWithoutApiKey: value,
                      },
                    })
                  }
                />
              </div>
              <div className="setting-line">
                <div>
                  <b>API key</b>
                  <span dir="ltr">X-Hsh-Api-Key</span>
                </div>
                <input
                  className="api-key-box"
                  dir="ltr"
                  value={service.security.apiKey}
                  onChange={(e) =>
                    setService({
                      ...service,
                      security: { ...service.security, apiKey: e.target.value },
                    })
                  }
                />
              </div>
            </div>
          </section>
        </div>
      )}
      {tab === "capabilities" && (
        <div className="settings-page-grid">
          <section className="panel">
            <div className="panel-head">
              <div>
                <h3>Processing modules</h3>
                <span>Registry مرکزی موتور تشخیص</span>
              </div>
            </div>
            <div className="capability-list">
              {caps.data?.map((cap) => (
                <div className="capability" key={cap.type}>
                  <div className="capability-icon">
                    {cap.kind === "Face" ? (
                      <UserRound size={17} />
                    ) : (
                      <Radio size={17} />
                    )}
                  </div>
                  <div>
                    <b>{cap.displayName}</b>
                    <span>
                      {cap.type} · {cap.optionsType?.split(".").pop()}
                    </span>
                    {cap.availabilityMessage && (
                      <small className="block-muted">
                        {cap.availabilityMessage}
                      </small>
                    )}
                  </div>
                  <Badge tone={cap.available === false ? "red" : "green"}>
                    {cap.available === false ? "unavailable" : "available"}
                  </Badge>
                </div>
              ))}
            </div>
          </section>
          <section className="panel">
            <div className="panel-head">
              <div>
                <h3>Model inventory</h3>
                <span>فقط packageهای قابل استفادهٔ runtime</span>
              </div>
              <Download size={18} />
            </div>
            <div className="model-list inventory">
              {models.data?.map((model) => (
                <div key={model.relativePath}>
                  <b>{model.name}</b>
                  <span>
                    {model.module} ·{" "}
                    {model.packaged ? "HSH package" : "legacy ONNX"}
                  </span>
                </div>
              ))}
              {!models.data?.length && (
                <Empty
                  icon={Database}
                  title="مدلی پیدا نشد"
                  text="مدل‌ها را کنار executable سرویس قرار دهید."
                />
              )}
            </div>
          </section>
        </div>
      )}
    </>
  );
}
function SettingLine({
  label,
  text,
  control,
}: {
  label: string;
  text: string;
  control: React.ReactNode;
}) {
  return (
    <div className="setting-line">
      <div>
        <b>{label}</b>
        <span>{text}</span>
      </div>
      {control}
    </div>
  );
}

export default App;
