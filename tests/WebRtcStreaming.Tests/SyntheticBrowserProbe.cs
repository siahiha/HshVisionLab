using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using HshDetectionService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace WebRtcStreaming.Tests;

/// <summary>Local test-only signaling and synthetic motion; no detection host or persistence.</summary>
internal static class SyntheticBrowserProbe
{
    public static async Task RunAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:5096");
        builder.Logging.ClearProviders();
        await using WebApplication app = builder.Build();
        using var gateway = new WebRtcGateway();
        using var first = new SyntheticSource(gateway, "camera-a", Color.FromArgb(25, 78, 140));
        using var second = new SyntheticSource(gateway, "camera-b", Color.FromArgb(100, 42, 120));

        app.MapGet("/", () => Results.Content(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SyntheticProbe.html")), "text/html"));
        app.MapGet("/diagnostics", () => Results.Ok(gateway.GetDiagnostics()));
        app.MapGet("/health", () => Results.Ok(new { synthetic = true, cameras = 2 }));
        app.MapPost("/offer/{cameraId}", async (string cameraId, ProbeOffer request, CancellationToken cancellationToken) =>
        {
            if (cameraId is not "camera-a" and not "camera-b") return Results.NotFound();
            try
            {
                WebRtcOfferResult answer = await gateway.AcceptOfferAsync(cameraId, request.Sdp, cancellationToken,
                    request.MaxWidth, request.MaxHeight, leased: true);
                return Results.Ok(answer);
            }
            catch (Exception error)
            {
                return Results.BadRequest(new { error = error.GetType().Name });
            }
        });
        app.MapPatch("/session/{sessionId}", (string sessionId, ProbeDemand request) =>
            gateway.UpdateSessionDemand(sessionId, request.MaxWidth, request.MaxHeight)
                ? Results.NoContent() : Results.NotFound());
        app.MapDelete("/session/{sessionId}", (string sessionId) =>
            gateway.Close(sessionId) ? Results.NoContent() : Results.NotFound());

        Console.WriteLine("Synthetic browser probe: http://127.0.0.1:5096 (two generated cameras, no detection host).");
        await app.RunAsync();
    }

    private sealed record ProbeOffer(string Sdp, int MaxWidth, int MaxHeight);
    private sealed record ProbeDemand(int MaxWidth, int MaxHeight);

    private sealed class SyntheticSource : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public SyntheticSource(WebRtcGateway gateway, string cameraId, Color background)
        {
            _thread = new Thread(() => Run(gateway, cameraId, background))
            {
                IsBackground = true,
                Name = "SyntheticVideoSource"
            };
            _thread.Start();
        }

        private void Run(WebRtcGateway gateway, string cameraId, Color background)
        {
            using var image = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
            using Graphics graphics = Graphics.FromImage(image);
            graphics.SmoothingMode = SmoothingMode.None;
            using var font = new Font("Arial", 32, FontStyle.Bold, GraphicsUnit.Pixel);
            using var line = new Pen(Color.FromArgb(160, 205, 220), 2);
            using var outline = new Pen(Color.Lime, 5);
            long sequence = 0;
            long interval = Stopwatch.Frequency / 15;
            long next = Stopwatch.GetTimestamp();
            while (!_stop.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    int waitMs = Math.Clamp((int)((next - now) * 1000 / Stopwatch.Frequency), 1, 20);
                    if (_stop.Token.WaitHandle.WaitOne(waitMs)) break;
                    continue;
                }
                next += ((now - next) / interval + 1) * interval;
                sequence++;
                if (!gateway.HasViewers(cameraId)) continue;

                graphics.Clear(background);
                for (int x = 0; x < 1280; x += 80) graphics.DrawLine(line, x, 0, x, 720);
                for (int y = 0; y < 720; y += 80) graphics.DrawLine(line, 0, y, 1280, y);
                int position = (int)(sequence * 13 % 1120);
                graphics.FillRectangle(Brushes.White, position, 240, 160, 240);
                graphics.FillEllipse(Brushes.Orange, 1150 - position, 140, 100, 100);
                graphics.DrawRectangle(outline, 128, 72, 1024, 576);
                graphics.DrawString($"{cameraId}  frame {sequence}  synthetic 15 FPS", font, Brushes.White, 24, 20);
                gateway.PushFrame(cameraId, image, sequence, now);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join();
            _stop.Dispose();
        }
    }
}
