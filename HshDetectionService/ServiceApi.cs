using System.Text.Json;
using System.Drawing;
using HshDetectionEngin;
using HshDetectionEngin.Face;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;

namespace HshDetectionService;

public static class ServiceApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", utc = DateTime.UtcNow }));
        app.MapGet("/health/ready", (DetectionRuntimeHost host) => host.IsReady && host.License.IsValid
            ? Results.Ok(new { status = "ready", license = host.License.Message })
            : Results.Json(new { status = "not_ready", error = host.ReadinessError ?? host.License.Message }, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/api/v1/service/status", (DetectionRuntimeHost host, ServiceSettingsStore store) => Results.Ok(new
        {
            ready = host.IsReady && host.License.IsValid,
            revision = store.Service.Revision,
            serviceNodeId = store.Service.ServiceNodeId,
            license = new { host.License.IsValid, host.License.Message, features = host.License.Claims?.Features.ToString() },
            cameras = host.GetCameraStatuses(),
            eventSequence = host.Events.CurrentSequence()
        }));

        app.MapGet("/api/v1/service/capabilities", (DetectionRuntimeHost host) => Results.Ok(host.ProcessingModules.Modules.Select(module => new
        {
            type = module.Type.Value,
            displayName = module.DisplayName,
            kind = module.Kind.ToString(),
            optionsType = module.OptionsType?.FullName,
            editorKey = module.EditorKey,
            available = string.IsNullOrWhiteSpace(module.AvailabilityMessage),
            availabilityMessage = module.AvailabilityMessage
        })));
        app.MapGet("/api/v1/service/models", () =>
        {
            // Keep the web catalog in sync with CameraSettingsForm. Models can live
            // beside the executable, in the legacy Modules folder, or in the
            // project module folders while running from a Debug output directory.
            var files = EnumerateModelFiles("Plate", "HshDetectionEngin.Plate")
                .Select(path => new { path, module = "Plate" })
                .Concat(EnumerateModelFiles("Face", "HshDetectionEngin.Face")
                    .Select(path => new { path, module = "Face" }));

            var models = files
                .Select(item =>
                {
                    string name = Path.ChangeExtension(Path.GetFileName(item.path), ".onnx");
                    string capability = item.module == "Plate"
                        ? "Plate"
                        : name.Contains("sface", StringComparison.OrdinalIgnoreCase)
                            ? "FaceRecognition"
                            : name.Contains("yunet", StringComparison.OrdinalIgnoreCase)
                                ? "FaceDetection"
                                : "Face";
                    return new
                    {
                        name,
                        relativePath = Path.GetRelativePath(AppContext.BaseDirectory, item.path).Replace('\\', '/'),
                        module = item.module,
                        capability,
                        packaged = true
                    };
                })
                .GroupBy(item => $"{item.module}:{item.name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.module)
                .ThenBy(item => item.name)
                .ToArray();
            return Results.Ok(models);
        });
        app.MapPost("/api/v1/service/reload", (DetectionRuntimeHost host, ServiceSettingsStore store) =>
        {
            host.ApplyDetectionSettings(store.Detection, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(new ServiceOperationResult(true, "Runtime reloaded.", store.Service.Revision));
        });

        app.MapGet("/api/v1/settings", (ServiceSettingsStore store) => Results.Ok(new
        {
            revision = store.Service.Revision,
            detection = store.Detection,
            service = store.Service
        }));

        app.MapPut("/api/v1/settings", (ConfigurationUpdateRequest request, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            try
            {
                long expectedRevision = request.Revision;
                if (request.Detection is not null) store.SaveDetection(request.Detection, expectedRevision);
                if (request.Service is not null) store.SaveService(request.Service, request.Detection is null ? expectedRevision : store.Service.Revision);
                host.ApplyDetectionSettings(store.Detection, store.Service.Runtime.AutoStartCameras);
                return Results.Ok(new ServiceOperationResult(true, "Settings applied.", store.Service.Revision));
            }
            catch (ConfigurationConflictException ex)
            {
                return Results.Conflict(new { code = "configuration_conflict", expectedRevision = ex.ExpectedRevision, actualRevision = ex.ActualRevision });
            }
        });

        app.MapPost("/api/v1/settings/validate", (AppSettings settings, DetectionRuntimeHost host) =>
        {
            var errors = new List<string>();
            foreach (CameraSettings camera in settings.Cameras ?? [])
            {
                camera.EnsureProcessingDefaults();
                if (string.IsNullOrWhiteSpace(camera.Id)) errors.Add("Camera id is required.");
                if (string.IsNullOrWhiteSpace(camera.SourceUrl)) errors.Add($"Camera '{camera.Name}' source is empty.");
                foreach (NamedRoi roi in camera.Rois)
                    if (roi.Points.Count is > 0 and < 3) errors.Add($"Camera '{camera.Name}' ROI '{roi.Name}' has fewer than three points.");
            }
            return Results.Ok(new { valid = errors.Count == 0, errors, modules = host.ProcessingModules.Descriptors });
        });

        app.MapGet("/api/v1/cameras", (DetectionRuntimeHost host) => Results.Ok(host.GetCameraStatuses()));
        app.MapPost("/api/v1/cameras", (CameraSettings settings, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            settings.Id = string.IsNullOrWhiteSpace(settings.Id) ? Guid.NewGuid().ToString("N") : settings.Id;
            AppSettings all = store.Detection;
            all.Cameras.RemoveAll(camera => camera.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase));
            all.Cameras.Add(settings);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Created($"/api/v1/cameras/{settings.Id}", settings);
        });

        app.MapGet("/api/v1/cameras/{cameraId}", (string cameraId, ServiceSettingsStore store) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            return camera is null ? Results.NotFound() : Results.Ok(camera);
        });

        app.MapPut("/api/v1/cameras/{cameraId}", (string cameraId, CameraSettings settings, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            if (!cameraId.Equals(settings.Id, StringComparison.OrdinalIgnoreCase)) settings.Id = cameraId;
            AppSettings all = store.Detection;
            if (!all.Cameras.Any(camera => camera.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))) return Results.NotFound();
            all.Cameras.RemoveAll(camera => camera.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            all.Cameras.Add(settings);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(settings);
        });

        app.MapDelete("/api/v1/cameras/{cameraId}", (string cameraId, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            if (all.Cameras.RemoveAll(camera => camera.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/cameras/{cameraId}/start", (string cameraId, DetectionRuntimeHost host) => host.StartCamera(cameraId) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/v1/cameras/{cameraId}/stop", (string cameraId, DetectionRuntimeHost host) => host.StopCamera(cameraId) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/v1/cameras/{cameraId}/restart", (string cameraId, DetectionRuntimeHost host) =>
        {
            if (!host.StopCamera(cameraId) || !host.StartCamera(cameraId)) return Results.NotFound();
            return Results.Ok();
        });

        app.MapGet("/api/v1/cameras/{cameraId}/rois", (string cameraId, ServiceSettingsStore store) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            return camera is null ? Results.NotFound() : Results.Ok(camera.Rois);
        });

        app.MapGet("/api/v1/cameras/{cameraId}/tasks", (string cameraId, ServiceSettingsStore store) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            return camera is null ? Results.NotFound() : Results.Ok(camera.Rois.SelectMany(roi => roi.Processing.Select(item => new { roi.Name, RoiId = roi.Id, Task = item })));
        });

        app.MapPost("/api/v1/cameras/{cameraId}/rois", (string cameraId, NamedRoi roi, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            if (camera is null) return Results.NotFound();
            roi.Id = string.IsNullOrWhiteSpace(roi.Id) ? Guid.NewGuid().ToString("N") : roi.Id;
            camera.Rois.Add(roi);
            AppSettings all = store.Detection;
            all.Cameras.RemoveAll(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            all.Cameras.Add(camera);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Created($"/api/v1/cameras/{cameraId}/rois/{roi.Id}", roi);
        });

        app.MapGet("/api/v1/cameras/{cameraId}/rois/{roiId}", (string cameraId, string roiId, ServiceSettingsStore store) =>
        {
            NamedRoi? roi = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
                .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            return roi is null ? Results.NotFound() : Results.Ok(roi);
        });

        app.MapPut("/api/v1/cameras/{cameraId}/rois/{roiId}", (string cameraId, string roiId, NamedRoi roi, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            CameraSettings? camera = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            NamedRoi? existing = camera?.Rois.FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            if (camera is null || existing is null) return Results.NotFound();
            roi.Id = roiId;
            camera.Rois.Remove(existing);
            camera.Rois.Add(roi);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(roi);
        });

        app.MapDelete("/api/v1/cameras/{cameraId}/rois/{roiId}", (string cameraId, string roiId, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            CameraSettings? camera = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            if (camera is null || camera.Rois.RemoveAll(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/cameras/{cameraId}/rois/{roiId}/tasks", (string cameraId, string roiId, CameraProcessingSettings task, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            NamedRoi? roi = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
                .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            if (roi is null) return Results.NotFound();
            task.Id = string.IsNullOrWhiteSpace(task.Id) ? Guid.NewGuid().ToString("N") : task.Id;
            roi.Processing.Add(task);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Created($"/api/v1/cameras/{cameraId}/rois/{roiId}/tasks/{task.Id}", task);
        });

        app.MapPut("/api/v1/cameras/{cameraId}/rois/{roiId}/tasks/{taskId}", (string cameraId, string roiId, string taskId, CameraProcessingSettings task, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            CameraProcessingSettings? existing = all.DetectionTask(cameraId, roiId, taskId);
            if (existing is null) return Results.NotFound();
            task.Id = taskId;
            existing.Type = task.Type; existing.Name = task.Name; existing.Enabled = task.Enabled;
            existing.MaxFps = task.MaxFps; existing.Threads = task.Threads; existing.Options = task.Options;
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(existing);
        });

        app.MapDelete("/api/v1/cameras/{cameraId}/rois/{roiId}/tasks/{taskId}", (string cameraId, string roiId, string taskId, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            NamedRoi? roi = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
                .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            if (roi is null || roi.Processing.RemoveAll(item => item.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.NoContent();
        });

        app.MapGet("/api/v1/events", (long? afterSequence, int? limit, string? cameraId, string? scenario, DateTime? fromUtc, DateTime? toUtc, DetectionRuntimeHost host) =>
        {
            IReadOnlyList<DetectionEventEnvelope> events = host.Events.ReadAfter(afterSequence ?? 0, Math.Clamp(limit ?? 200, 1, 2000));
            IEnumerable<DetectionEventEnvelope> filtered = events;
            if (!string.IsNullOrWhiteSpace(cameraId))
                filtered = filtered.Where(item => string.Equals(item.Source["cameraId"]?.GetValue<string>(), cameraId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(scenario))
                filtered = filtered.Where(item => string.Equals(item.Scenario, scenario, StringComparison.OrdinalIgnoreCase));
            if (fromUtc is not null) filtered = filtered.Where(item => item.OccurredAtUtc >= fromUtc.Value.ToUniversalTime());
            if (toUtc is not null) filtered = filtered.Where(item => item.OccurredAtUtc <= toUtc.Value.ToUniversalTime());
            return Results.Ok(filtered.ToArray());
        });
        app.MapGet("/api/v1/events/{eventId}", (string eventId, DetectionRuntimeHost host) =>
        {
            DetectionEventEnvelope? item = host.Events.Get(eventId);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        app.MapGet("/api/v1/events/{eventId}/artifacts", (string eventId, DetectionRuntimeHost host) =>
        {
            DetectionEventEnvelope? item = host.Events.Get(eventId);
            return item is null ? Results.NotFound() : Results.Ok(item.Artifacts);
        });

        app.MapGet("/api/v1/events/{eventId}/artifacts/{artifactId}", (string eventId, string artifactId, DetectionRuntimeHost host) =>
        {
            DetectionEventEnvelope? item = host.Events.Get(eventId);
            EventArtifactDescriptor? artifact = item?.Artifacts.FirstOrDefault(value => value.ArtifactId.Equals(artifactId, StringComparison.OrdinalIgnoreCase));
            if (artifact is null) return Results.NotFound();
            string path = host.Artifacts.Resolve(artifact);
            return File.Exists(path) ? Results.File(path, artifact.ContentType) : Results.NotFound();
        });

        app.MapGet("/api/v1/streams/{cameraId}/snapshot", (string cameraId, DetectionRuntimeHost host) =>
        {
            using Bitmap? frame = host.GetLatestFrame(cameraId);
            if (frame is null) return Results.NotFound();
            using var stream = new MemoryStream();
            frame.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
            return Results.File(stream.ToArray(), "image/jpeg");
        });

        app.MapGet("/api/v1/streams/{cameraId}/overlay", (string cameraId, DetectionRuntimeHost host) =>
        {
            if (!host.TryGetCamera(cameraId, out Camera? camera) || camera is null)
                return Results.NotFound();
            return Results.Ok(camera.GetLiveOverlaySnapshot());
        });

        app.MapPost("/api/v1/streams/{cameraId}/webrtc/offer", async (string cameraId, WebRtcOfferRequest request, DetectionRuntimeHost host, WebRtcGateway gateway, CancellationToken cancellationToken) =>
        {
            if (!host.TryGetCamera(cameraId, out _)) return Results.NotFound(new { error = "Camera was not found." });
            try
            {
                WebRtcOfferResult answer = await gateway.AcceptOfferAsync(cameraId, request.Sdp, cancellationToken);
                return Results.Ok(answer);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/v1/streams/webrtc/{sessionId}", (string sessionId, WebRtcGateway gateway) =>
            gateway.Close(sessionId) ? Results.NoContent() : Results.NotFound());

        app.MapMethods("/api/v1/streams/{cameraId}/webrtc/whep/{viewerId}", ["OPTIONS", "POST", "PATCH", "DELETE"],
            (HttpContext context, string cameraId, string viewerId, ServiceSettingsStore store, MediaMtxWebRtcProxy proxy, CancellationToken cancellationToken)
                => proxy.HandleAsync(context, cameraId, viewerId, store, cancellationToken));

        app.MapGet("/api/v1/face/people", (DetectionRuntimeHost host) => Results.Ok(host.FaceDatabase.Identities));
        app.MapPost("/api/v1/face/people", (CreatePersonRequest request, DetectionRuntimeHost host) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });
            FaceIdentity person = host.FaceDatabase.CreatePerson(request.Name);
            return Results.Created($"/api/v1/face/people/{person.Id}", person);
        });
        app.MapGet("/api/v1/face/people/{personId}", (string personId, DetectionRuntimeHost host) =>
        {
            FaceIdentity? person = host.FaceDatabase.Identities.FirstOrDefault(item => item.Id == personId);
            return person is null ? Results.NotFound() : Results.Ok(person);
        });

        app.MapPatch("/api/v1/face/people/{personId}", (string personId, RenamePersonRequest request, DetectionRuntimeHost host) =>
            host.FaceDatabase.Rename(personId, request.Name) ? Results.Ok() : Results.NotFound());
        app.MapDelete("/api/v1/face/people/{personId}", (string personId, DetectionRuntimeHost host) =>
            host.FaceDatabase.Remove(personId) ? Results.NoContent() : Results.NotFound());
        app.MapGet("/api/v1/face/people/{personId}/samples", (string personId, DetectionRuntimeHost host) =>
            Results.Ok(host.FaceDatabase.GetSamples().Where(sample => sample.PersonId == personId)));
        app.MapGet("/api/v1/face/samples/{sampleId}", (string sampleId, DetectionRuntimeHost host) =>
        {
            FaceSample? sample = host.FaceDatabase.GetSamples().FirstOrDefault(item => item.Id == sampleId);
            return sample is null ? Results.NotFound() : Results.Ok(sample);
        });
        app.MapGet("/api/v1/face/samples/{sampleId}/image", (string sampleId, DetectionRuntimeHost host) =>
        {
            FaceSample? sample = host.FaceDatabase.GetSamples().FirstOrDefault(item => item.Id == sampleId);
            if (sample is null) return Results.NotFound();
            byte[] image = host.FaceDatabase.GetFaceImage(sampleId);
            return image.Length == 0 ? Results.NotFound() : Results.File(image, "image/jpeg");
        });
        app.MapDelete("/api/v1/face/samples/{sampleId}", (string sampleId, DetectionRuntimeHost host) =>
            host.FaceDatabase.RemoveSample(sampleId) ? Results.NoContent() : Results.NotFound());
        app.MapPost("/api/v1/face/samples/{sampleId}/move", (string sampleId, MoveSampleRequest request, DetectionRuntimeHost host) =>
        {
            try { return host.FaceDatabase.MoveSample(sampleId, request.TargetPersonId) ? Results.Ok() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/face/people/{targetPersonId}/merge/{sourcePersonId}", (string targetPersonId, string sourcePersonId, DetectionRuntimeHost host) =>
        {
            try { return host.FaceDatabase.MergePeople(targetPersonId, sourcePersonId) ? Results.Ok() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapGet("/api/v1/face/similar", (float? minimumSimilarity, bool? onlyDifferentPeople, DetectionRuntimeHost host) =>
            Results.Ok(host.FaceDatabase.FindSimilar(minimumSimilarity ?? 0.40f, onlyDifferentPeople ?? false)));
        app.MapGet("/api/v1/face/database/health", (DetectionRuntimeHost host) => Results.Ok(new
        {
            databasePath = host.FaceDatabase.DatabasePath,
            people = host.FaceDatabase.Identities.Count,
            samples = host.FaceDatabase.GetSamples().Count,
            sizeBytes = File.Exists(host.FaceDatabase.DatabasePath) ? new FileInfo(host.FaceDatabase.DatabasePath).Length : 0
        }));
        app.MapPost("/api/v1/face/database/backup", (DetectionRuntimeHost host) =>
        {
            string path = Path.Combine(host.Paths.BackupDirectory, $"face-database-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            host.FaceDatabase.Save(path);
            return Results.Ok(new { path, sizeBytes = new FileInfo(path).Length });
        });

        app.MapPost("/api/v1/face/people/{personId}/samples", async (HttpRequest request, string personId, DetectionRuntimeHost host, CancellationToken cancellationToken) =>
        {
            IFormCollection form = await request.ReadFormAsync(cancellationToken);
            IFormFile? file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "An image file is required." });
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            FaceSample sample = await host.EnrollFaceSampleAsync(personId, null, stream.ToArray(), file.FileName, cancellationToken);
            return Results.Ok(sample);
        });

        app.MapGet("/api/v1/triggers", (ServiceSettingsStore store) => Results.Ok(new { revision = store.Service.Revision, items = store.Service.Triggers }));
        app.MapGet("/api/v1/triggers/{triggerId}", (string triggerId, ServiceSettingsStore store) =>
        {
            TriggerDefinition? trigger = store.Service.Triggers.FirstOrDefault(item => item.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase));
            return trigger is null ? Results.NotFound() : Results.Ok(trigger);
        });
        app.MapPost("/api/v1/triggers", (TriggerDefinition trigger, ServiceSettingsStore store) =>
        {
            ServiceSettingsDocument settings = store.Service;
            settings.Triggers.Add(trigger);
            store.SaveService(settings, settings.Revision);
            return Results.Created($"/api/v1/triggers/{trigger.Id}", trigger);
        });
        app.MapPatch("/api/v1/triggers/{triggerId}", (string triggerId, TriggerDefinition update, ServiceSettingsStore store) =>
        {
            ServiceSettingsDocument settings = store.Service;
            TriggerDefinition? current = settings.Triggers.FirstOrDefault(item => item.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase));
            if (current is null) return Results.NotFound();
            update.Id = triggerId;
            settings.Triggers.Remove(current);
            settings.Triggers.Add(update);
            store.SaveService(settings, settings.Revision);
            return Results.Ok(update);
        });
        app.MapDelete("/api/v1/triggers/{triggerId}", (string triggerId, ServiceSettingsStore store) =>
        {
            ServiceSettingsDocument settings = store.Service;
            if (settings.Triggers.RemoveAll(item => item.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveService(settings, settings.Revision);
            return Results.NoContent();
        });

        app.MapHub<DetectionHub>("/hubs/detections");
    }

    private static IEnumerable<string> EnumerateModelFiles(string capability, string projectDirectory)
    {
        var directories = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Models", capability),
            Path.Combine(AppContext.BaseDirectory, "Models"),
            Path.Combine(AppContext.BaseDirectory, "Modules", capability, "Models")
        };

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
        {
            directories.Add(Path.Combine(directory.FullName, projectDirectory, "Models"));
        }

        return directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.hshmodel", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record ConfigurationUpdateRequest(long Revision, AppSettings? Detection, ServiceSettingsDocument? Service);
public sealed record CreatePersonRequest(string Name);
public sealed record RenamePersonRequest(string Name);
public sealed record MoveSampleRequest(string TargetPersonId);
public sealed record WebRtcOfferRequest(string Type, string Sdp);

internal static class AppSettingsApiExtensions
{
    public static CameraProcessingSettings? DetectionTask(this AppSettings settings, string cameraId, string roiId, string taskId)
        => settings.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
            .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase))?.Processing
            .FirstOrDefault(item => item.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase));
}

public sealed class DetectionHub : Hub
{
    public const string LiveGroup = "detection-live";
    private static readonly SemaphoreSlim ReplayGate = new(1, 1);
    private readonly EventStore _events;

    public DetectionHub(EventStore events) => _events = events;

    public async Task Subscribe(long lastSequence = 0)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroup);
        await ReplayGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            long? oldest = _events.OldestSequence();
            if (oldest is long first && lastSequence < first - 1)
            {
                await Clients.Caller.SendAsync("cursorExpired", new { requestedAfter = lastSequence, availableFrom = first, requiresResync = true }, Context.ConnectionAborted);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroup);
                return;
            }

            long watermark = _events.CurrentSequence();
            await Clients.Caller.SendAsync("replayStarted", new { requestedAfter = lastSequence, watermark }, Context.ConnectionAborted);
            long cursor = lastSequence;
            while (cursor < watermark)
            {
                IReadOnlyList<DetectionEventEnvelope> batch = _events.ReadAfter(cursor, 500, watermark);
                if (batch.Count == 0) break;
                foreach (DetectionEventEnvelope item in batch)
                {
                    await Clients.Caller.SendAsync("detection", item, Context.ConnectionAborted);
                    cursor = item.Sequence;
                }
            }
            await Clients.Caller.SendAsync("replayCompleted", new { lastSequence = cursor, watermark }, Context.ConnectionAborted);
        }
        finally { ReplayGate.Release(); }
    }

    public static async Task PublishAsync(IHubContext<DetectionHub> hub, DetectionEventEnvelope item, CancellationToken cancellationToken)
    {
        await ReplayGate.WaitAsync(cancellationToken);
        try { await hub.Clients.Group(LiveGroup).SendAsync("detection", item, cancellationToken); }
        finally { ReplayGate.Release(); }
    }
}
