import type { Artifact, DetectionEvent } from "./types";

export type DetectionKind = "plate" | "face" | "palm";
export interface DetectionMetric { label: string; value: string }
export interface DetectionSummary {
  kind: DetectionKind;
  label: string;
  value: string;
  trackId?: string;
  status: string;
  tone: "accepted" | "rejected" | "neutral";
  metrics: DetectionMetric[];
  color?: { value: string; score?: string; swatch?: string; reason?: string };
}

export function compactDetectionMetric(summary: DetectionSummary): DetectionMetric {
  if (summary.kind === "plate") return summary.metrics.find(metric =>
    metric.label === "اطمینان OCR" || metric.label === "اطمینان حروف") ?? { label: "اطمینان OCR", value: "—" };
  return summary.metrics.find(metric => metric.label === "شباهت شناسایی") ?? summary.metrics[0];
}

const record = (value: unknown): Record<string, unknown> =>
  value !== null && typeof value === "object" ? value as Record<string, unknown> : {};
const text = (value: unknown): string => typeof value === "string" ? value.trim() : "";
const number = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);

export function detectionPercent(value: unknown): string {
  if (!number(value) || value < 0 || value > 100) return "—";
  return `${Math.round(value <= 1 ? value * 100 : value)}%`;
}

const dateTime = new Intl.DateTimeFormat("fa-IR", { dateStyle: "short", timeStyle: "medium" });
export function detectionTimestamp(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : dateTime.format(date);
}

const colors: Record<string, [string, string]> = {
  White: ["سفید", "#f3f4f6"], SilverGray: ["نقره‌ای / خاکستری", "#a1a8b0"],
  Yellow: ["زرد", "#eecb38"], Red: ["قرمز", "#e75454"], Green: ["سبز", "#42a775"],
  Blue: ["آبی", "#4d8fda"], Black: ["مشکی", "#17191d"], Orange: ["نارنجی", "#ed9945"],
  Gold: ["طلایی", "#c9a64f"], Brown: ["قهوه‌ای", "#997354"],
};

export function detectionSummaries(event: DetectionEvent): DetectionSummary[] {
  const result: DetectionSummary[] = [];
  for (const kind of ["plate", "face", "palm"] as const) {
    const component = event.components[kind];
    if (!component) continue;
    const label = kind === "plate" ? "پلاک" : kind === "face" ? "چهره" : "کف دست";
    const item: DetectionSummary = {
      kind, label, value: text(component.label) || label,
      trackId: number(component.trackId) ? String(component.trackId) : text(component.trackId) || undefined,
      status: component.status === "Rejected" ? "ردشده" : component.status === "Accepted" ? "تشخیص داده‌شده" : "",
      tone: component.status === "Rejected" ? "rejected" : component.status === "Accepted" ? "accepted" : "neutral",
      metrics: [{ label: "اطمینان تشخیص", value: detectionPercent(component.confidence) }],
    };
    const threshold = component.threshold ?? (kind === "plate" ? component.plateThreshold : undefined);
    if (number(threshold)) item.metrics.push({ label: "حداقل تشخیص", value: detectionPercent(threshold) });
    if (kind === "plate") {
      item.value = text(component.plateText) || item.value;
      if (component.isValidIranianPlate === true) { item.status = "پلاک معتبر"; item.tone = "accepted"; }
      else if (component.isValidIranianPlate === false) { item.status = "متن نامعتبر"; item.tone = "rejected"; }
      const source = text(component.recognitionSource);
      if (source || text(component.recognitionModel)) {
        if (number(component.recognitionConfidence)) item.metrics.push({
          label: source === "Detector" ? "اطمینان حروف" : "اطمینان OCR", value: detectionPercent(component.recognitionConfidence),
        });
        if (source) item.metrics.push({ label: "خواندن پلاک", value: source === "Detector" ? "مدل پلاک" : source === "OcrFallback" ? "OCR جایگزین" : source === "Ocr" ? "OCR" : source });
      }
      const color = record(component.vehicleColor);
      if (text(color.status)) {
        const accepted = color.status === "Accepted" && text(color.color) && color.color !== "Unknown";
        const known = colors[text(color.color)];
        item.color = {
          value: accepted ? known?.[0] || text(color.color) : color.status === "Pending" ? "در انتظار" : "نامشخص",
          score: accepted && number(color.score) ? detectionPercent(color.score) : undefined,
          swatch: accepted ? known?.[1] : undefined,
          reason: color.reason === "VehicleNotFoundOrAmbiguous" ? "خودروی متناظر پیدا نشد یا کادر آن مبهم بود"
            : color.status === "Failed" ? "تشخیص رنگ در دسترس نیست" : undefined,
        };
      }
    } else {
      const recognition = record(component.recognition);
      const status = text(component.recognitionStatus);
      if (status === "NotAttempted") { item.value = label; item.status = "بدون شناسایی"; }
      else if (status === "Unknown" || recognition.isUnknown === true) { item.value = `${label} ناشناس`; item.status = "ناشناس"; item.tone = "neutral"; }
      else if (status === "Matched" || text(recognition.name)) { item.value = text(recognition.name) || item.value; item.status = "شناسایی‌شده"; }
      if (number(recognition.similarity)) item.metrics.push({ label: "شباهت شناسایی", value: detectionPercent(recognition.similarity) });
      if (number(recognition.personNumber) && recognition.personNumber > 0) item.metrics.push({ label: "شماره شخص", value: String(recognition.personNumber) });
    }
    result.push(item);
  }
  return result;
}

export interface DetectionPreview { artifact: Artifact; label: string; kind?: DetectionKind }
export function detectionPreviews(event: DetectionEvent): DetectionPreview[] {
  const result: DetectionPreview[] = [];
  const used = new Set<string>();
  const add = (types: string[], label: string, kind?: DetectionKind) => {
    const artifact = types.map(type => event.artifacts.find(a => a.type.toLowerCase() === type && !used.has(a.artifactId))).find(Boolean);
    if (artifact) { result.push({ artifact, label, kind }); used.add(artifact.artifactId); }
  };
  // DetectionCrop is the service's unaligned face crop. Never substitute it
  // for a missing plate or palm in an associated event.
  if (event.components.plate) add(["platecrop"], "پلاک", "plate");
  if (event.components.face) add(["facealignedcrop", "detectioncrop"], "چهره", "face");
  if (event.components.palm) add(["palmcrop"], "کف دست", "palm");
  if (!result.length) add(["detectioncrop", "roiannotated", "fullframeraw", "associatedframeraw"], "تصویر تشخیص");
  return result;
}
