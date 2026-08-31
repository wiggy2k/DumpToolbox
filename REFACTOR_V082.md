# v0.8.2 EdgeRecoveryService modularisation

This release continues the structural cleanup from the confirmed-compiling v0.8.0 and v0.8.1 refactor checkpoints. v0.7.99 remains the pre-refactor rollback baseline.

## Boundaries

`EdgeRecoveryService` remains one `public sealed partial class`; its fields, private helpers and public API retain class-level visibility while implementation is separated by responsibility.

- **EdgeRecoveryService.cs** — public entry points, orchestration, target/anchor decisions and CUE-aware audio-edge flow.
- **EdgeRecoveryService.SingleAudio.cs** — single-audio extent inference, zero-padding recovery and padding-plus-shift search-source construction.
- **EdgeRecoveryService.SilenceShift.cs** — leading/trailing silence shift logic and proven all-zero overage trimming.
- **EdgeRecoveryService.IOAndVerification.cs** — generic edge repair, partial variants, streaming copy/zero writers, hash verification and output-path helpers.

## Dead-code audit

The source-tree private-method reference audit was rerun after the split. Avalonia handlers remain exempt because XAML references are not represented as C# call sites. `SkeletonResurrectionService.RawPhysicalOffset` was the only additional core private method with no source-tree call site and was removed.

## Behaviour policy

No FindCRCs edge-recovery condition, silence-shift rule, zero-padding rule, target ordering, output naming, hash-verification rule, or cancellation/error handling is intentionally changed by this release.
