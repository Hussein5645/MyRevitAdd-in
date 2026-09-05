
# AI Render for Revit — Prototype

## Overview

This is a Revit 2026 add-in for an AI-assisted architectural rendering workflow. It exports the active printable view, sends it to OpenAI for GPT Image editing, and presents the result inside Revit.

## Features

- **AI Render ribbon:** Adds an `AI Render` panel with an `AI Render Active View` command.
- **View export:** Exports the current printable Revit view as a 2048px PNG.
- **Render brief:** Captures an architectural style and free-text prompt.
- **Job output:** Saves source image, output image, and render-request text under `Pictures\MyRevitPlugin\RenderJobs`.
- **OpenAI rendering:** Uses GPT Image 2 through the OpenAI Image API.
- **Secure credentials:** Saves the API key in Windows Credential Manager, never in source code or render logs.
- **Render feedback:** Shows capture, request, rendering, and completion states with cancellation and friendly error details.
- **Diagnostic logs:** Writes job IDs and sanitized diagnostics to `%LocalAppData%\MyRevitPlugin\Logs\ai-render.log`.
- **Reference images:** Attach up to eight PNG, JPEG, or WebP references for materials, lighting, landscape, and mood. The Revit viewport is submitted first as authoritative geometry.
- **Project-aware live gallery:** Saves renders under a Revit project-ID folder and opens on the current project by default. Switch to all projects or any specific saved project. Legacy flat images remain available under All projects.
- **Gallery-to-studio editing:** Right-click a saved render to reopen it in Render Studio, change the brief or references, and render another version.
- **Iterative edits:** Continue editing the latest AI result without saving it first. Studio clearly warns that an unsaved intermediate version will not be kept in Gallery.
- **Single-window studio:** Configuration, exact outbound Revit capture, rendering status, errors, and the final image remain in one window.
- **Modeless Revit workflow:** Render Studio stays open without locking Revit. Orbit, zoom, or change the active view, then use **Sync Revit viewport** to capture it again.
- **Visual edit tools:** Add numbered comments directly on the image, edit their text, and create multiple replace/remove areas. Areas run one at a time for tighter local control, and removals reject black-fill failures.
- **Provider-neutral UI:** The ribbon, studio, settings, progress, and error messages present one integrated AI Render product without external provider branding.
- **Visible viewport capture:** The default capture temporarily matches the active Revit zoom extents, exports the image, and rolls the model back unchanged.

## Getting Started

### Prerequisites

- **Revit 2026:** The project references `H:\APPs\Revit26\Revit 2026`.
- **.NET 8 SDK:** Required for the Revit 2026 add-in and installer projects.
- **Visual Studio:** Recommended for developing and building your Revit add-in.

### Installation

1. Build the solution:

   ```powershell
   dotnet build MyRevitPlugin.sln --configuration Release
   ```

2. Run `Runaddin` from its build output, select **2026**, and click **Install Plugin**.

3. Restart Revit 2026, open a printable view, and choose **AI Render → AI Render Active View**.

## OpenAI

Open **AI Render → AI Render Settings** to save or replace the OpenAI API key. Rendering requires an OpenAI API project with billing and access to `gpt-image-2`.
