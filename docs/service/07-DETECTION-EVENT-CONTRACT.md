# قرارداد کامل رخداد تشخیص و Trigger

## 1. هدف قرارداد

رخداد Trigger باید یک بستهٔ کامل از نتیجه و شواهد تشخیص باشد؛ نه فقط یک `label` و `confidence`.

برای هر رخداد باید بتوانیم بدون دسترسی به state لحظه‌ای runtime بفهمیم:

- چه چیزی تشخیص داده شد؟
- در کدام فریم، دوربین، ROI و task تشخیص داده شد؟
- مختصات دقیق detection چه بوده است؟
- تصویر فریم و ROI مربوط به آن کجاست؟
- مدل و threshold چه بوده است؟
- اگر چهره بود، ناشناس بود یا به کدام شخص متصل شد؟
- اگر پلاک و چهره با هم بودند، ارتباط آن‌ها با چه نتیجه‌ای ارزیابی شد؟
- آیا نتیجه با رکورد قبلی مطابقت داشت یا جدید/متناقض بود؟

کامل بودن event به این معنی نیست که تصاویر به‌صورت Base64 داخل هر پیام live قرار بگیرند. تمام metadata باید در event باشد و تصاویر به‌صورت artifact پایدار، hashشده و قابل دریافت ارائه شوند.

## 2. سناریوهای رسمی

قرارداد باید از ابتدا این سه سناریو را پشتیبانی کند:

| Scenario | توضیح |
| --- | --- |
| `PlateOnly` | تشخیص پلاک بدون وابستگی به چهره |
| `FaceRecognition` | تشخیص چهره و در صورت امکان شناسایی از Face Database |
| `PlateFaceAssociation` | تشخیص هم‌زمان پلاک و چهره و مقایسهٔ ارتباط آن‌ها با رکورد قبلی |

سناریو فقط نوع رخداد را مشخص می‌کند. هر سناریو می‌تواند چند component تشخیص داشته باشد.

## 3. Envelope عمومی رخداد

```json
{
  "eventId": "event-guid",
  "sequence": 18420,
  "payloadVersion": 1,
  "eventType": "PlateFaceMatched",
  "scenario": "PlateFaceAssociation",
  "occurredAtUtc": "2026-09-20T10:20:30.125Z",
  "receivedAtUtc": "2026-09-20T10:20:30.180Z",
  "source": {
    "serviceNodeId": "node-01",
    "cameraId": "camera-entrance",
    "cameraName": "Entrance",
    "taskId": "task-plate-face",
    "taskName": "Plate and face matching",
    "roiId": "roi-gate",
    "roiName": "Main Gate",
    "sourceFrameSequence": 991238,
    "sourceFrameCapturedAtUtc": "2026-09-20T10:20:30.090Z",
    "frameWidth": 1920,
    "frameHeight": 1080
  },
  "trigger": {
    "triggerId": "trigger-entrance-match",
    "triggerName": "Known vehicle entry",
    "matched": true,
    "cooldownApplied": false
  },
  "components": {
    "plate": { },
    "face": { },
    "association": { }
  },
  "artifacts": [ ]
}
```

## 4. اطلاعات مشترک هر detection component

هر component حداقل این اطلاعات را دارد:

```json
{
  "componentId": "component-guid",
  "kind": "Face",
  "status": "Accepted",
  "label": "Ali Ahmadi",
  "confidence": 0.94,
  "threshold": 0.80,
  "trackId": 12,
  "bounds": {
    "x": 820,
    "y": 220,
    "width": 160,
    "height": 190,
    "xNormalized": 0.427,
    "yNormalized": 0.203,
    "widthNormalized": 0.083,
    "heightNormalized": 0.176
  },
  "roiLocalBounds": {
    "x": 120,
    "y": 80,
    "width": 160,
    "height": 190
  },
  "model": {
    "name": "face_yunet_2023mar.onnx",
    "capability": "Face",
    "version": "deployment-version",
    "preprocessing": "None"
  }
}
```

مختصات هم در فضای فریم اصلی و هم در فضای ROI ارائه می‌شوند تا کلاینت مجبور به محاسبهٔ مجدد نباشد.

## 5. سناریوی `PlateOnly`

جزئیات پلاک باید شامل موارد زیر باشد:

