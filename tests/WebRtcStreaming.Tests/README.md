# Synthetic WebRTC checks

This Windows x64 console project references the service assembly but does not run
its `Program`, `DetectionRuntimeHost`, real cameras, models, database or triggers.
No credentials or private camera URLs are needed.

Run the checks from the repository root:

```powershell
dotnet run --project tests/WebRtcStreaming.Tests/WebRtcStreaming.Tests.csproj
```

The process exits with code `0` when every check passes and `1` on failure. It
uses fake encoders/peers for deterministic scheduling and lifetime checks, and
the packaged Emgu/OpenCV conversion for synthetic I420 pixel checks. It covers:

- viewport bounds, 16 pixel alignment, source aspect and no upscaling;
- one encoder and encoded payload shared by viewers of one camera;
- duplicate/old sequence rejection;
- one active image plus one newest pending image under a blocked encoder;
- independent workers for a blocked and a healthy camera;
- no encoding allocation for viewers still negotiating;
- keyframe delivery for a joining viewer;
- pending frame release and codec disposal on its worker after shutdown;
- I420 array reuse and reallocation only when dimensions change;
- correct Y/U/V plane order and source/ROI coordinate coverage without padding;
- real native VP8 encoding across viewport downsize/upsize, with keyframe header
  dimensions checked against the actual bitstream;
- VP8 fragment bounds, start descriptor and byte reconstruction;
- 90 kHz RTP source timing, skipped-frame intervals and timestamp wraparound;
- pending session expiry, viewport heartbeat renewal and legacy compatibility.

The worker checks use bounded waits. One shutdown check creates a single
coordinating test task; frame submission does not start a task per frame.

## Native browser probe

Start a separate synthetic process:

```powershell
dotnet run --project tests/WebRtcStreaming.Tests/WebRtcStreaming.Tests.csproj -- --serve
```

Open [the local probe](http://127.0.0.1:5096). This listener binds only to
`127.0.0.1:5096`. It creates its own `WebRtcGateway` and two generated
`1280×720`, 15 FPS sources on two bounded worker threads. The actual VP8 encoder
and WebRTC/RTP stack are exercised by the browser decoder. The service listener,
camera configuration and saved data are not involved.

1. Click **Start all viewers**. Both generated moving scenes should advance.
2. Select `320×180`, `640×360` or `1280×720` per viewer. Actual decoded dimensions
   appear below the video and may be smaller after 16 pixel alignment.
3. Add another viewer for camera A and start it. Diagnostics should still show
   one camera A encoder stream, with an increased viewer count. Encoded frame
   count should track source frames rather than the number of viewers.
4. Resize one viewer while another plays. Both should continue decoding; the
   larger viewer demand controls their shared camera dimensions.
5. Click **Stop all viewers**. Gateway diagnostics should become an empty list
   after the sessions close. Restarting should negotiate fresh sessions and
   produce a keyframe.

The page shows `RTCPeerConnection.getStats()` samples including decoded frames,
measured FPS, dimensions, dropped frames, loss and jitter. Current frame/dimension
counts are also exposed as `data-frames`, `data-width` and `data-height` on each
viewer section for browser automation. **Gateway diagnostics** show encoder
counts, frame age, timings and active/pending frame bounds.

The test signaling endpoints are `/offer/{cameraId}`,
`PATCH /session/{sessionId}`, `DELETE /session/{sessionId}` and `/diagnostics`.
They exist only in this test process. Close the process with Ctrl+C after the
probe. The probe runs neither a detector nor a live camera, so its FPS and CPU
measurements validate the streaming path rather than production model load.

## Recorded local check — 2026-11-17

- The solution and this project built successfully with zero warnings/errors in
  a separate artifacts directory; the frontend production build also passed.
- All 15 streaming checks and 36 default tracked-completion checks passed.
- The browser decoded two generated sources and a second viewer of camera A;
  diagnostics showed two encoders for three viewers. Downsize/upsize retained
  the same session, and closing every viewer emptied gateway diagnostics.
- This was a functional check. Browser dropped frames and variable decode FPS
  were observed during the run; it does not establish stutter-free playback or
  a CPU/RAM improvement percentage under actual recognition load.
- The running detection service was not replaced or restarted. Validation with
  the user's two real cameras remains necessary after installing the new build.
