# MediaMTX / WebRTC

سرویس برای sourceهای معتبر RTSP و فعال، درگاه مستقل MediaMTX/WHEP دارد.
`MediaMtxRuntime` در پروژهٔ سرویس، یک MediaMTX مشترک را هنگام اولین درخواست
پخش خام اجرا می‌کند و برای هر دوربین path جداگانه می‌سازد:

```text
RTSP camera ──┬─ FrameSource/FFmpeg (1 decoder thread) → detection pipeline
              └─ Service MediaMTX path → WebRTC/WHEP browser
```

شاخهٔ WHEP تصویر خام را به مصرف‌کنندهٔ مرورگر می‌دهد و موتور تشخیص هم‌زمان
مستقیماً با FFmpeg، یک thread decoder و نگه‌داری جدیدترین فریم از دوربین
می‌خواند. مصرف‌کنندهٔ WHEP می‌تواند Drawing را با یک لایهٔ جداگانه روی ویدئوی
خام رسم کند:

```text
MediaMTX WHEP خام ───────────────► <video> مرورگر
                                      ▲
GET /api/v1/streams/{id}/overlay ────┘  SVG overlay (`LiveOverlaySvg`)
```

پاسخ Overlay شامل ابعاد فریم، ROIهای ثابت (`rois`)، ناحیه‌های Motion ROI
(`motionRois`)، bounds و مشخصات detectionها و primitiveهای پردازشی است. `rois`
با خط پیوسته و نام ROI و `motionRois` با همان هندسهٔ scale‌شده و خط‌چین رسم
می‌شوند تا با previewهای قبلی یکسان باشند. detectionهای پویا پس از حدود 2.5
ثانیه منقضی می‌شوند. ویدئوی WHEP خام برای Drawing دوباره encode نمی‌شود.

رابط وب فعلی در tile، نمای متمرکز و ROI زنده از `CompositeWebRtcStream` و
endpoint `/webrtc/offer` استفاده می‌کند. کامپوننت خام `RawMediaMtxStream`
در سورس موجود است و در این نماهای فعال mount نمی‌شود. این سند قرارداد درگاه
WHEP مستقل را شرح می‌دهد.

مسیر خام WHEP هیچ HLS segment یا transcoding ندارد. مصرف‌کننده از endpoint سرویس
استفاده می‌کند:

```text
POST /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
PATCH /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
```

`viewerId` باید برای هر تب یا viewer مستقل باشد. پاسخ SDP و headerهای `Location`
و `Link` از MediaMTX عبور داده می‌شوند و URL دوربین یا username/password هرگز
به مرورگر داده نمی‌شود.

## پورت‌ها و مسیر binary

binary در خروجی Debug/Publish سرویس در مسیر
`MediaMTX/mediamtx.exe` کپی می‌شود. در نصب نهایی نیز می‌توان مسیر آن را با
`HSH_MEDIAMTX_PATH` تغییر داد.

متغیرهای اختیاری:

```text
HSH_MEDIAMTX_PATH
HSH_MEDIAMTX_CONFIG
HSH_MEDIAMTX_WEBRTC_PORT     (پیش‌فرض 9000)
HSH_MEDIAMTX_WEBRTC_UDP_PORT (پیش‌فرض 8190)
HSH_MEDIAMTX_API_PORT        (پیش‌فرض 9997)
HSH_MEDIAMTX_ADDITIONAL_HOSTS (با comma یا semicolon)
```

برای دسترسی WebRTC از سیستم دیگر، پورت UDP مربوط به WebRTC باید در Firewall
باز باشد و آدرس قابل دسترسی سرور در `HSH_MEDIAMTX_ADDITIONAL_HOSTS` قرار گیرد.
اگر فقط روی همان سیستم تست می‌شود، `127.0.0.1` و `localhost` به‌صورت پیش‌فرض
اضافه می‌شوند.

`sourceOnDemand` فعال است؛ قطع آخرین viewer باعث قطع اتصال upstream درگاه
پس از timeout کوتاه می‌شود. تمام viewerهای WHEP یک دوربین از یک اتصال
upstream درگاه استفاده می‌کنند. اتصال upstream پخش WHEP با TCP برقرار می‌شود؛
تنظیم TCP/UDP دوربین مربوط به دریافت مستقیم FFmpeg موتور تشخیص است.
پیکربندی تولیدشدهٔ درگاه listener RTSP را غیرفعال می‌کند و فقط Control API و
WebRTC را فعال نگه می‌دارد. فایل config سفارشی همچنان قابل استفاده است.
`sourceOnDemandStartTimeout` و `sourceOnDemandCloseAfter` زمان شروع/بستن
upstream را کنترل می‌کنند.

مصرف‌کنندهٔ پخش خام از endpointهای زیر استفاده می‌کند؛ endpoint
`/api/v1/streams/{id}/webrtc/offer` خروجی کامپوزیت‌شدهٔ فعال UI فعلی را می‌دهد:

```text
POST/PATCH/DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
GET              /api/v1/streams/{cameraId}/overlay
```

مستندات رسمی: [MediaMTX WebRTC/WHEP](https://mediamtx.org/docs/read/webrtc)،
[Control API](https://mediamtx.org/docs/features/control-api)،
[Configuration reference](https://mediamtx.org/docs/references/configuration-file).