```json
{
  "plate": {
    "componentId": "plate-component",
    "kind": "Plate",
    "status": "Accepted",
    "plateText": "12الف34567",
    "rawText": "12الف34567",
    "normalizedText": "12الف34567",
    "isValidIranianPlate": true,
    "plateConfidence": 0.92,
    "plateThreshold": 0.35,
    "bounds": { },
    "trackId": 44,
    "model": { },
    "characters": [
      {
        "index": 0,
        "symbol": "1",
        "classId": 1,
        "confidence": 0.91,
        "bounds": { }
      },
      {
        "index": 1,
        "symbol": "2",
        "classId": 2,
        "confidence": 0.89,
        "bounds": { }
      }
    ],
    "ocr": {
      "ordering": "right-to-left",
      "characterCount": 8,
      "hasMissingCharacter": false,
      "validationMessage": null
    }
  }
}
```

برای هر character، `symbol`، `classId`، confidence و bounds باید قابل دسترسی باشد. اگر موتور در یک مرحله characterها را تولید نکند، `characters` خالی و `hasCharacterDetails=false` اعلام شود؛ دادهٔ جعلی تولید نشود.

## 6. سناریوی `FaceRecognition`

وضعیت چهره باید بین detection و recognition تفاوت بگذارد:

```text
DetectionStatus:
  Rejected / Accepted

RecognitionStatus:
  NotAttempted
  Matched
  Unknown
  NoFaceDatabase
  RecognitionFailed
```

نمونه:

```json
{
  "face": {
    "componentId": "face-component",
    "kind": "Face",
    "status": "Accepted",
    "recognitionStatus": "Matched",
    "label": "Ali Ahmadi",
    "confidence": 0.94,
    "detectionThreshold": 0.80,
    "recognition": {
      "personId": "person-guid",
      "personNumber": 17,
      "name": "Ali Ahmadi",
      "isUnknown": false,
      "similarity": 0.88,
      "minimumSimilarity": 0.40,
      "matchedSampleId": "sample-guid",
      "databaseRevision": 31
    },
    "trackId": 12,
    "bounds": { },
    "landmarks": [
      { "name": "leftEye", "x": 0, "y": 0 },
      { "name": "rightEye", "x": 0, "y": 0 },
      { "name": "nose", "x": 0, "y": 0 },
      { "name": "leftMouth", "x": 0, "y": 0 },
      { "name": "rightMouth", "x": 0, "y": 0 }
    ],
    "model": { }
  }
}
```

برای چهرهٔ ناشناس:

```json
{
  "recognitionStatus": "Unknown",
  "recognition": {
    "personId": "unknown-person-guid",
    "personNumber": 42,
    "name": "Unknown #0042",
    "isUnknown": true,
    "similarity": 0.36,
    "minimumSimilarity": 0.40,
    "matchedSampleId": null
  }
}
```

در این حالت باید مشخص باشد که فرد واقعاً ناشناس تشخیص داده شده است، نه اینکه recognition اصلاً اجرا نشده باشد.

## 7. سناریوی `PlateFaceAssociation`

این سناریو دو detection مستقل و یک نتیجهٔ ارتباطی دارد:

```json
{
  "components": {
    "plate": { },
    "face": { },
    "association": {
      "status": "MatchedPreviousRecord",
      "plateComponentId": "plate-component",
      "faceComponentId": "face-component",
      "plateText": "12الف34567",
      "personId": "person-guid",
      "personName": "Ali Ahmadi",
      "isUnknownPerson": false,
      "associationConfidence": 0.91,
      "matchingPolicy": "SameVehicleRoiAndTimeWindow",
      "timeWindowSeconds": 10,
      "spatialRelation": {
        "sameRoi": true,
        "overlap": 0.62,
        "distancePixels": 340
      },
      "previousRecord": {
        "recordId": "association-record-guid",
        "firstSeenAtUtc": "2026-09-01T08:00:00Z",
        "lastSeenAtUtc": "2026-09-19T17:30:00Z",
        "evidenceEventId": "previous-event-guid",
        "historicalConfidence": 0.87
      }
    }
  }
}
```

مقادیر مجاز `association.status`:

| وضعیت | معنا |
| --- | --- |
| `MatchedPreviousRecord` | پلاک و چهره با رکورد قبلی تطبیق دارند |
| `NewAssociation` | هر دو معتبرند ولی ارتباط قبلی وجود ندارد |
| `Conflict` | پلاک و چهره با رکورد قبلی ناسازگارند |
| `UnknownFace` | پلاک دیده شده ولی چهره ناشناس است |
| `PlateOnly` | چهره برای association کافی نیست |
| `InsufficientQuality` | یکی از دو component کیفیت لازم را ندارد |
| `NoPreviousRecord` | تشخیص انجام شد ولی سابقه‌ای پیدا نشد |

