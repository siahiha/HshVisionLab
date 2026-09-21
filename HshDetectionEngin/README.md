# HshDetectionEngin SDK

هستهٔ چنددوربینه مستقل از WinForms است. راهنمای کامل معماری و بازسازی در [REBUILD_GUIDE.md](../docs/REBUILD_GUIDE.md) است.

```csharp
using HshDetectionEngin;

var settings = new CameraSettings
{
    Name = "Entrance",
    SourceUrl = "0", // Webcam index, or an RTSP/video-file path
    MotionGateEnabled = true,
    Rois =
    [
        new NamedRoi
        {
            Name = "Entrance",
            Points =
            [
                new RoiPoint(0, 0),
                new RoiPoint(1, 0),
                new RoiPoint(1, 1),
                new RoiPoint(0, 1)
            ],
            Processing =
            [
                new CameraProcessingSettings
                {
                    Type = "Plate",
                    Name = "Plate detection",
                    Enabled = true,
                    ModelFile = "best.onnx"
                }
            ]
        }
    ]
};

using var camera = new Camera(settings);
camera.PipelineResultsReady += (_, detections) => Render(detections);
camera.Start();
```

هر `Camera` یک `FrameSource` و loop مستقل دارد. به‌صورت پیش‌فرض source فقط جدیدترین فریم را نگه می‌دارد؛ اگر `BufferCount` مثبت باشد، یک صف محدود با همان ظرفیت ایجاد می‌شود و هنگام پرشدن قدیمی‌ترین فریم حذف می‌گردد. برای stream زنده نباید انتظار پردازش تک‌تک فریم‌ها را داشت.

## Backendهای دریافت تصویر

`CameraSettings.CaptureBackend` سه مسیر را پشتیبانی می‌کند:

```text
FFmpeg   → FrameSource/OpenCV FFmpeg
LibVLC   → VlcFrameSource/LibVLC و Live555
MediaMTX → MediaMtxFrameSource → local RTSP path در 127.0.0.1:8554
```

در حالت MediaMTX، runtime upstream دوربین را در path اختصاصی MediaMTX ثبت
می‌کند و engine از local RTSP با TCP و حالت low-latency می‌خواند. این مسیر برای
inference است؛ مرورگر باید همان path را از WHEP خام بگیرد و Drawing را جداگانه
روی کلاینت رسم کند. `MediaMtxFrameSource` مقدار buffer را صفر نگه می‌دارد تا
فریم قدیمی در صف پردازش باقی نماند. در حالت LibVLC، MediaMTX استفاده نمی‌شود و
فریم decodeشده مستقیماً از callback LibVLC وارد latest-frame slot یا صف محدود
می‌شود.

برای source جدید `IFrameSource` و برای تحلیل جدید `IProcessingPipeline` را پیاده‌سازی کنید و آن را با `ProcessingModuleRegistration` در `ProcessingRegistry` ثبت کنید. نوع قابلیت با `ProcessingType` مشخص می‌شود؛ string فقط برای سازگاری فایل تنظیمات استفاده می‌شود. `ProcessingContext.Image` تصویر محلی ROI است و bounds خروجی باید با ابعاد همان ROI سازگار باشد. برای رسم هندسهٔ بصری از `PipelineResult.Overlays` و `ProcessingOverlayKind` استفاده کنید؛ از ساختن detection برای هر نقطه یا خط پرهیز کنید، چون detection وارد event/history می‌شود و هزینهٔ اضافی دارد. اگر pipeline تصویر را resize می‌کند، نگاشت bounds و overlayها به ROI اصلی بر عهدهٔ خود pipeline است. اگر pipeline `NextImage` برمی‌گرداند، Runtime با `PipelineResult.TakeNextImage()` مالکیت آن را به pipeline بعدی منتقل می‌کند؛ در غیر این صورت `PipelineResult.Dispose()` آن را آزاد می‌کند.

وقتی `DrawBoxes` روشن باشد، هر `AnalysisDetection` روی preview رسم می‌شود. برای قرارداد رنگ‌بندی، metadata نتیجه باید `OverlayThreshold` را به‌عنوان آستانهٔ نمایش داشته باشد و در صورت نیاز `Accepted` را برای اعلام اعتبار نهایی تنظیم کند؛ خروجی سبز پذیرفته‌شده و خروجی قرمز فقط در بازهٔ حداکثر ۱۰٪ پایین‌تر از threshold است. کاندیدهای ضعیف‌تر منتشر نمی‌شوند. وقتی `DrawBoxes` خاموش باشد هیچ‌کدام از این ترسیم‌ها انجام نمی‌شود. کاندید قرمز فقط برای بازخورد تصویری است و نباید وارد tracking، recognition یا history شود.

Engine license و `ProcessingRegistry` را از Composition Root دریافت می‌کند. `CameraRuntime` مسئول capture، lifecycle و Motion Gate است و `CameraPipelineCoordinator` برای هر ROI graph را از registry می‌سازد و pipelineها را اجرا می‌کند. Composition Root برنامه `PlateModule` و `FaceModule` را در یک registry مشترک ثبت می‌کند؛ بنابراین Runtime به concrete type یا مسیر ساخت مخصوص هیچ‌کدام وابسته نیست. هر دو از قرارداد `IProcessingPipeline` استفاده می‌کنند و state آن‌ها بین ROIها مشترک نیست. جزئیات در [LICENSING.md](../docs/LICENSING.md) است.
