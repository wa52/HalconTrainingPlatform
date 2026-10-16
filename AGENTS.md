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
dotnet build .\HalconTrainingPlatform.sln -c Release
dotnet run --project .\src\HalconTraining.Worker\HalconTraining.Worker.csproj -c Release -- --environment-probe
dotnet run --project .\src\HalconTraining.App\HalconTraining.App.csproj -c Release
```

Use the project's HALCON 24.11 package `24111.0.0`, .NET 8, and x64. The current machine requires network access to restore NuGet audit metadata; disabling audit for an offline cached-package build is a local workaround, not a completed dependency-security review.
