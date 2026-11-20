# Rendering و WebRTC با Drawing پایدار

## مسیر فعال نمایش

Tile، focus، fullscreen و ROI زندهٔ UI وب از `CompositeWebRtcStream` و خروجی
کامپوزیت‌شدهٔ سرویس استفاده می‌کنند. دریافت، inference، preview و encode
مستقل‌اند؛ پایان تشخیص محرک ارسال فریم نیست.

```text
Camera RTSP → FFmpeg، یک decoder thread → جدیدترین فریم
                                          ├── inference با ابعاد اصلی
                                          └── preview worker هر دوربین
                                                ↓ آخرین overlay معتبر
                                           Bitmap در اندازهٔ نمایش
                                                ↓ یک pending slot
                                           encoder مستقل همان دوربین
                                                ↓ یک نمونهٔ VP8 مشترک
                                           WebRTC peerهای همان دوربین
```

دریافت در Windows و سرویس فقط از مسیر مستقیم FFmpeg است. `Threads` آیتم
پردازشی مربوط به ONNX است و تعداد threadهای decoder را تغییر نمی‌دهد.
تغییر اندازهٔ نمایش، ورودی مدل و ROI پردازش را کوچک نمی‌کند.

## نرخ، ابعاد و تقاضای نمایش

`CameraRuntime.ConfigurePreview(maxSize, enabled, maxFps)` تقاضای نمایش را
کنترل می‌کند. سقف ۱۵ FPS حفظ شده است و `Service.Runtime.PreviewFps` در بازهٔ
`1..15` اعمال می‌شود.

مرورگر عرض و ارتفاع فضای نمایش را از CSS می‌فرستد؛ `devicePixelRatio` به آن
ضرب نمی‌شود. Resize درخواست همان session را به‌روز می‌کند. برای هر دوربین،
بزرگ‌ترین عرض و ارتفاع در میان viewerهای متصل، ابعاد source مشترک را تعیین
می‌کند. Viewer کوچک ممکن است خروجی بزرگ‌تر مورد نیاز viewer دیگر را بگیرد؛
تعداد viewer یک دوربین تعداد encoder را افزایش نمی‌دهد.

ابعاد encode از `1280×720` و ابعاد منبع بزرگ‌تر نمی‌شوند و در هر دو محور
مضرب ۱۶ هستند. نسبت ابعاد در حد گردکردن حفظ می‌شود؛ کل تصویر resize می‌شود و
letterbox یا crop اضافه ندارد. برای نمونه، viewport با اندازهٔ `640×360`
می‌تواند فریم `640×352` بگیرد. مختصات نرمال ROI روی همان تصویر کامل معتبر
می‌ماند.

Preview فقط برای WebRTC viewer متصل یا درخواست snapshot تولید می‌شود.
Session در مرحلهٔ signaling هنوز preview یا encoder را فعال نمی‌کند. مرورگر
هنگام مخفی‌شدن صفحه یا خارج‌شدن viewer از دید، session را می‌بندد و هنگام
بازگشت دوباره متصل می‌شود. حذف آخرین تقاضا منابع نمایش را آزاد می‌کند؛ capture
و inference ادامه دارند.

Snapshot یک تقاضای سه‌ثانیه‌ای ایجاد می‌کند. اگر فقط snapshot مصرف‌کننده باشد،
نرخ preview حداکثر ۵ FPS است. اولین درخواست پس از حالت بدون نمایش تا ۷۵۰
میلی‌ثانیه برای فریم منتظر می‌ماند و دوربین متوقف را خودکار start نمی‌کند.
پس از پایان تقاضا، snapshot ذخیره‌شده نیز آزاد می‌شود.

## مالکیت فریم و حافظه

`FrameSource` فقط جدیدترین Mat منتظر inference را نگه می‌دارد. worker نمایش
با Matهای قابل استفادهٔ مجدد، کپی جداگانهٔ خودش را می‌سازد؛ inference و renderer
روی یک Mat مشترک کار نمی‌کنند.

`Camera_FrameReady` در سرویس Bitmap را به gateway قرض می‌دهد؛ gateway یک
clone برای slot منتظر می‌گیرد. هنگام تقاضای snapshot، مخزن snapshot مالکیت
خود Bitmap را می‌گیرد؛ در غیر این صورت میزبان آن را آزاد می‌کند.

`CameraVideoPump` برای هر دوربین:

- یک worker و یک encoder دارد؛ task به‌ازای هر فریم ساخته نمی‌شود؛
- حداکثر یک Bitmap در حال encode و یک Bitmap منتظر نگه می‌دارد؛
- فریم منتظر قبلی را هنگام جایگزینی با فریم جدید آزاد می‌کند؛
- sequence تکراری یا قدیمی را دوباره encode نمی‌کند؛
- Bitmap تبدیل رنگ، Mat و آرایهٔ I420 را تا تغییر ابعاد دوباره استفاده می‌کند؛
- نمونهٔ فشردهٔ VP8 و fragmentهای payload آن را بین peerهای همان دوربین
  مشترک می‌کند. هر viewer state ارتباط و زمان RTP خودش را دارد.

با تغییر ابعاد واقعی encode، codec قبلی روی worker آزاد و codec تازه ساخته
می‌شود و نخستین خروجی keyframe است. در نسخهٔ فعلی `Vpx.Net`، planeهای داخلی
codec پس از کوچک‌شدن تصویر ممکن است اندازهٔ قبلی را نگه دارند؛ درخواست
keyframe به‌تنهایی کافی نیست. در ابعاد ثابت codec و bufferهای I420 دوباره
استفاده می‌شوند. آزمون native، کاهش و افزایش اندازه و ادامهٔ خروجی را از
header واقعی VP8 بررسی می‌کند.

