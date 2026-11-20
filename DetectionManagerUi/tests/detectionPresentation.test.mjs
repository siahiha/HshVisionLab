import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import ts from "typescript";

const source = await readFile(new URL("../src/detectionPresentation.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } });
const { detectionSummaries, detectionPreviews, detectionPercent, detectionTimestamp, compactDetectionMetric } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString("base64")}`);
const event = (components, artifacts = []) => ({ components, artifacts, source: {}, trigger: {}, occurredAtUtc: "2026-11-20T11:00:01Z" });
const artifact = (type, id = type) => ({ type, artifactId: id, downloadUrl: `/artifacts/${id}` });

test("plate confidence, OCR confidence and supplementary color stay distinct", () => {
  const input = event({ plate: { plateText: "۱۲ب۳۴۵۶۷", confidence: .86, threshold: .55, trackId: 0,
    isValidIranianPlate: true, recognitionSource: "OcrFallback", recognitionConfidence: .63,
    vehicleColor: { status: "Accepted", color: "Gold", score: .12 } } });
  const original = JSON.stringify(input);
  const [plate] = detectionSummaries(input);
  assert.equal(plate.value, "۱۲ب۳۴۵۶۷");
  assert.equal(plate.trackId, "0");
  assert.deepEqual(plate.metrics, [
    { label: "اطمینان تشخیص", value: "86%" }, { label: "حداقل تشخیص", value: "55%" },
    { label: "اطمینان OCR", value: "63%" }, { label: "خواندن پلاک", value: "OCR جایگزین" },
  ]);
  assert.equal(plate.color.value, "طلایی");
  assert.equal(plate.color.score, "12%");
  assert.equal(plate.tone, "accepted");
  assert.equal(JSON.stringify(input), original);
});

test("combined-model characters are not labelled OCR", () => {
  const [plate] = detectionSummaries(event({ plate: { confidence: .8, recognitionSource: "Detector", recognitionConfidence: .9 } }));
  assert.ok(plate.metrics.some(m => m.label === "اطمینان حروف" && m.value === "90%"));
  assert.ok(!plate.metrics.some(m => m.label === "اطمینان OCR"));
});

test("historical records do not invent missing recognition scores", () => {
  const [plate] = detectionSummaries(event({ plate: { label: "Plate", confidence: .75 } }));
  assert.deepEqual(plate.metrics, [{ label: "اطمینان تشخیص", value: "75%" }]);
  assert.equal(plate.color, undefined);
  assert.equal(plate.trackId, undefined);
});

test("recognized people use their name and their own similarity", () => {
  const [face] = detectionSummaries(event({ face: { confidence: .92, trackId: 7, recognitionStatus: "Matched",
    recognition: { name: "شخص نمونه", similarity: .68, personNumber: 12 } } }));
  assert.equal(face.value, "شخص نمونه");
  assert.equal(face.status, "شناسایی‌شده");
  assert.ok(face.metrics.some(m => m.label === "شباهت شناسایی" && m.value === "68%"));
  assert.ok(face.metrics.some(m => m.label === "شماره شخص" && m.value === "12"));
});

test("unknown and detector-only faces/palms have different statuses", () => {
  const result = detectionSummaries(event({
    face: { trackId: 3, recognitionStatus: "NotAttempted", recognition: { isUnknown: true } },
    palm: { trackId: 4, recognitionStatus: "Unknown", recognition: { isUnknown: true } },
  }));
  assert.equal(result[0].value, "چهره");
  assert.equal(result[0].status, "بدون شناسایی");
  assert.equal(result[1].value, "کف دست ناشناس");
  assert.equal(result[1].trackId, "4");
});

test("an unmatched vehicle cannot appear as an accepted color", () => {
  const [plate] = detectionSummaries(event({ plate: { vehicleColor: { color: "Red", status: "Unknown", score: .99, reason: "VehicleNotFoundOrAmbiguous" } } }));
  assert.equal(plate.color.value, "نامشخص");
  assert.equal(plate.color.score, undefined);
  assert.equal(plate.color.swatch, undefined);
});

test("associated events show each component crop once", () => {
  const previews = detectionPreviews(event({ plate: {}, face: {}, palm: {} },
    [artifact("PlateCrop"), artifact("DetectionCrop"), artifact("FaceAlignedCrop"), artifact("PalmCrop")]));
  assert.deepEqual(previews.map(p => [p.kind, p.artifact.type]), [["plate", "PlateCrop"], ["face", "FaceAlignedCrop"], ["palm", "PalmCrop"]]);
});

test("a missing plate crop never borrows the face crop", () => {
  const previews = detectionPreviews(event({ plate: {}, face: {} }, [artifact("DetectionCrop")]));
  assert.deepEqual(previews.map(p => p.kind), ["face"]);
});

test("artifact-free records stay visible without a fabricated image", () => {
  assert.deepEqual(detectionPreviews(event({ plate: {} })), []);
});

test("percentages and invalid timestamps are formatted safely", () => {
  assert.equal(detectionPercent(1), "100%");
  assert.equal(detectionPercent(0), "0%");
  assert.equal(detectionPercent(.55), "55%");
  assert.equal(detectionPercent(85), "85%");
  assert.equal(detectionPercent(undefined), "—");
  assert.equal(detectionPercent(NaN), "—");
  assert.equal(detectionTimestamp("bad date"), "—");
});

test("compact plate row shows only the reading score, never the box score", () => {
  const [plate] = detectionSummaries(event({ plate: { confidence: .95, recognitionSource: "Ocr", recognitionConfidence: .63 } }));
  assert.deepEqual(compactDetectionMetric(plate), { label: "اطمینان OCR", value: "63%" });
  const [missing] = detectionSummaries(event({ plate: { confidence: .95 } }));
  assert.equal(compactDetectionMetric(missing).value, "—");
});

test("combined reading score retains its source label", () => {
  const [plate] = detectionSummaries(event({ plate: { confidence: .95, recognitionSource: "Detector", recognitionConfidence: .82 } }));
  assert.deepEqual(compactDetectionMetric(plate), { label: "اطمینان حروف", value: "82%" });
});

test("associated face and palm rows use their own recognition scores", () => {
  const summaries = detectionSummaries(event({ plate: { confidence: .95, recognitionSource: "Ocr", recognitionConfidence: .63 },
    face: { confidence: .99, recognitionStatus: "Matched", recognition: { name: "شخص نمونه", similarity: .72 } },
    palm: { confidence: .91, recognitionStatus: "Unknown", recognition: { isUnknown: true } } }));
  assert.deepEqual(summaries.map(summary => compactDetectionMetric(summary).value), ["63%", "72%", "91%"]);
});
