# HalconTrainingPlatform

## Scope

- This is an independent Windows x64 / .NET 8 WPF program in the umbrella `D:\AiProjects` workspace.
- Keep training-platform implementation in this directory. The only planned integration with `HalconVisionPlatform` is the versioned `.hdl` model-package contract and separately validated inference nodes.
- Do not commit HALCON native/runtime binaries, license files, real image data, datasets, trained models, logs, or generated reports.

## Architecture

- `Domain` is framework-independent. It must not reference WPF, HALCON, Persistence, Worker, or App.
- `Application` owns use cases, orchestration contracts, and versioned worker protocol DTOs. It must not reference WPF, HALCON, Persistence, Worker, or App.
- `Halcon` implements Application contracts and owns HALCON algorithm/model handles. Do not add UI types or display calls to algorithm modules.
- `Persistence` implements Application storage contracts. All paths are caller-provided or project-root-relative; do not hardcode machine-specific output paths.
- `Worker` is a one-job process boundary for GPU/HALCON training work. Use structured messages, cooperative cancellation where supported, explicit errors, and process exit for complete resource reclamation.
- `App` is WPF + MVVM. Views bind to ViewModels; ViewModels call Application interfaces. HALCON display integration lives in App presentation adapters using `HSmartWindowControlWPF`.
- HALCON `HImage`, `HRegion`, `HDlModel`, and `HDlDevice` handles never cross process boundaries or enter serialized Domain models.

## HALCON conventions

- Perform a flow and data-lifecycle analysis before adding an algorithm.
- HALCON performs image algorithms; WPF displays results through `HSmartWindowControlWPF`.
- Do not use `open_window`, `disp_obj`, or HDevelop `dev_display_*` operators in runtime code.
- Algorithm and post-processing parameters are configurable and persisted with the experiment.
- Support batch inputs with per-image structured results, logging, error isolation, and cancellation only where the HALCON operation permits it.
- Report operator, version, device, license, and task validation separately. HALCON runtime loading or DL-device enumeration alone does not prove training support.
- Dispose model, image, region, dictionary, tuple, and device handles promptly. If HALCON handle cleanup is not safe or bounded for a worker operation, keep it inside a short-lived worker and document the evidence.

## Build and validation

```powershell
dotnet build .\src\HalconTraining.App\HalconTraining.App.csproj -c Release
dotnet run --project .\src\HalconTraining.Worker\HalconTraining.Worker.csproj -c Release -- --environment-probe
dotnet run --project .\src\HalconTraining.App\HalconTraining.App.csproj -c Release
```

Use the project's HALCON 24.11 package `24111.0.0`, .NET 8, and x64. The current machine requires network access to restore NuGet audit metadata; disabling audit for an offline cached-package build is a local workaround, not a completed dependency-security review.

## UI acceptance standard

- Highest priority: 简洁、有效、清晰, with functional, visual and interaction quality together. Follow `docs/UI_SIMPLICITY.md` and `docs/UI_UX_STANDARD.md`. Plan layout before coding; record seven-dimension screenshot review, concrete issues and regression evidence in `docs/UI_VISUAL_REVIEW.md`. Do not equate compilation with visual acceptance.
- Exactly three primary pages: 数据集、训练、模型. Put the current task and truthful environment status in the top status bar.
- One primary task per screen and one primary action per area. Center dataset browsing and testing on large native image displays.
- Before training keep image size, Batch, Epoch and learning rate directly visible and editable; keep split ratios/counts and base model visible. During training lock all parameter editing; restore editors on stop/completion.
- Before training emphasize parameters; during training automatically collapse parameters and emphasize progress/curves; on completion offer the model/test step.
- Advanced parameters, logs and detailed metrics start collapsed. Explain blocked operations and invalid input immediately.
- White/light gray, one blue accent, necessary status colors only. No decorative gradients, shadows, icons or card collections.
- Functional controls use shared outline icons (`Views/ActionIcon`, `ActionVisual`); navigation, primary operations and section headers retain necessary labels. File-management actions remain icons with hints. Names, parameter labels/values and results remain readable text. Do not hide data as though it were an action or sacrifice comprehension for minimalism.
- Chinese functional hints wait 1500 ms on every control, including transitions to adjacent controls, disabled controls, generated controls and owned windows. Global `BetweenShowDelay=0`; retain accessible names, keyboard navigation/shortcuts and selected/disabled states. Apply expander hints to the header, not its editable content.
- One visible entry per operation and object: no duplicate split refresh/export/navigation buttons. Keep the three page headers as the navigation entry, with Ctrl+1/2/3 as keyboard alternatives.
- UI changes require native checks at 1120×780 and 1380×920, long names/paths, expanded low-frequency regions, panel widths and mode changes. Inspect real display and tooltip timing; a successful build alone is insufficient. Keep the stable title and desktop launcher; only deploy checked candidates after active work exits.
- UI delivery images must be fresh captures of the running target window, with no other app/window mixed in. Prefer usable mobile image attachments; otherwise commit the inspected capture under `docs/screenshots/`, push, verify the remote image, and provide a SHA-pinned mobile URL. State whether the image shows an editor or real execution results. Never substitute a mockup/generated or old image.
- Validate the first-use flow: select dataset → start training → view result → test original images, including empty/error/running/stopped states.
- Detection training includes every active image regardless of review status. Use the current saved/manual draft layer first (including an intentionally empty draft), otherwise use visible prediction candidates. No boxes means a negative sample automatically; do not add a negative-marking requirement or automatic prelabelling step. Preserve review states and exclude explicitly removed images. Combined training includes background-only folders as negative images without creating empty object categories. At least five images and positive object examples are needed; retain train/validation/test isolation and provenance of manual/predicted/negative labels.
- Folder-based annotation, single-class training and prediction remain isolated per child folder under `models/detection/<class>/`. Separate eligible folders may train concurrently; final HALCON training combines all active images into one multi-class `.hdict` and model. Current unsaved frame edits are saved as a draft before training/HDICT export, never silently approved.
- Folder names select the drawing class; they never supply automatic boxes or approval. Preserve human-approved labels, saved drafts, failed inputs and other folders when merging predictions.
- Detection folder training can select multiple independently eligible classes with a concurrency limit of 1 or 2. Snapshot each class before dispatch, retain separate worker progress/logs/models, serialize model-map registration, isolate job failures, and cancel queued as well as running work on stop. Do not claim HALCON concurrent GPU support from a YOLO-only validation.
