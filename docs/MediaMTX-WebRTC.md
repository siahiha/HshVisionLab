# MediaMTX / WebRTC

با انتخاب `MediaMTX` در تنظیمات هر دوربین، برنامه یک MediaMTX مشترک را فقط
هنگام اولین اتصال اجرا می‌کند. برای هر دوربین path جداگانه ساخته می‌شود:

```text
RTSP camera -> MediaMTX path -> WebRTC/WHEP browser
                         \-> local RTSP reader -> detection pipeline
```

این دو شاخه عمداً از هم جدا هستند. شاخهٔ WHEP برای نمایش کم‌تاخیر تصویر خام
در مرورگر است؛ شاخهٔ RTSP داخلی فقط ورودی موتور تشخیص و state مربوط به
Overlay را تأمین می‌کند. رابط وب، Drawing را با یک لایهٔ جداگانه روی ویدئوی
خام رسم می‌کند و برای نمایش MediaMTX از مسیر `FrameReady`، تبدیل Bitmap یا
`WebRtcGateway` کامپوزیت‌شده استفاده نمی‌کند:

```text
MediaMTX WHEP خام ───────────────► <video> مرورگر
                                      ▲
GET /api/v1/streams/{id}/overlay ────┘  SVG overlay (`LiveOverlaySvg`)
```

پاسخ Overlay شامل ابعاد فریم، ROIهای ثابت، bounds و مشخصات detectionها و
primitiveهای پردازشی است. detectionهای پویا پس از حدود 2.5 ثانیه منقضی
می‌شوند. به این ترتیب inference یا render کند، latency خود ویدئو را افزایش
نمی‌دهد.

مسیر WebRTC هیچ HLS segment یا transcoding ندارد. مرورگر از endpoint سرویس
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

نسخهٔ Windows در خروجی Debug/Publish هر دو برنامه در مسیر
`MediaMTX/mediamtx.exe` کپی می‌شود. در نصب نهایی نیز می‌توان مسیر آن را با
`HSH_MEDIAMTX_PATH` تغییر داد.

متغیرهای اختیاری:

```text
HSH_MEDIAMTX_PATH
HSH_MEDIAMTX_CONFIG
HSH_MEDIAMTX_RTSP_PORT       (پیش‌فرض 8554)
HSH_MEDIAMTX_WEBRTC_PORT     (پیش‌فرض 8889)
HSH_MEDIAMTX_WEBRTC_UDP_PORT (پیش‌فرض 8189)
HSH_MEDIAMTX_API_PORT        (پیش‌فرض 9997)
HSH_MEDIAMTX_ADDITIONAL_HOSTS (با comma یا semicolon)
```

برای دسترسی WebRTC از سیستم دیگر، پورت UDP مربوط به WebRTC باید در Firewall
باز باشد و آدرس قابل دسترسی سرور در `HSH_MEDIAMTX_ADDITIONAL_HOSTS` قرار گیرد.
اگر فقط روی همان سیستم تست می‌شود، `127.0.0.1` و `localhost` به‌صورت پیش‌فرض
اضافه می‌شوند.

`sourceOnDemand` فعال است؛ بنابراین قطع آخرین local reader یا viewer باعث قطع
اتصال upstream بعد از timeout کوتاه می‌شود. صف دریافت داخلی نیز در حالت
`BufferCount = 0` فقط آخرین فریم را نگه می‌دارد.

در ورودی داخلی MediaMTX، `MediaMtxFrameSource` مسیر local RTSP را با TCP و
حالت کم‌تاخیر FFmpeg (`nobuffer`، `low_delay` و `max_delay=0`) باز می‌کند و
برای این backend مقدار buffer را صفر نگه می‌دارد. `sourceOnDemandStartTimeout`
و `sourceOnDemandCloseAfter` فقط روی شروع/بستن upstream اثر دارند و نباید به‌عنوان
تأخیر ثابت هر فریم تفسیر شوند.

برای تشخیص latency، اگر مرورگر در حالت MediaMTX به
`/api/v1/streams/{id}/webrtc/offer` وصل شود، مسیر legacy کامپوزیت‌شده فعال شده
است. مسیر درست رابط فعلی، WHEP زیر است:

```text
POST/PATCH/DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
GET              /api/v1/streams/{cameraId}/overlay
```

مستندات رسمی: [MediaMTX WebRTC/WHEP](https://mediamtx.org/docs/read/webrtc)،
[Control API](https://mediamtx.org/docs/features/control-api)،
[Configuration reference](https://mediamtx.org/docs/references/configuration-file).