## 8. رکورد ارتباطی قبلی

رکورد ارتباطی خودرو و شخص نباید داخل `People` یا `FaceSamples` قرار گیرد. این یک domain جداست:

```text
VehiclePersonAssociation
 ├── AssociationId
 ├── PlateTextNormalized
 ├── PersonId nullable
 ├── PersonNameSnapshot
 ├── IsUnknownPerson
 ├── FirstSeenAtUtc
 ├── LastSeenAtUtc
 ├── Confidence
 ├── Status
 └── EvidenceEventIds
```

این رکورد می‌تواند از eventهای معتبر ساخته یا توسط API/کاربر تأیید شود. event جدید باید هم نتیجهٔ جاری و هم reference رکورد قبلی را گزارش کند.

## 9. Artifactهای تصویری هر رخداد

برای هر event، بستهٔ artifactها می‌تواند شامل این موارد باشد:

| Artifact | محتوا |
| --- | --- |
| `FullFrameRaw` | فریم اصلی بدون Drawing، در صورت فعال بودن policy |
| `FullFrameAnnotated` | فریم کامل همراه ROIها و کادرهای detection |
| `RoiRaw` | crop خام ROI مربوط به task |
| `RoiAnnotated` | crop ROI همراه Drawing محلی و جزئیات detection |
| `DetectionCrop` | crop دقیق پلاک یا چهره |
| `FaceAlignedCrop` | crop aligned استفاده‌شده برای recognition |
| `PlateCrop` | crop نهایی پلاک |
| `CharacterCrop` | crop یا image sheet جزئیات characterها، در صورت نیاز |

در پیاده‌سازی فعلی آرشیو تشخیص، `FullFrameRaw` و `RoiRaw` از snapshot خام قبل
از اجرای pipeline و قبل از هرگونه رسم ROI یا detection ساخته می‌شوند. تصویر
رندرشدهٔ preview و overlay زنده مسیر جداگانه‌ای دارند و نباید به‌عنوان artifact
آرشیو استفاده شوند.

هر artifact:

```json
{
  "artifactId": "artifact-guid",
  "type": "RoiAnnotated",
  "contentType": "image/jpeg",
  "width": 640,
  "height": 360,
  "sourceFrameSequence": 991238,
  "sha256": "...",
  "sizeBytes": 48210,
  "retentionUntilUtc": "2026-10-20T10:20:30Z",
  "downloadUrl": "/api/v1/events/event-guid/artifacts/artifact-guid"
}
```

تصاویر باید قبل از broadcast پایدار شوند یا حداقل در یک outbox قابل‌بازیابی قرار گیرند. در غیر این صورت replay metadata انجام می‌شود ولی تصویر event قطع‌شده ممکن است وجود نداشته باشد.

## 10. Payload live، replay و Webhook

هر سه کانال باید همین event contract را استفاده کنند:

- SignalR: metadata کامل + artifact descriptors
- Replay API: metadata کامل + artifact descriptors
- Webhook: metadata کامل + URL امضاشده یا قابل‌دسترسی برای artifactها

برای eventهای کوچک، ارسال `DetectionCrop` به‌صورت multipart اختیاری است؛ Base64 برای فریم کامل توصیه نمی‌شود.

## 11. API artifact و association

```text
GET   /api/v1/events/{eventId}
GET   /api/v1/events/{eventId}/artifacts
GET   /api/v1/events/{eventId}/artifacts/{artifactId}

GET   /api/v1/associations
POST  /api/v1/associations
GET   /api/v1/associations/{associationId}
PATCH /api/v1/associations/{associationId}
DELETE /api/v1/associations/{associationId}
GET   /api/v1/associations/{associationId}/evidence
```

## 12. معیار پذیرش قرارداد

- رخداد پلاک بدون نیاز به Face شامل فریم، ROI، پلاک، characterها و جزئیات مدل باشد.
- رخداد چهره مشخص کند detection موفق بوده، recognition اجرا شده یا نه، فرد ناشناس است یا به چه شخصی متصل شده است.
- رخداد پلاک/چهره reference هر دو component و نتیجهٔ association با رکورد قبلی را داشته باشد.
- فریم artifact با `sourceFrameSequence` به همان فریم تشخیص متصل باشد.
- قطع UI باعث حذف metadata یا artifact رخداد نشود، مشروط به retention.
- eventهای live و replay دقیقاً schema یکسان داشته باشند.
