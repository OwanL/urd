namespace Urd.Core.Context;

// Freshness of the paths the instruction-resource catalog serves for one working tree.
internal enum InstructionResourceIndexPhase {
  // A settled snapshot reconciled with the tree, covered by a live watcher when one exists.
  Complete,
  // Last-known rows are being reconciled with the tree in the background.
  Refreshing,
  // A cold scan has not published its full snapshot; paths show the matches found so far.
  Partial,
}

// How fresh the served paths are, with MatchedFiles counting the files known so far for the tree.
// Complete is the zero value: an untouched state reads as a settled empty index.
internal readonly record struct InstructionResourceIndexState(InstructionResourceIndexPhase Phase, int MatchedFiles);
