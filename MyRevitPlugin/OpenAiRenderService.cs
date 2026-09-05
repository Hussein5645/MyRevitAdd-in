using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace MyRevitPlugin;

internal static class OpenAiRenderService
{
    private const string Model = "gpt-image-2";
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/images/edits");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task<string> RenderAsync(string sourcePath, IRenderRequest request, CancellationToken cancellationToken)
    {
        string apiKey = OpenAiCredentialStore.ReadKey()
            ?? throw new RenderServiceException("The rendering service is not connected. Open Render Settings and save the service key first.");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent(BuildPrompt(request)), "prompt");
        form.Add(new StringContent(DetermineOutputSize(sourcePath, request.PixelSize)), "size");
        form.Add(new StringContent(DetermineQuality(request.PixelSize)), "quality");
        form.Add(new StringContent("png"), "output_format");
        form.Add(new StringContent("opaque"), "background");

        AddImage(form, sourcePath);
        foreach (string referencePath in request.ReferenceImagePaths)
            AddImage(form, referencePath);
        if (request is IMaskedRenderRequest { MaskPath: not null } masked && File.Exists(masked.MaskPath))
            AddMask(form, masked.MaskPath);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = form;
        using HttpResponseMessage response = await Client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response.StatusCode, responseBody, response.Headers.TryGetValues("x-request-id", out var values) ? string.Join(",", values) : null);

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement data = document.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0 || !data[0].TryGetProperty("b64_json", out JsonElement encodedImage))
            throw new RenderServiceException("The render service completed the request but did not return an image. Try a simpler brief or source view.");

        string? imageData = encodedImage.GetString();
        if (string.IsNullOrWhiteSpace(imageData))
            throw new RenderServiceException("The render service returned an empty image response.");

        string outputPath = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            $"ai-render-openai-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        await File.WriteAllBytesAsync(outputPath, Convert.FromBase64String(imageData), cancellationToken);
        return outputPath;
    }

    private static string BuildPrompt(IRenderRequest request)
    {
        string fidelityInstruction = request.Fidelity.StartsWith("Strict", StringComparison.Ordinal)
            ? "Preserve the exact building geometry, openings, proportions, camera position, perspective, and composition. Do not add, remove, move, or reshape architectural elements."
            : request.Fidelity.StartsWith("Balanced", StringComparison.Ordinal)
                ? "Preserve the primary architecture, openings, massing, and camera while improving materials, lighting, landscaping, entourage, and atmosphere."
                : "Keep the recognizable architectural concept and camera, but allow expressive materials, landscaping, lighting, and atmosphere.";

        string referenceInstruction = request.ReferenceImagePaths.Count == 0
            ? string.Empty
            : $"The first supplied image is the authoritative Revit viewport. The following {request.ReferenceImagePaths.Count} image(s) are visual references only; borrow their material language, lighting, landscaping, and mood without copying their geometry. ";
        bool hasMask = request is IMaskedRenderRequest { MaskPath: not null } maskedRequest && File.Exists(maskedRequest.MaskPath);
        string editGuidance = request is IMaskedRenderRequest masked && !string.IsNullOrWhiteSpace(masked.EditGuidance)
            ? $"Apply these precise edit notes: {masked.EditGuidance} "
            : string.Empty;
        string maskInstruction = hasMask
            ? "This is a local masked edit. Change only the transparent masked region; preserve the image outside the mask exactly, including geometry, composition, lighting, and materials. Reconstruct removed content from the surrounding architecture and context. Return a fully opaque, finished image—never leave a black, transparent, blank, or empty rectangle. "
            : string.Empty;
        return $"Transform the supplied Revit viewport into a polished {request.RenderStyle} visualization. {referenceInstruction}{fidelityInstruction} {maskInstruction}{editGuidance}" +
               "Treat the first image's visible lines, edges, masses, and proportions as authoritative design geometry. Produce a coherent professional architectural render without labels, borders, application UI, or added text. " +
               $"User brief: {request.Prompt}";
    }

    private static void AddImage(MultipartFormDataContent form, string imagePath)
    {
        var imageContent = new StreamContent(File.OpenRead(imagePath));
        string extension = Path.GetExtension(imagePath).ToLowerInvariant();
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        });
        form.Add(imageContent, "image[]", Path.GetFileName(imagePath));
    }

    private static void AddMask(MultipartFormDataContent form, string maskPath)
    {
        var maskContent = new StreamContent(File.OpenRead(maskPath));
        maskContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(maskContent, "mask", Path.GetFileName(maskPath));
    }

    private static string DetermineOutputSize(string imagePath, int requestedPixels)
    {
        var decoder = BitmapDecoder.Create(new Uri(imagePath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int sourceWidth = decoder.Frames[0].PixelWidth;
        int sourceHeight = decoder.Frames[0].PixelHeight;
        double ratio = Math.Clamp((double)sourceWidth / sourceHeight, 1d / 3d, 3d);
        int longEdge = requestedPixels switch { >= 4096 => 3840, >= 2048 => 2048, _ => 1280 };

        int width;
        int height;
        if (ratio >= 1)
        {
            width = longEdge;
            height = (int)Math.Round(longEdge / ratio);
        }
        else
        {
            height = longEdge;
            width = (int)Math.Round(longEdge * ratio);
        }

        width = RoundToMultipleOf16(width);
        height = RoundToMultipleOf16(height);
        const double minimumPixels = 655_360;
        const double maximumPixels = 8_294_400;
        double pixels = (double)width * height;
        if (pixels < minimumPixels)
        {
            double scale = Math.Sqrt(minimumPixels / pixels);
            width = RoundToMultipleOf16((int)Math.Ceiling(width * scale));
            height = RoundToMultipleOf16((int)Math.Ceiling(height * scale));
        }
        else if (pixels > maximumPixels)
        {
            double scale = Math.Sqrt(maximumPixels / pixels);
            width = RoundToMultipleOf16((int)Math.Floor(width * scale));
            height = RoundToMultipleOf16((int)Math.Floor(height * scale));
        }
        return $"{width}x{height}";
    }

    private static int RoundToMultipleOf16(int value) => Math.Max(16, (int)Math.Round(value / 16d) * 16);
    private static string DetermineQuality(int requestedPixels) => requestedPixels switch { >= 4096 => "high", >= 2048 => "medium", _ => "low" };

    private static RenderServiceException CreateApiException(HttpStatusCode statusCode, string responseBody, string? requestId)
    {
        string detail = "The render service rejected the request.";
        try
        {
            using JsonDocument error = JsonDocument.Parse(responseBody);
            detail = error.RootElement.GetProperty("error").GetProperty("message").GetString() ?? detail;
        }
        catch (JsonException) { }

        string friendly = statusCode switch
        {
            HttpStatusCode.BadRequest => "The render service could not process this image or configuration.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The rendering connection was rejected. Open Settings and update the service key.",
            HttpStatusCode.TooManyRequests => "The rendering usage or spending limit was reached. Check the service account’s usage and billing.",
            HttpStatusCode.ServiceUnavailable => "The rendering service is temporarily unavailable. Please try again shortly.",
            _ when (int)statusCode >= 500 => "The rendering service encountered a temporary server error. Please try again.",
            _ => $"The rendering service returned HTTP {(int)statusCode}."
        };
        string reference = string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" Request ID: {requestId}.";
        return new RenderServiceException($"{friendly} {detail}{reference}");
    }
}

internal sealed class RenderServiceException : Exception
{
    public RenderServiceException(string message) : base(message) { }
}

internal static class OpenAiCredentialStore
{
    private const string TargetName = "MyRevitPlugin/OpenAI";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;

    public static string? ReadKey()
    {
        if (!CredRead(TargetName, GenericCredential, 0, out IntPtr credentialPointer))
            return null;
        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            return credential.CredentialBlob == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static void SaveKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Enter a rendering service key.", nameof(apiKey));

        byte[] secret = Encoding.Unicode.GetBytes(apiKey.Trim());
        IntPtr secretPointer = Marshal.AllocCoTaskMem(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, secretPointer, secret.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = TargetName,
                CredentialBlobSize = checked((uint)secret.Length),
                CredentialBlob = secretPointer,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Windows could not save the credential (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Array.Clear(secret);
            Marshal.FreeCoTaskMem(secretPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
