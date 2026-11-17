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

هر `Camera` یک `FrameSource` و loop مستقل دارد. source همیشه فقط جدیدترین فریم را نگه می‌دارد و فریم منتظر قبلی با ورود فریم تازه آزاد می‌شود. برای stream زنده نباید انتظار پردازش تک‌تک فریم‌ها را داشت.

## دریافت مستقیم تصویر

دریافت RTSP در Windows و سرویس مسیر یکسانی دارد:

```text
Camera RTSP → FrameSource/OpenCV FFmpeg → newest-frame slot → CameraRuntime
```

RTSP با تنظیمات کم‌تأخیر و یک thread صریح decoder باز می‌شود؛ TCP/UDP و فاصلهٔ
reconnect از تنظیمات دوربین می‌آیند. `IFrameSource.Configure(source, transport,
reconnectDelayMs)` اتصال را تنظیم می‌کند. فریم `FrameAvailable` فقط در طول callback
borrowed است و فریم خروجی `TryDequeueFrame` به مصرف‌کننده منتقل می‌شود.

نمایش مرورگر متعلق به سرویس است: UI فعلی از خروجی کامپوزیت‌شدهٔ WebRTC سرویس
استفاده می‌کند. درگاه مستقل MediaMTX/WHEP نیز برای sourceهای فعال RTSP و
مصرف‌کنندهٔ پخش خام موجود است و Overlay سبک را جداگانه می‌دهد. ورودی تشخیص
مستقیماً از دوربین به `FrameSource` می‌رود. جزئیات در
[راهنمای پخش مرورگر](../docs/MediaMTX-WebRTC.md) است.

برای source جدید `IFrameSource` و برای تحلیل جدید `IProcessingPipeline` را پیاده‌سازی کنید و آن را با `ProcessingModuleRegistration` در `ProcessingRegistry` ثبت کنید. نوع قابلیت با `ProcessingType` مشخص می‌شود؛ string فقط برای سازگاری فایل تنظیمات استفاده می‌شود. `ProcessingContext.Image` تصویر محلی ROI است و bounds خروجی باید با ابعاد همان ROI سازگار باشد. برای رسم هندسهٔ بصری از `PipelineResult.Overlays` و `ProcessingOverlayKind` استفاده کنید؛ از ساختن detection برای هر نقطه یا خط پرهیز کنید، چون detection وارد event/history می‌شود و هزینهٔ اضافی دارد. اگر pipeline تصویر را resize می‌کند، نگاشت bounds و overlayها به ROI اصلی بر عهدهٔ خود pipeline است. اگر pipeline `NextImage` برمی‌گرداند، Runtime با `PipelineResult.TakeNextImage()` مالکیت آن را به pipeline بعدی منتقل می‌کند؛ در غیر این صورت `PipelineResult.Dispose()` آن را آزاد می‌کند.

وقتی `DrawBoxes` روشن باشد، هر `AnalysisDetection` روی preview رسم می‌شود. برای قرارداد رنگ‌بندی، metadata نتیجه باید `OverlayThreshold` را به‌عنوان آستانهٔ نمایش داشته باشد و در صورت نیاز `Accepted` را برای اعلام اعتبار نهایی تنظیم کند؛ خروجی سبز پذیرفته‌شده و خروجی قرمز فقط در بازهٔ حداکثر ۱۰٪ پایین‌تر از threshold است. کاندیدهای ضعیف‌تر منتشر نمی‌شوند. وقتی `DrawBoxes` خاموش باشد هیچ‌کدام از این ترسیم‌ها انجام نمی‌شود. کاندید قرمز فقط برای بازخورد تصویری است و نباید وارد tracking، recognition یا history شود.

Engine license و `ProcessingRegistry` را از Composition Root دریافت می‌کند. `CameraRuntime` مسئول capture، lifecycle و Motion Gate است و `CameraPipelineCoordinator` برای هر ROI graph را از registry می‌سازد؛ ROIهای یک دوربین موازی اجرا می‌شوند و `NamedRoi.ProcessingMode` اجرای pipelineهای داخل هر ROI را سریالی یا موازی می‌کند. در حالت سریالی `PreviousDetections`/`NextImage` زنجیره می‌شوند و در حالت موازی هر pipeline کپی مستقل تصویر ROI را می‌گیرد. Composition Root برنامه `PlateModule` و `FaceModule` را در یک registry مشترک ثبت می‌کند؛ بنابراین Runtime به concrete type یا مسیر ساخت مخصوص هیچ‌کدام وابسته نیست. هر دو از قرارداد `IProcessingPipeline` استفاده می‌کنند و state آن‌ها بین ROIها مشترک نیست. جزئیات در [LICENSING.md](../docs/LICENSING.md) است.
