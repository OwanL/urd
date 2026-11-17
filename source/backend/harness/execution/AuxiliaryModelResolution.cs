using Urd.Core.Providers;

namespace Urd.Core.Execution;

// The selection frozen for one auxiliary conversation plus whether it is the run's own chat selection.
internal sealed record AuxiliaryModelResolution(LlmSelection Selection, bool UsedSessionModel);