Encoder یک دوربین worker دوربین دیگر را مسدود نمی‌کند. هنگام close، pending
frame حذف می‌شود و همان worker پس از پایان encode جاری codec و bufferهایش را
آزاد می‌کند. codec در میانهٔ فراخوانی native از thread دیگری dispose نمی‌شود؛
عملیات native جاری ممکن است تا بازگشت خودش ادامه داشته باشد.

## Overlay و استقلال از inference

Preview فریم تازه را با آخرین state معتبر ROI، کادرها، labelها و primitiveهای
پردازشی ترکیب می‌کند. `DetectionOverlayHoldMs` مدت نگه‌داری نتیجهٔ قبلی را
کنترل می‌کند؛ پیش‌فرض ۲۵۰۰ میلی‌ثانیه است. کندبودن inference باید تازگی کادر را
تغییر دهد، نه حرکت تصویر را متوقف کند.

`_renderGate` فقط تعویض یا snapshot فهرست نتیجه را محافظت می‌کند. خود inference
زیر قفل preview اجرا نمی‌شود. Drawing پلاک هنوز از `_overlayGate` استفاده
می‌کند؛ در صورت باقی‌ماندن هزینهٔ render، اندازه‌گیری این بخش مرحلهٔ بعد است.
تعداد threadهای ONNX برای جبران فشار نمایش بدون اندازه‌گیری افزایش نمی‌یابد.

## Signaling و عمر session

| درخواست | رفتار |
| --- | --- |
| `POST /api/v1/streams/{cameraId}/webrtc/offer` | دریافت `type`، `sdp` و `maxWidth/maxHeight` اختیاری؛ پاسخ شامل `sessionId`، `type`، `sdp` و `supportsViewportUpdates` است. دوربین باید فعال و در حال اجرا باشد. |
| `PATCH /api/v1/streams/webrtc/{sessionId}` | به‌روزکردن ابعاد اختیاری و تمدید heartbeat همان session |
| `DELETE /api/v1/streams/webrtc/{sessionId}` | بستن viewer و کاهش تقاضای نمایش همان دوربین |
| `GET /api/v1/streams/diagnostics` | آمار encoder و صف برای دوربین‌های دارای session |
| `GET /api/v1/streams/{cameraId}/snapshot` | JPEG خروجی Drawing همراه با تقاضای موقت preview |

UI جدید هر ۱۰ ثانیه heartbeat می‌فرستد. Session دارای viewport پس از ۴۵ ثانیه
بدون تمدید منقضی می‌شود؛ signaling ناقص پس از ۳۰ ثانیه پاک می‌شود. نگه‌داری
دوره‌ای هر پنج ثانیه است، بنابراین پاک‌سازی نهایی ممکن است تا tick بعدی طول
بکشد. کلاینت قدیمی که ابعاد نفرستاده، پس از اتصال به heartbeat وابسته نیست؛
capability پاسخ به UI اجازه می‌دهد سرویس قدیمی را نیز تحمل کند.

زمان RTP از timestamp محلی فریم دریافت‌شده با clock نودکیلوهرتز محاسبه می‌شود؛
حذف فریم، فاصلهٔ زمانی را به مقدار ثابت ۶۷ میلی‌ثانیه تبدیل نمی‌کند. هر فریم
VP8 به payloadهای حداکثر ۱۲۰۰ بایتی قطعه می‌شود؛ fragment نخست بیت start دارد،
همهٔ fragmentها timestamp یکسان دارند و fragment آخر marker می‌گیرد. Viewer
جدید تا keyframe فریم وابسته دریافت نمی‌کند. اتصال جدید، تغییر اندازه و PLI
درخواست keyframe می‌دهند؛ PLI تکراری محدود می‌شود.

## درگاه مستقل خام WHEP

درگاه RTSP به WHEP سرویس جدا از مسیر Drawing فعال وب وجود دارد:

```text
MediaMTX WHEP خام → Browser <video>
Overlay API       → Browser SVG overlay
```

`MediaMtxRuntime` فقط در سرویس است و برای source فعال RTSP هنگام درخواست WHEP،
path مرورگر را می‌سازد. ویدئوی خام برای اضافه‌کردن Drawing دوباره encode
نمی‌شود. `GET /api/v1/streams/{cameraId}/overlay` مختصات و متادیتای سبک را
برمی‌گرداند. این درگاه و کامپوننت `RawMediaMtxStream` گیرندهٔ دیگری برای موتور
نیستند؛ UI فعال همچنان `CompositeWebRtcStream` را استفاده می‌کند.

## مشاهده و بررسی

Diagnostics تعداد viewerهای کل و متصل، ابعاد واقعی، تعداد فریم‌های دریافتی و
encodeشده، replacementها، duplicateها، زمان encode، سن آخرین فریم، FPS خروجی،
byteهای encodeشده، تعداد ارسال/خطا و تعداد frameهای active/pending را می‌دهد.
این آمار داخلی است و تأخیر کامل دوربین تا مرورگر را اندازه نمی‌گیرد. درصد
بهبود CPU/RAM باید با بار واقعی اندازه‌گیری شود.

بررسی‌های پیشین با ورودی مصنوعی، صف محدود، استقلال دوربین‌ها، encoder مشترک،
حذف duplicate، عمر buffer و session، تبدیل I420 و قطعه‌بندی/زمان RTP را پوشش
داده‌اند. این بررسی‌ها شامل دوربین واقعی، دیتابیس و trigger نبوده‌اند.
