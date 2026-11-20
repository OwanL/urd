// Run from source/ui: node tests/support/TranscriptBaseline.ts
// Build Vite, then the Release server first. Not part of any test suite.
import { runTranscriptBaseline } from "./transcript-baseline/TranscriptBaselineRun.ts";

await runTranscriptBaseline();
