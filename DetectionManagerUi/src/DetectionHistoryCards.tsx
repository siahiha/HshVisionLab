import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Hand, ImageOff, UserRound } from "lucide-react";
import { serviceUrl } from "./api";
import { compactDetectionMetric, detectionPreviews, detectionSummaries, detectionTimestamp } from "./detectionPresentation";
import type { DetectionEvent } from "./types";

const text = (value: unknown, fallback = "") => typeof value === "string" && value.trim() ? value : fallback;
type Hovered = { event: DetectionEvent; anchor: DOMRect };

function DetectionHoverDetails({ hovered, id, onEnter, onLeave }: {
  hovered: Hovered; id: string; onEnter: () => void; onLeave: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState<{ left: number; top: number }>();
  useLayoutEffect(() => {
    const tooltip = ref.current;
    if (!tooltip) return;
    const { anchor } = hovered;
    const margin = 8, gap = 10;
    const width = tooltip.offsetWidth, height = tooltip.offsetHeight;
    let left: number, top: number;
    if (anchor.left >= width + margin + gap) { left = anchor.left - width - gap; top = anchor.top; }
    else if (innerWidth - anchor.right >= width + margin + gap) { left = anchor.right + gap; top = anchor.top; }
    else { left = anchor.left; top = anchor.top >= height + margin + gap ? anchor.top - height - gap : anchor.bottom + gap; }
    setPosition({ left: Math.max(margin, Math.min(left, innerWidth - width - margin)), top: Math.max(margin, Math.min(top, innerHeight - height - margin)) });
  }, [hovered]);
  const { event } = hovered;
  return createPortal(<div ref={ref} id={id} role="tooltip" className="detection-hover-details"
    style={{ left: position?.left ?? 0, top: position?.top ?? 0, visibility: position ? "visible" : "hidden" }}
    onMouseEnter={onEnter} onMouseLeave={onLeave}>
    <div className="detection-hover-source">
      <b>{text(event.source.cameraName, text(event.source.cameraId, "دوربین نامشخص"))}</b>
      {text(event.source.roiName) && <span>{text(event.source.roiName)}</span>}
      <time dateTime={event.occurredAtUtc}>{detectionTimestamp(event.occurredAtUtc)}</time>
      {text(event.source.taskName) && <span>{text(event.source.taskName)}</span>}
      {event.trigger.matched === true && <span className="detected-trigger">تریگر فعال</span>}
    </div>
    {detectionSummaries(event).map(detail => <section className={`detection-hover-component ${detail.kind}`} key={detail.kind}>
      <div className="detected-component-heading"><b>{detail.label}</b>{detail.trackId !== undefined && <span dir="ltr">Track #{detail.trackId}</span>}</div>
      <b className="detection-hover-value" dir={detail.kind === "plate" ? "ltr" : "auto"}>{detail.value}</b>
      {detail.status && <span className={`detected-component-status ${detail.tone}`}>{detail.status}</span>}
      <dl className="detected-component-metrics">{detail.metrics.map(metric => <div key={metric.label}><dt>{metric.label}</dt><dd dir="auto">{metric.value}</dd></div>)}</dl>
      {detail.color && <div className="detected-component-color"><span>رنگ خودرو</span><b>{detail.color.swatch && <i style={{ backgroundColor: detail.color.swatch }} aria-hidden="true" />}{detail.color.value}</b>{detail.color.score && <small dir="ltr">{detail.color.score}</small>}{detail.color.reason && <span className="detection-hover-reason">{detail.color.reason}</span>}</div>}
    </section>)}
  </div>, document.body);
}

export function DetectionHistoryCards({ events, onOpenEvent }: { events: DetectionEvent[]; onOpenEvent: (eventId: string) => void }) {
  const [hovered, setHovered] = useState<Hovered>();
  const closeTimer = useRef<ReturnType<typeof setTimeout>>();
  const tooltipId = useId();
  const cancelClose = () => { if (closeTimer.current !== undefined) clearTimeout(closeTimer.current); };
  const close = () => { cancelClose(); setHovered(undefined); };
  const scheduleClose = () => { cancelClose(); closeTimer.current = setTimeout(() => setHovered(undefined), 120); };
  const show = (event: DetectionEvent, anchor: HTMLElement) => { cancelClose(); setHovered({ event, anchor: anchor.getBoundingClientRect() }); };
  useEffect(() => () => { if (closeTimer.current !== undefined) clearTimeout(closeTimer.current); }, []);
  useEffect(() => {
    if (!hovered) return;
    const dismiss = () => setHovered(undefined);
    window.addEventListener("resize", dismiss);
    window.addEventListener("scroll", dismiss);
    return () => { window.removeEventListener("resize", dismiss); window.removeEventListener("scroll", dismiss); };
  }, [hovered]);
  useEffect(() => {
    if (hovered && !events.some(event => event.eventId === hovered.event.eventId)) close();
  }, [events]);
  return <>
    <div className="detected-events-list" onScroll={close}>
      {events.map(event => {
        const details = detectionSummaries(event), previews = detectionPreviews(event);
        const camera = text(event.source.cameraName, text(event.source.cameraId, "دوربین نامشخص"));
        return <button className="detected-event-card compact" key={event.eventId}
          onClick={() => { close(); onOpenEvent(event.eventId); }}
          onPointerEnter={e => { if (e.pointerType === "mouse") show(event, e.currentTarget); }} onPointerLeave={scheduleClose}
          onFocus={e => show(event, e.currentTarget)} onBlur={close}
          onKeyDown={e => { if (e.key === "Escape") close(); }}
          aria-describedby={hovered?.event.eventId === event.eventId ? tooltipId : undefined}
          aria-label={`${camera}، ${details.map(detail => `${detail.label}: ${detail.value}`).join("، ")}`}>
          <div className="detected-event-camera" title={camera}>{camera}</div>
          {details.map(detail => {
            const preview = previews.find(p => p.kind === detail.kind) ?? (details.length === 1 ? previews.find(p => !p.kind) : undefined);
            const metric = compactDetectionMetric(detail);
            return <div className={`detected-compact-component ${detail.kind}`} key={detail.kind}>
              <div className={`detected-compact-image ${detail.kind}`}>
                {preview ? <img src={serviceUrl(preview.artifact.downloadUrl)} alt={preview.label} loading="lazy" decoding="async" />
                  : detail.kind === "face" ? <UserRound size={22} /> : detail.kind === "palm" ? <Hand size={22} /> : <ImageOff size={22} />}
              </div>
              <div className="detected-compact-result">
                <span className="detected-compact-kind">{detail.label}</span>
                <b className="detected-compact-value" dir={detail.kind === "plate" ? "ltr" : "auto"}>{detail.value}</b>
                <span className="detected-compact-confidence"><span>{metric.label}</span><b dir="ltr">{metric.value}</b></span>
              </div>
            </div>;
          })}
          {!details.length && <span className="detected-event-no-details">جزئیات تشخیص موجود نیست</span>}
        </button>;
      })}
    </div>
    {hovered && <DetectionHoverDetails hovered={hovered} id={tooltipId} onEnter={cancelClose} onLeave={scheduleClose} />}
  </>;
}
